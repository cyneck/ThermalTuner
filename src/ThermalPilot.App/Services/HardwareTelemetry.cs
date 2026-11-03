using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace ThermalPilot.App.Services;

/// <summary>一次采样的遥测快照。</summary>
/// <param name="PowerMeasured">功耗是否实测（RAPL 能量计数器或 LHM 传感器）；false 表示按负载粗估。</param>
public readonly record struct TelemetrySample(
    double TempC,
    double LoadPercent,
    double PowerW,
    double MaxCoreGhz,
    bool PowerMeasured,
    string Source);

/// <summary>
/// 只读遥测源。三级回退，任何一级失败都不中断程序：
///   1. LibreHardwareMonitor 0.9.4（WinRing0 内核驱动，可读 CPU 封装温度 / 包功耗 / 核心频率，需管理员）
///   2. ACPI 热区 WMI（部分机型可用，粒度为整区）
///   3. GetSystemTimes 计算负载（始终可用）
/// 研发注意：本类与 src/ThermalPilot.Monitor/TelemetrySource.cs 逻辑同源，
/// 建议 M2 阶段抽取为 ThermalPilot.Core.Telemetry（见 PRD 附录 A）。
/// </summary>
public sealed class HardwareTelemetry : IDisposable
{
    private readonly Computer? _computer;
    private readonly IHardware? _cpu;
    private bool _driverOk;

    private RaplPowerMeter? _rapl;

    private bool _acpiOk = true;
    private bool _perfOk = true;
    private double _lastWmiTemp = double.NaN;

    private long _prevIdle, _prevKernel, _prevUser;
    private double _lastLoad;

