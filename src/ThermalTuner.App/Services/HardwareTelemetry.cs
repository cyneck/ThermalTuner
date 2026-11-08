using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App.Services;

/// <summary>一次采样的遥测快照。</summary>
/// <param name="PowerMeasured">功耗是否实测（RAPL 能量计数器或 LHM 传感器）；false 表示按负载粗估。</param>
public readonly record struct TelemetrySample(
    double TempC,
    double LoadPercent,
    double PowerW,
    double MaxCoreGhz,
    bool PowerMeasured,
    string Source,
    // 监控面板扩展指标。NaN = 本机不可读；UI 必须显示 N/A，不得用演算值冒充实测。
    double GpuLoadPercent = double.NaN,
    double GpuMemoryPercent = double.NaN,
    double GpuMemoryUsedMb = double.NaN,
    double GpuMemoryTotalMb = double.NaN,
    double FanRpm = double.NaN,
    double FanControlPercent = double.NaN,
    string GpuName = "",
    string GpuSource = "",
    // 风扇读数来源：LHM 实测 / EC 直读(ec-probe) / NBFC 服务；空串 = 不可读。
    string FanSource = "",
    double FanRawValue = double.NaN,
    // EC 原始值恒定检测：连续多少次真实采样未变化 / 是否已判定「疑似非实时」。
    int FanStagnantReads = 0,
    bool FanStagnant = false,
    // 诊断对照：LHM CPU Total 是线程级估算、随 Update 窗口剧烈跳动，
    // 口径与任务管理器不同，仅用于自检报告，不进界面 / 决策树 / 功耗估算。
    double LhmCpuTotal = double.NaN,
    // 独显核心温度（LHM GPU Core）；NaN = 本机不可读。
    double GpuTempC = double.NaN);

