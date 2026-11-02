using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace ThermalPilot.Core;

/// <summary>
/// 一个风扇通道：转速传感器 +（可选）调速控制传感器，按同名序号配对。
/// 台式机主板由 SuperIO 提供；一体式水冷常见 HardwareType.Cooler；
/// 部分笔记本由 EmbeddedController 提供。
/// </summary>
public sealed class FanChannel
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public ISensor? RpmSensor { get; init; }
    public ISensor? ControlSensor { get; init; }
    public double LastPct { get; set; } = -1;

    public bool Controllable => ControlSensor?.Control != null;
}

/// <summary>
/// 硬件访问层：封装 LibreHardwareMonitor，负责温度/负载/频率/功耗/转速采集，
/// 以及（在主板支持时）风扇 PWM 软件调速。
/// LHM 自带已签名内核驱动（需管理员），用于 SuperIO / EC / MSR 访问。
/// </summary>
public sealed class HardwareService : IDisposable
{
    private Computer? _computer;
    private readonly List<FanChannel> _fanChannels = new();
    private readonly UpdateVisitor _visitor = new();
    private readonly CpuUsageCounter _cpuUsage = new();

    // 温度抗毛刺：真机实测 704 个样本里有 43 个单点离群值（详见 TempFilter 注释）。
    private readonly TempFilter _cpuTempFilter = new();
    private readonly TempFilter _gpuTempFilter = new();

    /// <summary>本次运行累计被丢掉的离群温度读数（CPU + GPU），诊断页展示用。</summary>
    public int TempGlitchCount => _cpuTempFilter.RejectedCount + _gpuTempFilter.RejectedCount;

    /// <summary>笔记本风扇外挂后端（NBFC）。无 LHM 调速通道时才启用。</summary>
    public NbfcBridge Nbfc { get; } = new();

    public bool IsOpen { get; private set; }
    public string OpenError { get; private set; } = "";
    public IReadOnlyList<FanChannel> FanChannels => _fanChannels;
    public bool LhmFanControlSupported => _fanChannels.Any(c => c.Controllable);
    public bool LhmRpmSupported => _fanChannels.Any(c => c.RpmSensor != null);

    /// <summary>风扇是否可被本程序控制：LHM 通道优先，其次 NBFC 后端。</summary>
    public bool FanControlSupported => LhmFanControlSupported || Nbfc.Installed;

    /// <summary>
    /// 能否读到**转速**。只认 LHM 通道。
    /// NBFC 不算——它回读的是占空比百分比，不是 RPM（见 NbfcStatus 注释）。
    /// </summary>
    public bool RpmSupported => LhmRpmSupported;

    // 说明：旧版这里有 FanDutyReadable（NBFC 回读占空比）。已删除 ——
    // 那个"回读"只是 NBFC 自己上次写入的值，不是独立测量，界面上再显示一次
    // 只会造出「实测不动 / 实测与指令不一致」这类假问题。
    public List<string> HardwareNames { get; } = new();
    public List<string> SensorInventory { get; } = new();
    public int SensorCount { get; private set; }