    public HardwareTelemetry()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        }

        try
        {
            var computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            _cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _computer = computer;
            _driverOk = _cpu != null;
        }
        catch
        {
            _driverOk = false;
        }
    }

    public bool DriverAvailable => _driverOk;

    /// <summary>
    /// 挂载 RAPL 功耗计量。MSR 通道由 SessionState 统一持有，
    /// 遥测层只借用，不负责打开/关闭（避免重复句柄）。
    /// </summary>
    public void AttachRapl(RaplPowerMeter? meter) => _rapl = meter;

    /// <summary>RAPL 是否可用（诊断页展示）。</summary>
    public bool RaplAvailable => _rapl?.Available == true;
    public string RaplDetail => _rapl?.Detail ?? "未接入";

    /// <summary>传感器总数（诊断页用）。</summary>
    public int SensorCount => _cpu?.Sensors.Length ?? 0;

    /// <summary>诊断：列出 LHM 枚举到的全部硬件与传感器。</summary>
    public IEnumerable<string> DumpSensors()
    {
        if (_computer is null) { yield return "Computer 未初始化（驱动不可用）"; yield break; }

        if (_cpu is not null)
        {
            yield return $"CPU 传感器 {_cpu.Sensors.Length} 个";
            foreach (var s in _cpu.Sensors)
                yield return $"  {s.SensorType,-12} {s.Name,-26} = {s.Value?.ToString("F1") ?? "null"}";
        }
        else
        {
            yield return $"硬件数量 {_computer.Hardware.Count}（未找到 CPU）";
            foreach (var hw in _computer.Hardware)
                yield return $"[{hw.HardwareType}] {hw.Name}";
        }
    }

    public TelemetrySample Read()
    {
        // RAPL 每拍只读一次：能量计数器是累加量，多读会打乱 Δt
        double raplW = _rapl is not null && _rapl.TrySample(out double rw) ? rw : double.NaN;

        if (_driverOk && _cpu is not null)
        {
            try
            {
                _cpu.Update();
                foreach (var sub in _cpu.SubHardware) sub.Update();

                double temp = PickTemperature();
                double lhmPower = PickSensor(SensorType.Power, "Package");
                double load = PickSensor(SensorType.Load, "CPU Total");
                double clock = PickMaxClock();

                if (!double.IsNaN(temp))
                {
                    if (double.IsNaN(load)) load = ReadLoadFromSystemTimes();

                    // 优先级：LHM 传感器 > RAPL 实测 > 按负载估算
                    if (!double.IsNaN(lhmPower))
                        return new TelemetrySample(temp, load, lhmPower, clock, true, "LHM");

                    if (!double.IsNaN(raplW))
                        return new TelemetrySample(temp, load, raplW, clock, true, "RAPL");

                    return new TelemetrySample(temp, load, EstimatePower(load), clock, false, "LHM/估算");
                }
            }
            catch
            {
                _driverOk = false;
            }
        }

        // 回退：ACPI 温度 + 系统负载；功耗优先 RAPL，其次估算
        double t = ReadWmiTemperature();
        double l = ReadLoadFromSystemTimes();

        if (!double.IsNaN(raplW))
            return new TelemetrySample(t, l, raplW, 0, true, "RAPL");

        return new TelemetrySample(t, l, EstimatePower(l), 0, false,
            double.IsNaN(t) ? "SystemTimesOnly" : "WMI");
    }

    // ---------- 传感器挑选 ----------

    private double PickTemperature()
    {
        string[] prefer = { "CPU Package", "Core (Tctl/Tdie)", "CPU Core", "Core Max", "Core Average" };
        foreach (var name in prefer)
        {
            var s = _cpu!.Sensors.FirstOrDefault(x =>
                x.SensorType == SensorType.Temperature && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s?.Value is float v && v is > 5 and < 120) return v;
        }

        double best = double.NaN;
        foreach (var s in _cpu!.Sensors.Where(x => x.SensorType == SensorType.Temperature))
        {
            if (s.Value is float v && v is > 5 and < 120)
                best = double.IsNaN(best) ? v : Math.Max(best, v);
        }
        return best;
    }

    private double PickSensor(SensorType type, string contains)
    {
        var s = _cpu!.Sensors.FirstOrDefault(x =>
            x.SensorType == type && x.Name.Contains(contains, StringComparison.OrdinalIgnoreCase));
        return s?.Value ?? double.NaN;
    }

    private double PickMaxClock()
    {
        double best = double.NaN;
        foreach (var s in _cpu!.Sensors.Where(x => x.SensorType == SensorType.Clock))
        {
            if (s.Value is float v && v > 0)
                best = double.IsNaN(best) ? v : Math.Max(best, v);
        }
        // LHM 的 Clock 传感器单位是 MHz；MaxCoreGhz 与界面都按 GHz 消费，
        // 不换算会出现「3510.00 GHz」，且错值会进 ThermalSnapshot.CoreClockGhz。
        return double.IsNaN(best) ? best : best / 1000.0;
    }

    // ---------- 回退路径 ----------

    private double ReadWmiTemperature()
    {
        double t = ReadAcpiZone();
        if (double.IsNaN(t)) t = ReadPerfZone();
        if (!double.IsNaN(t)) _lastWmiTemp = t;
        return _lastWmiTemp;
    }

    private double ReadAcpiZone()
    {
        if (!_acpiOk) return double.NaN;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root/WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double best = double.NaN;
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    double c = Convert.ToDouble(mo["CurrentTemperature"]) / 10.0 - 273.15;
                    if (c is > 5 and < 120 && (double.IsNaN(best) || c > best)) best = c;
                }
            }
            if (!double.IsNaN(best)) return best;
            _acpiOk = false;
            return double.NaN;
        }
        catch
        {
            _acpiOk = false;
            return double.NaN;
        }
    }

    private double ReadPerfZone()
    {
        if (!_perfOk) return double.NaN;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root/CIMV2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            double best = double.NaN;
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    double c = Convert.ToDouble(mo["Temperature"]) - 273.15;
                    if (c is > 5 and < 120 && (double.IsNaN(best) || c > best)) best = c;
                }
            }
            if (!double.IsNaN(best)) return best;
            _perfOk = false;
            return double.NaN;
        }
        catch
        {
            _perfOk = false;
            return double.NaN;
        }
    }

    private double ReadLoadFromSystemTimes()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
            return _lastLoad;

        long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        long total = dKernel + dUser;
        if (total <= 0) return _lastLoad;

        _lastLoad = Math.Clamp((total - dIdle) * 100.0 / total, 0.0, 100.0);
        return _lastLoad;
    }

    private static double EstimatePower(double loadPct) =>
        double.IsNaN(loadPct) ? 5.0 : 5.0 + loadPct / 100.0 * 20.0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* 忽略 */ }
    }
}
