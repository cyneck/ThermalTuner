using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace ThermalTuner.Monitor;

/// <summary>一次采样的遥测快照。</summary>
public readonly record struct TelemetrySample(
    double TempC,
    double LoadPercent,
    double PowerW,
    double MaxCoreGhz,
    string Source);

/// <summary>
/// 只读遥测源。三级回退，任何一级失败都不会中断程序：
///   1. LibreHardwareMonitor（内核驱动，可读 CPU 封装温度 / 包功耗 / 核心频率，需管理员）
///   2. ACPI 热区 WMI（部分机型可用，温度粒度为整区）
///   3. GetSystemTimes 计算负载（始终可用）
/// </summary>
public sealed class TelemetrySource : IDisposable
{
    private readonly Computer? _computer;
    private IHardware? _cpu;
    private bool _driverOk;

    private bool _acpiOk = true;
    private bool _perfOk = true;
    private double _lastWmiTemp = double.NaN;

    private long _prevIdle, _prevKernel, _prevUser;
    private double _lastLoad = 0;

    public TelemetrySource()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        }

        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _driverOk = _cpu != null;
        }
        catch
        {
            _driverOk = false;
        }
    }

    public bool DriverAvailable => _driverOk;

    /// <summary>诊断：列出 LHM 枚举到的全部硬件与传感器（排查无温度读数用）。</summary>
    public IEnumerable<string> DumpSensors()
    {
        if (_computer == null) { yield return "Computer 未初始化"; yield break; }
        yield return $"硬件数量: {_computer.Hardware.Count}";
        foreach (var hw in _computer.Hardware)
        {
            try { hw.Update(); } catch { }
            yield return $"[{hw.HardwareType}] {hw.Name}  传感器 {hw.Sensors.Length} 个";
            foreach (var s in hw.Sensors)
                yield return $"    {s.SensorType,-12} {s.Name,-30} = {s.Value?.ToString() ?? "null"}";
            foreach (var sub in hw.SubHardware)
            {
                try { sub.Update(); } catch { }
                yield return $"  [子硬件 {sub.HardwareType}] {sub.Name}  传感器 {sub.Sensors.Length} 个";
                foreach (var s in sub.Sensors)
                    yield return $"      {s.SensorType,-12} {s.Name,-30} = {s.Value?.ToString() ?? "null"}";
            }
        }
    }

    public TelemetrySample Read()
    {
        double temp, load, power, clock;
        string source;

        if (_driverOk && _cpu != null)
        {
            try
            {
                _cpu.Update();
                foreach (var sub in _cpu.SubHardware) sub.Update();

                temp = PickTemperature();
                power = PickSensor(SensorType.Power, "Package");
                load = PickSensor(SensorType.Load, "CPU Total");
                if (double.IsNaN(load)) load = double.NaN; // 回退到系统负载
                clock = PickMaxClock();

                if (!double.IsNaN(temp))
                {
                    load = double.IsNaN(load) ? ReadLoadFromSystemTimes() : load;
                    return new TelemetrySample(temp, load, power, clock, "LHM");
                }
            }
            catch
            {
                _driverOk = false;
            }
        }

        // 回退：ACPI 温度 + 系统负载
        temp = ReadWmiTemperature();
        load = ReadLoadFromSystemTimes();
        power = EstimatePower(load);
        source = double.IsNaN(temp) ? "SystemTimesOnly" : "WMI";
        return new TelemetrySample(temp, load, power, 0, source);
    }

    // ---------- LibreHardwareMonitor 传感器挑选 ----------

    private double PickTemperature()
    {
        // 优先封装温度，其次最热核心，最后所有温度取最大
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
        if (total <= 0) return _lastLoad; // 首拍差值为 0，沿用上次（初始 0）

        _lastLoad = Math.Clamp((total - dIdle) * 100.0 / total, 0.0, 100.0);
        return _lastLoad;
    }

    private static double EstimatePower(double loadPct) =>
        double.IsNaN(loadPct) ? 5.0 : 5.0 + loadPct / 100.0 * 20.0; // 仅 WMI 回退时的粗估

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* 忽略 */ }
    }
}