    /// <summary>重启引擎时清空滤波窗口，避免上一次运行的残留样本污染本次第一帧。</summary>
    public void ResetTempFilters()
    {
        _cpuTempFilter.Reset();
        _gpuTempFilter.Reset();
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public bool Open()
    {
        // NBFC 探测必须独立于 LHM：NBFC 自己带驱动、自己读 EC，
        // 完全不需要 LHM 服务成功。放在 _computer.Open() 之后会导致
        // "以非管理员启动时 Open 抛异常 → 跳过 NBFC 探测 → 诊断页错误地
        // 提示用户去装 NBFC（其实已经装了）"。
        try { Nbfc.Detect(); }
        catch (Exception ex) { Log.Warn("NBFC 探测异常：" + ex.Message); }

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,   // SuperIO：风扇转速与 PWM 控制
                IsControllerEnabled = true,    // 部分笔记本 EC、水冷控制器
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false
            };
            _computer.Open();
            IsOpen = true;

            Walk(buildInventory: true);
            Log.Info($"硬件监控已启动：设备 {HardwareNames.Count} 个，传感器 {SensorCount} 个，" +
                     $"风扇转速通道 {_fanChannels.Count(c => c.RpmSensor != null)} 个，" +
                     $"可调速通道 {_fanChannels.Count(c => c.Controllable)} 个" +
                     (Nbfc.Installed ? $"；NBFC 后端可用（配置：{Nbfc.ConfigName}）" : "；NBFC 未安装"));
            DumpSensorInventory();
            return true;
        }
        catch (Exception ex)
        {
            OpenError = ex.Message;
            IsOpen = false;
            Log.Error("硬件监控初始化失败（多为未以管理员运行，或驱动被 HVCI/杀软拦截）", ex);
            return false;
        }
    }

    /// <summary>把识别到的全部传感器写入日志，供排查"为什么没有 RPM"这类问题。</summary>
    private void DumpSensorInventory()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- 传感器清单 ----");
            foreach (var line in SensorInventory) sb.AppendLine(line);
            sb.AppendLine("--------------------");
            Log.Info(sb.ToString());
        }
        catch { }
    }

    public SensorSnapshot Read()
    {
        if (!IsOpen || _computer == null) return new SensorSnapshot();

        try
        {
            Walk(buildInventory: false);

            var acc = new CpuAccumulator();
            var fanReadings = _fanChannels.Select(ReadChannel).ToList();

            foreach (var hw in _computer.Hardware)
                Collect(hw, acc);

            // CPU 主温度：Package → Core Max → 各核最大（三级回退，顺序不能换）
            double cpuTemp = acc.PackageTemp;
            var cpuTempCandidates = acc.TempCandidates;
            if (cpuTemp <= 0) cpuTemp = acc.CoreMaxTemp;
            if (cpuTemp <= 0 && cpuTempCandidates.Count > 0)
                cpuTemp = cpuTempCandidates.Max();

            // GPU 主温度同理：Core → 其它命名 → 最后才用热点（有 Core 就不该报热点）
            double gpuTemp = acc.GpuCoreTemp > 0 ? acc.GpuCoreTemp
                           : acc.GpuTemp > 0 ? acc.GpuTemp
                           : acc.GpuHotSpot;
            double gpuLoad = acc.GpuLoad, gpuPower = acc.GpuPower;
            double cpuLoad = _cpuUsage.Next();   // 恒 0–100，见 CpuUsageCounter 注释
            var (ramTotal, ramUsed, ramPct) = SystemMemory.Read();

            // 抗毛刺（必须在拿到功率之后再算，虽然中值本身不看功率，
            // 但同一个 Read 里只取一次读数，保证原始值/滤波值来自同一拍）。
            double cpuTempSmooth = _cpuTempFilter.Push(cpuTemp);
            double gpuTempSmooth = _gpuTempFilter.Push(gpuTemp);

            // LHM 没有任何风扇传感器时，**不再**用 NBFC 的"占空比回读"凑一个假通道。
            // 原因：NBFC 的 Current fan speed 就是它自己上次写进 EC 的值，不是独立测量，
            // 跟本程序的指令同义、不会随负载自己动。把它当成"实测"显示（旧版的两格并排）
            // 会让人以为风扇卡住了。风扇输出请一律用 EngineState.AppliedFanPct（指令），
            // 转速只有 LHM 真的有传感器时才有。

            return new SensorSnapshot
            {
                CpuTemp = Math.Round(cpuTemp, 1),
                CpuTempSmoothed = Math.Round(cpuTempSmooth > 0 ? cpuTempSmooth : cpuTemp, 1),
                CpuCoreMaxTemp = Math.Round(acc.CoreMaxTemp > 0 ? acc.CoreMaxTemp : acc.PackageTemp, 1),
                CpuVoltage = Math.Round(acc.Voltage, 3),
                CpuLoad = Math.Round(cpuLoad, 1),
                CpuFrequencyMhz = Math.Round(acc.FreqMhz, 0),
                CpuPower = Math.Round(acc.Power, 1),
                GpuTemp = Math.Round(gpuTemp, 1),
                GpuTempSmoothed = Math.Round(gpuTempSmooth > 0 ? gpuTempSmooth : gpuTemp, 1),
                GpuHotSpotTemp = Math.Round(acc.GpuHotSpot, 1),
                GpuLoad = Math.Round(gpuLoad, 1),
                GpuPower = Math.Round(gpuPower, 1),
                RamTotalMb = ramTotal,
                RamUsedMb = ramUsed,
                RamPct = ramPct,
                Fans = fanReadings
            };
        }
        catch (Exception ex)
        {
            Log.Error("读取传感器失败", ex);
            return new SensorSnapshot();
        }
    }

    private sealed class CpuAccumulator
    {
        public double CoreMaxTemp, Voltage, FreqMhz, Power, GpuTemp, GpuLoad, GpuPower;
        /// <summary>CPU Package（与 OEM 工具/任务管理器同口径的主温度）。</summary>
        public double PackageTemp;
        /// <summary>GPU 核心温度（主指标）。</summary>
        public double GpuCoreTemp;
        /// <summary>GPU 热点温度（只作解释，不当主指标）。</summary>
        public double GpuHotSpot;
        public readonly List<double> TempCandidates = new();
    }

    private FanReading ReadChannel(FanChannel c)
    {
        double? rpm = null, pct = null;
        try
        {
            if (c.RpmSensor?.Value is float rv) rpm = rv;
            if (c.ControlSensor?.Value is float cv) pct = cv;
        }
        catch { }
        return new FanReading
        {
            Name = c.Name,
            Source = c.Source,
            Rpm = (int)Math.Round(rpm ?? 0),
            OutputPct = pct.HasValue ? Math.Round(pct.Value, 0) : null,
            Controllable = c.Controllable
        };
    }

    private void Walk(bool buildInventory)
    {
        if (_computer == null) return;
        foreach (var hw in _computer.Hardware) hw.Accept(_visitor);

        // 每次重建通道列表：LHM 的传感器对象引用保持不变，重建代价很低且更稳妥
        _fanChannels.Clear();
        HardwareNames.Clear();
        if (buildInventory) SensorInventory.Clear();
        SensorCount = 0;

        foreach (var hw in _computer.Hardware)
            ScanChannels(hw, buildInventory);
    }

    private static int? IndexOf(string name)
    {
        var m = Regex.Match(name, @"#?(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
            ? i
            : null;
    }

    private void ScanChannels(IHardware hw, bool buildInventory)
    {
        if (!HardwareNames.Contains(hw.Name))
            HardwareNames.Add($"{hw.HardwareType}:{hw.Name}");

        foreach (var s in hw.Sensors)
        {
            SensorCount++;
            if (buildInventory)
                SensorInventory.Add($"{hw.HardwareType}/{hw.Name} :: {s.SensorType} :: {s.Name} :: " +
                                    (s.Value.HasValue ? s.Value.Value.ToString("F1", CultureInfo.InvariantCulture) : "null") +
                                    (s.Control != null ? " [可控]" : ""));

            if (s.SensorType == SensorType.Control)
            {
                int? idx = IndexOf(s.Name);
                var match = idx is null
                    ? null
                    : _fanChannels.FirstOrDefault(c => IndexOf(c.Name) == idx && c.ControlSensor == null);
                if (match == null)
                {
                    _fanChannels.Add(new FanChannel
                    {
                        Name = s.Name,
                        Source = hw.Name,
                        ControlSensor = s
                    });
                }
                else
                {
                    _fanChannels[_fanChannels.IndexOf(match)] = new FanChannel
                    {
                        Name = match.Name,
                        Source = match.Source,
                        RpmSensor = match.RpmSensor,
                        ControlSensor = s
                    };
                }
            }
            else if (s.SensorType == SensorType.Fan)
            {
                int? idx = IndexOf(s.Name);
                var match = idx is null
                    ? null
                    : _fanChannels.FirstOrDefault(c => IndexOf(c.Name) == idx && c.RpmSensor == null);
                if (match == null)
                {
                    _fanChannels.Add(new FanChannel
                    {
                        Name = s.Name,
                        Source = hw.Name,
                        RpmSensor = s
                    });
                }
                else
                {
                    _fanChannels[_fanChannels.IndexOf(match)] = new FanChannel
                    {
                        Name = match.Name,
                        Source = match.Source,
                        RpmSensor = s,
                        ControlSensor = match.ControlSensor
                    };
                }
            }
        }

        foreach (var sub in hw.SubHardware) ScanChannels(sub, buildInventory);
    }

    private void Collect(IHardware hw, CpuAccumulator a)
    {
        bool isCpu = hw.HardwareType == HardwareType.Cpu;
        bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

        foreach (var s in hw.Sensors)
        {
            float? raw = s.Value;
            if (raw is not float v || float.IsNaN(v)) continue;
            string name = s.Name;

            switch (s.SensorType)
            {
                // CPU 温度必须分层，不能笼统取 max：
                //   本机同时有 "CPU Package"（59）和 "Core Max"（60），取 max 会显示最热单核，
                //   比 OEM 工具/任务管理器高 1~3℃，看起来就是"对不上"。
                //   主指标固定用 Package（AMD 是 Tdie/Tctl），没有 Package 才退回 Core Max。
                case SensorType.Temperature when isCpu:
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                        a.PackageTemp = Math.Max(a.PackageTemp, v);
                    else if (name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                        a.CoreMaxTemp = Math.Max(a.CoreMaxTemp, v);
                    else if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
                    { /* 距 TjMax 的余量，不是温度本身 —— 绝不能参与取最大值 */ }
                    else
                        a.TempCandidates.Add(v);
                    break;

                // GPU 同理：本机有 "GPU Core"(35) 与 "GPU Hot Spot"(43.7)。
                // 旧代码用 name.Contains("GPU") 把热点也算了进去再取 max，于是"GPU 温度"显示的是热点，
                // 比 OEM 工具高 8~12℃ —— 用户看到"GPU 没在用却 49.5℃"就是这么来的。
                case SensorType.Temperature when isGpu:
                    if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                        a.GpuHotSpot = Math.Max(a.GpuHotSpot, v);
                    else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        a.GpuCoreTemp = Math.Max(a.GpuCoreTemp, v);
                    else
                        a.GpuTemp = Math.Max(a.GpuTemp, v);   // 兜底：其它命名的 GPU 温度
                    break;

                case SensorType.Voltage when isCpu:
                    // "CPU Core" / "CPU Core #n"：取最大值即负载时的实际 Vcore
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        a.Voltage = Math.Max(a.Voltage, v);
                    break;

                // 负载不再取 LHM 的计数器（可能 >100%），见 CpuUsageCounter
                case SensorType.Load when isGpu:
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        a.GpuLoad = Math.Max(a.GpuLoad, v);
                    break;

                case SensorType.Clock when isCpu:
                    if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) && !name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                        a.FreqMhz = Math.Max(a.FreqMhz, v);
                    break;

                case SensorType.Power when isCpu:
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                        a.Power = Math.Max(a.Power, v);
                    break;

                case SensorType.Power when isGpu:
                    a.GpuPower = Math.Max(a.GpuPower, v);
                    break;
            }
        }

        foreach (var sub in hw.SubHardware)
            Collect(sub, a);
    }

    /// <summary>
    /// 设置全部可调速风扇通道的 PWM 百分比。返回成功设置的通道数。
    /// <paramref name="force"/> 为 false 时，NBFC 后端会按 EC 写保护节流（默认 3 s 一次）。
    /// </summary>
    public int SetFanPercent(double percent, bool force = false)
    {
        if (!IsOpen) return 0;

        if (_fanChannels.All(c => !c.Controllable))
        {
            // 没有 LHM 调速通道（笔记本 EC）→ 走 NBFC 后端
            if (Nbfc.Installed && Nbfc.TrySetPercent(percent, out string msg, force))
            {
                // 被节流/去重跳过时不要刷日志（每秒一条会把日志冲爆，也掩盖真正的问题）
                if (Nbfc.LastWriteApplied) Log.Info(msg);
                return 1;
            }
            return 0;
        }

        int ok = 0;
        foreach (var ch in _fanChannels.Where(c => c.Controllable))
        {
            try
            {
                ch.ControlSensor!.Control!.SetSoftware((float)Math.Clamp(percent, 0, 100));
                ch.LastPct = percent;
                ok++;
            }
            catch (Exception ex)
            {
                Log.Warn($"风扇通道 {ch.Name} 调速失败：{ex.Message}");
            }
        }
        return ok;
    }

    /// <summary>恢复 BIOS 默认风扇策略（退出/停止/标定结束时必须调用）。</summary>
    public void RestoreFanDefault()
    {
        if (!IsOpen) return;

        foreach (var ch in _fanChannels.Where(c => c.Controllable))
        {
            try { ch.ControlSensor?.Control?.SetDefault(); }
            catch (Exception ex) { Log.Warn($"风扇通道 {ch.Name} 恢复默认失败：{ex.Message}"); }
        }

        if (_fanChannels.All(c => !c.Controllable) && Nbfc.Installed)
        {
            try
            {
                // 用 set -a（自动曲线），不是 nbfc stop。stop 会关掉 NBFC 服务，
                // 之后本进程再也控不了风扇，实验中途会把旁路数据全丢掉。
                if (Nbfc.TrySetAuto(out string msg)) Log.Info(msg);
            }
            catch (Exception ex) { Log.Warn("NBFC 恢复失败：" + ex.Message); }
            return;
        }

        Log.Info("风扇控制已交还 BIOS 默认策略。");
    }

    public void Dispose()
    {
        try
        {
            RestoreFanDefault();
            _computer?.Close();
        }
        catch { }
        IsOpen = false;
    }
}
