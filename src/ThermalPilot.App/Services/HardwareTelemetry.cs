using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace ThermalPilot.App.Services;

/// <summary>一次采样的遥测快照。</summary>
public readonly record struct TelemetrySample(
    double TempC,
    double LoadPercent,
    double PowerW,
    double MaxCoreGhz,
    string Source);

/// <summary>
/// 只读遥测源。三级回退，任何一级失败都不中断程序：
///   1. LibreHardwareMonitor（内核驱动，可读 CPU 封装温度 / 包功耗 / 核心频率，需管理员 + PawnIO）
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
        if (_driverOk && _cpu is not null)
        {
            try
            {
                _cpu.Update();
                foreach (var sub in _cpu.SubHardware) sub.Update();

                double temp = PickTemperature();
                double power = PickSensor(SensorType.Power, "Package");
                double load = PickSensor(SensorType.Load, "CPU Total");
                double clock = PickMaxClock();

                if (!double.IsNaN(temp))
                {
                    if (double.IsNaN(load)) load = ReadLoadFromSystemTimes();
                    if (double.IsNaN(power)) power = EstimatePower(load);
                    return new TelemetrySample(temp, load, power, clock, "LHM");
                }
            }
            catch
            {
                _driverOk = false;
            }
        }

        // 回退：ACPI 温度 + 系统负载 + 粗估功耗
        double t = ReadWmiTemperature();
        double l = ReadLoadFromSystemTimes();
        return new TelemetrySample(t, l, EstimatePower(l), 0, double.IsNaN(t) ? "SystemTimesOnly" : "WMI");
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
        return best;
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