/// <summary>
/// 只读遥测源。三级回退，任何一级失败都不中断程序：
///   1. LibreHardwareMonitor 0.9.6（PawnIO 内核驱动，可读 CPU 封装温度 / 包功耗 / 核心频率，需管理员）
///   2. ACPI 热区 WMI（部分机型可用，粒度为整区）
///   3. GetSystemTimes 计算负载（始终可用）
/// 研发注意：本类与 src/ThermalTuner.Monitor/TelemetrySource.cs 逻辑同源，
/// 建议 M2 阶段抽取为 ThermalTuner.Core.Telemetry（见 PRD 附录 A）。
/// </summary>
public sealed class HardwareTelemetry : IDisposable
{
    private readonly Computer? _computer;
    private readonly IHardware? _cpu;
    /// <summary>GPU 硬件，独显优先、核显次之。</summary>
    private readonly List<IHardware> _gpus = new();
    /// <summary>主板及子硬件，风扇转速 / 控制百分比来源（若机型暴露）。</summary>
    private readonly List<IHardware> _fanSources = new();
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
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = true
            };
            computer.Open();
            _cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _computer = computer;
            _driverOk = _cpu != null;

            foreach (var hw in computer.Hardware)
            {
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    _gpus.Add(hw);
                if (hw.HardwareType == HardwareType.Motherboard)
                {
                    _fanSources.Add(hw);
                    foreach (var sub in hw.SubHardware) _fanSources.Add(sub);
                }
            }
            // 独显优先：有 GPU Core / GPU Memory 精确传感器；核显只作 D3D 回退
            _gpus.Sort((a, b) => GpuRank(b).CompareTo(GpuRank(a)));
        }
        catch
        {
            _driverOk = false;
        }

        // 构造过程（Computer.Open / 枚举硬件 / 加载 PawnIO 模块）本身占用 CPU。
        // 若不重置差分基准，首拍会把启动开销算进「总时间」，读数虚高
        // （管理员态实测首拍 68.5%，稳态仅十余 %）。以构造结束时刻作为第一拍起点。
        if (GetSystemTimes(out long idle2, out long kernel2, out long user2))
        {
            _prevIdle = idle2; _prevKernel = kernel2; _prevUser = user2;
        }
    }

    private static int GpuRank(IHardware hw) => hw.HardwareType == HardwareType.GpuIntel ? 1 : 2;

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

    /// <summary>诊断：列出 LHM 枚举到的全部硬件与传感器（含 GPU / 主板风扇）。</summary>
    public IEnumerable<string> DumpSensors()
    {
        if (_computer is null) { yield return "Computer 未初始化（驱动不可用）"; yield break; }

        foreach (var hw in _computer.Hardware)
        {
            yield return $"[{hw.HardwareType}] {hw.Name} · 传感器 {hw.Sensors.Length} 个";
            foreach (var s in hw.Sensors)
                yield return $"  {s.SensorType,-12} {s.Name,-30} = {s.Value?.ToString("F1") ?? "null"}";
            foreach (var sub in hw.SubHardware)
            {
                yield return $"  └─ [{sub.HardwareType}] {sub.Name}";
                foreach (var s in sub.Sensors)
                    yield return $"     {s.SensorType,-12} {s.Name,-30} = {s.Value?.ToString("F1") ?? "null"}";
            }
        }
    }

    public TelemetrySample Read()
    {
        // 每拍都推进系统计时差分，窗口恒为「上一拍 → 本拍」：
        // GetSystemTimes 的 (总时间 − 空闲时间) / 总时间 与任务管理器的系统级 CPU 时间占比同源，
        // 按全部逻辑核汇总后天然落在 0~100。LHM 的 CPU Total 是线程级负载估算，
        // 依赖两次 Update() 之间的窗口，实测同一秒内可从 16% 跳到 50%+（自检报告里 49.6%），
        // 与温度/功耗/风扇读数自相矛盾，故只保留作诊断对照，不再作为 LoadPercent。
        // 注意：不能只在回退路径调用，否则差分基准长期不推进，回退时会拿到「自启动以来」的平均值。
        double systemLoad = ReadLoadFromSystemTimes();

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
                double lhmTotal = Pct(PickSensor(SensorType.Load, "CPU Total"));  // 诊断对照，不进决策
                double load = double.IsNaN(systemLoad) ? lhmTotal : systemLoad;
                if (double.IsNaN(load)) load = 0;
                double clock = PickMaxClock();

                if (!double.IsNaN(temp))
                {
                    // 优先级：LHM 传感器 > RAPL 实测 > 按负载估算
                    if (!double.IsNaN(lhmPower))
                        return WithExtras(new TelemetrySample(temp, load, lhmPower, clock, true, "LHM",
                            LhmCpuTotal: lhmTotal));

                    if (!double.IsNaN(raplW))
                        return WithExtras(new TelemetrySample(temp, load, raplW, clock, true, "RAPL",
                            LhmCpuTotal: lhmTotal));

                    return WithExtras(new TelemetrySample(temp, load, EstimatePower(load), clock, false,
                        "LHM/估算", LhmCpuTotal: lhmTotal));
                }
            }
            catch
            {
                _driverOk = false;
            }
        }

        // 回退：ACPI 温度 + 同一份系统级负载；功耗优先 RAPL，其次估算
        double t = ReadWmiTemperature();
        double l = double.IsNaN(systemLoad) ? 0 : systemLoad;

        if (!double.IsNaN(raplW))
            return WithExtras(new TelemetrySample(t, l, raplW, 0, true, "RAPL"));

        return WithExtras(new TelemetrySample(t, l, EstimatePower(l), 0, false,
            double.IsNaN(t) ? "SystemTimesOnly" : "WMI"));
    }

    /// <summary>
    /// 百分比类指标统一归一化到 0~100。NaN 表示「本机读不到」，必须原样保留给 UI 显示 N/A。
    /// 这是遥测离开采集层的唯一出口，所有消费者（指标卡 / 时序图 / 决策树 / CSV）用同一份合法值。
    /// </summary>
    private static double Pct(double v) => double.IsNaN(v) ? v : Math.Clamp(v, 0.0, 100.0);

    /// <summary>把 GPU / 风扇扩展指标附加到基础采样；任何异常都返回 NaN，不打断主链路。</summary>
    private TelemetrySample WithExtras(TelemetrySample s)
    {
        // 出采样层前的最后一道归一化：覆盖所有 Read() 返回路径（含异常 catch 分支）。
        s = s with
        {
            LoadPercent = Pct(s.LoadPercent),
            GpuLoadPercent = Pct(s.GpuLoadPercent),
            GpuMemoryPercent = Pct(s.GpuMemoryPercent)
        };

        try
        {
            var g = ReadGpu();
            // 风扇数据源优先级统一由 Core 的 FanSourceResolver 编排：
            //   LHM 传感器 → EC 端口直读（内部再回退 NBFC）→ Unavailable。
            // 本机 HP 838F 无 Fan/Control 传感器时，绝不拿决策树演算值冒充实测。
            var fan = FanSources.Read(ReadFan);
            double rpm = fan.HasValue && fan.Capability.IsRpm ? fan.Value : double.NaN;
            double pct = fan.HasValue && !fan.Capability.IsRpm &&
                         fan.Capability.Measure != FanMeasureKind.RawByte
                ? fan.Value : double.NaN;
            string fanSource = fan.HasValue ? fan.Capability.Name : string.Empty;

            return s with
            {
                GpuLoadPercent = Pct(g.Load),
                GpuMemoryPercent = Pct(g.MemPct),
                GpuMemoryUsedMb = g.UsedMb,
                GpuMemoryTotalMb = g.TotalMb,
                GpuName = g.Name,
                GpuSource = g.Source,
                GpuTempC = g.TempC,
                FanRpm = rpm,
                // FanPercentCodec 已钳到 0~100；这里再过一道，保证与 CPU/GPU 同一口径。
                FanControlPercent = Pct(pct),
                FanSource = fanSource,
                FanRawValue = fan.RawValue,
                FanStagnantReads = fan.StagnantReads,
                FanStagnant = fan.Stagnant
            };
        }
        catch
        {
            return s;   // 扩展指标失败不影响温度/功耗/决策
        }
    }

    /// <summary>
    /// GPU 使用率与显存占用率。优先独显的精确传感器（GPU Core / GPU Memory）；
    /// 核显回退到 D3D 负载峰值与共享显存 Used/Total。NaN 表示本机读不到。
    /// </summary>
    private (double Load, double MemPct, double UsedMb, double TotalMb, string Name, string Source, double TempC) ReadGpu()
    {
        foreach (var gpu in _gpus)
        {
            try { gpu.Update(); } catch { continue; }

            double load = double.NaN, memPct = double.NaN, used = double.NaN, total = double.NaN;
            double gpuTemp = double.NaN;
            foreach (var s in gpu.Sensors)
            {
                if (s.Value is not float v) continue;
                string name = s.Name;

                if (s.SensorType == SensorType.Temperature &&
                    name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) gpuTemp = v;
                else if (s.SensorType == SensorType.Load &&
                    name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) load = v;
                else if (s.SensorType == SensorType.Load &&
                         name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase)) memPct = v;
                else if (s.SensorType == SensorType.Load &&
                         name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase) &&
                         (double.IsNaN(load) || v > load)) load = v;   // 核显回退：D3D 峰值
                else if (s.SensorType == SensorType.SmallData &&
                         name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase)) used = v;
                else if (s.SensorType == SensorType.SmallData &&
                         name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase)) total = v;
                else if (s.SensorType == SensorType.SmallData &&
                         name.Contains("Shared Memory Used", StringComparison.OrdinalIgnoreCase) &&
                         double.IsNaN(used)) used = v;
                else if (s.SensorType == SensorType.SmallData &&
                         name.Contains("Shared Memory Total", StringComparison.OrdinalIgnoreCase) &&
                         double.IsNaN(total)) total = v;
            }

            if (double.IsNaN(memPct) && !double.IsNaN(used) && !double.IsNaN(total) && total > 0)
                memPct = Math.Clamp(used / total * 100.0, 0, 100);

            if (!double.IsNaN(load) || !double.IsNaN(memPct))
                return (load, memPct, used, total, gpu.Name,
                        gpu.HardwareType == HardwareType.GpuIntel ? "D3D 引擎峰值（核显）" : "NVAPI · GPU Core",
                        gpuTemp);
        }
        return (double.NaN, double.NaN, double.NaN, double.NaN, string.Empty, string.Empty, double.NaN);
    }

    /// <summary>
    /// 风扇实测：SensorType.Fan = RPM，SensorType.Control = 控制百分比。
    /// 部分笔记本主板不被 LHM 支持（本机 HP 838F 即如此），此时返回 NaN，
    /// 由 FanSourceResolver 按「LHM → EC 直读 → NBFC → Unavailable」逐级降级；
    /// 全不可读时 UI 显示 N/A。本类只读，从不写风扇 / EC / PWM。
    /// </summary>
    private (double Rpm, double Pct) ReadFan()
    {
        double rpm = double.NaN, pct = double.NaN;
        foreach (var hw in _fanSources)
        {
            try { hw.Update(); } catch { continue; }
            foreach (var s in hw.Sensors)
            {
                if (s.Value is not float v) continue;
                if (s.SensorType == SensorType.Fan && (double.IsNaN(rpm) || v > rpm)) rpm = v;
                if (s.SensorType == SensorType.Control && (double.IsNaN(pct) || v > pct)) pct = v;
            }
        }
        return (rpm, pct);
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
