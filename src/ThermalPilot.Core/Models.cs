using System.Text.Json.Serialization;

namespace ThermalPilot.Core;

/// <summary>一次采样的硬件快照。温度摄氏度，频率 MHz，风扇转速 RPM。</summary>
public sealed class SensorSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    /// <summary>CPU 主温度**原始读数**（Package，无 Package 时 Core Max）。诊断/CSV 追溯用。</summary>
    public double CpuTemp { get; init; }
    /// <summary>
    /// CPU 主温度**抗毛刺后**的值（5 点滑动中值，见 <see cref="TempFilter"/>）。
    /// 显示与控制一律用它：原始读数里约有 6% 的样本是单点离群值（真机实测 43/704），
    /// 峰值能假到 85~96℃，足以误触发 95℃ 超温保护。
    /// </summary>
    public double CpuTempSmoothed { get; init; }
    public double CpuCoreMaxTemp { get; init; }
    public double CpuVoltage { get; init; }
    public double CpuLoad { get; init; }
    public double CpuFrequencyMhz { get; init; }
    public double CpuPower { get; init; }
    /// <summary>GPU 核心温度（与任务管理器 / OEM 工具同口径）。原始读数。</summary>
    public double GpuTemp { get; init; }
    /// <summary>GPU 核心温度抗毛刺后的值（同 <see cref="CpuTempSmoothed"/>）。</summary>
    public double GpuTempSmoothed { get; init; }
    /// <summary>GPU 热点温度（Hot Spot，通常比核心高 8~12℃）。只用于解释，不当主指标。</summary>
    public double GpuHotSpotTemp { get; init; }
    public double GpuLoad { get; init; }
    public double GpuPower { get; init; }
    /// <summary>物理内存总量 / 已用 / 占用率（与任务管理器同口径）。</summary>
    public double RamTotalMb { get; init; }
    public double RamUsedMb { get; init; }
    public double RamPct { get; init; }
    public List<FanReading> Fans { get; init; } = new();

    [JsonIgnore] public bool CpuTempValid => CpuTemp > 0;
    [JsonIgnore] public bool GpuTempValid => GpuTemp > 0;

    /// <summary>界面展示的 CPU 温度：优先滤波值，滤波尚未产出时退回原始值。</summary>
    [JsonIgnore] public double CpuTempShown => CpuTempSmoothed > 0 ? CpuTempSmoothed : CpuTemp;
    [JsonIgnore] public double GpuTempShown => GpuTempSmoothed > 0 ? GpuTempSmoothed : GpuTemp;

    /// <summary>本拍原始读数被滤波改动过（= 刚才那一下是毛刺），界面用它加注"已平滑"。</summary>
    [JsonIgnore] public bool CpuTempSuppressed =>
        CpuTemp > 0 && CpuTempSmoothed > 0 && Math.Abs(CpuTemp - CpuTempSmoothed) >= 3.0;

    /// <summary>所有风扇通道中的最高转速（0 表示未检测到转速通道）。</summary>
    [JsonIgnore] public int FanRpm => Fans.Count == 0 ? 0 : Fans.Max(f => f.Rpm);

    /// <summary>主风扇输出百分比（-1 表示当前由 BIOS 控制）。</summary>
    [JsonIgnore] public double FanSoftwarePct
    {
        get
        {
            var v = Fans.Where(f => f.OutputPct.HasValue).Select(f => f.OutputPct!.Value).ToList();
            return v.Count == 0 ? -1 : v.Average();
        }
    }

    /// <summary>
    /// 控制决策使用的最热点温度。**必须用滤波值**：
    /// 原始读数里的单点离群值会直接触发 95℃ 超温保护（PL1 压到 10 W → 游戏掉帧），
    /// 而它对应的功率增量中位数是 −0.5 W，物理上不可能是真温升。
    /// </summary>
    [JsonIgnore] public double Hotspot => Math.Max(CpuTempValid ? CpuTempShown : 0, GpuTempValid ? GpuTempShown : 0);
}

/// <summary>单个风扇通道的读数。</summary>
public sealed class FanReading
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public int Rpm { get; init; }
    public double? OutputPct { get; init; }
    public bool Controllable { get; init; }
}

/// <summary>曲线控制点：输入(温度℃) -> 输出(风扇% 或 CPU 最大性能状态%)。</summary>
public sealed class CurvePoint
{
    public double Temperature { get; set; }
    public double Value { get; set; }

    public CurvePoint() { }

    public CurvePoint(double temperature, double value)
    {
        Temperature = temperature;
        Value = value;
    }
}

/// <summary>折线控制曲线，点之间线性插值，区间外取端点值。</summary>
public sealed class Curve
{
    public List<CurvePoint> Points { get; set; } = new();

    public Curve Clone() => new() { Points = Points.Select(p => new CurvePoint(p.Temperature, p.Value)).ToList() };

    public void Normalize()
    {
        Points = Points
            .Where(p => p != null)
            .Select(p => new CurvePoint(Math.Clamp(p.Temperature, 0, 110), p.Value))
            .OrderBy(p => p.Temperature)
            .ToList();
    }

    public double Evaluate(double input, double fallback = 0)
    {
        if (Points.Count == 0) return fallback;
        var pts = Points;
        if (input <= pts[0].Temperature) return pts[0].Value;
        if (input >= pts[^1].Temperature) return pts[^1].Value;

        for (int i = 1; i < pts.Count; i++)
        {
            if (input > pts[i].Temperature) continue;
            double x0 = pts[i - 1].Temperature, x1 = pts[i].Temperature;
            double y0 = pts[i - 1].Value, y1 = pts[i].Value;
            if (Math.Abs(x1 - x0) < 0.001) return y1;
            double t = (input - x0) / (x1 - x0);
            return y0 + (y1 - y0) * t;
        }
        return pts[^1].Value;
    }
}

/// <summary>温控与调频配置（持久化到 settings.json）。</summary>
public sealed class ThermalConfig
{
    public string ProfileName { get; set; } = "均衡";

    public Curve FanCurve { get; set; } = Presets.Fan("均衡");
    public Curve FreqCurve { get; set; } = Presets.Freq("均衡");

    // —— 基础控制 ——
    public double FanHysteresisC { get; set; } = 3.0;
    public double FanMinPct { get; set; } = 20;
    public double FanMaxPct { get; set; } = 100;
    public double FanRampUpPctPerSec { get; set; } = 60;
    public double FanRampDownPctPerSec { get; set; } = 10;
    public double FanStopBelowC { get; set; } = 40;
    /// <summary>
    /// 两次写 EC（风扇）之间的最小间隔。笔记本风扇走 NBFC/WinRing0 直写 EC，
    /// 1 Hz 连续写会把 ACPI EC 驱动打到超时（事件日志 ACPI 13），
    /// 而电池电量同样经由 EC 上报 —— 这就是"检测不到电池"的直接来源。
    /// 默认 3000 ms；低于 1000 会被夹住。
    /// </summary>
    public int FanWriteMinIntervalMs { get; set; } = 3000;
    public double CriticalTempC { get; set; } = 95;
    public int CriticalProcMax { get; set; } = 40;
    public int SampleIntervalMs { get; set; } = 1000;

    public bool EnableFanControl { get; set; } = true;
    public bool EnableFrequencyControl { get; set; } = true;
    public bool EnableUndervolt { get; set; }
    public int UndervoltMv { get; set; } = -50;

    // —— 控制通道（互斥）——
    /// <summary>
    /// msr / powercfg / policy。同一时刻只允许一个通道拥有 CPU 频率/功率的控制权。
    /// 旧版让这三套同时生效，互相覆盖，是"越调越慢"的直接原因（见 ControlChannels 注释）。
    /// </summary>
    public string ControlChannel { get; set; } = "msr";

    // —— CPU 精细调控（直接写 MSR：PL1/PL2/Time/EPP/睿频倍率）——
    public bool EnableCpuTuning { get; set; }
    public int CpuPl1W { get; set; }          // 0 = 不改
    public int CpuPl2W { get; set; }          // 0 = 不改
    public int CpuTauS { get; set; }          // 0 = 不改（Time/τ 秒）
    public int CpuEpp { get; set; } = -1;     // -1 = 不改；0=性能 255=节能
    public int CpuTurboRatio { get; set; }    // 0 = 不改（×100MHz，如 35=3.5GHz）

    // —— 智能自动 CPU 散热（闭环，推荐）——
    public bool AutoCpuMode { get; set; }
    /// <summary>自动档取向：silent / balanced / performance。</summary>
    public string AutoCpuProfile { get; set; } = "balanced";
    /// <summary>
    /// 允许的最高温度（ceiling），不是"要压到的目标值"。
    /// 默认随档位走：静音 88 / 平衡 88 / 性能 90（性能档散热最强，所以允许更热以换取更大功率预算）。
    /// </summary>
    public double AutoTargetC { get; set; } = 88;
    /// <summary>环境温度（无传感器，用户输入，影响功率预算绝对值）。</summary>
    public double AmbientC { get; set; } = 24;

    // —— 平台功率（PL1/PL2 的可用区间由它决定）——
    // 0 = 自动（读 MSR 0x614 / NVML / 型号表）。非 0 = 用户手填，以用户为准。
    // 这两个数不是"策略"，是机器的电气属性，所以放全局配置而不是配置档增量。
    /// <summary>CPU 设计功耗（W）。0 = 自动探测。</summary>
    public double CpuDesignW { get; set; }
    /// <summary>独显设计功耗（W）。0 = 自动探测。</summary>
    public double GpuDesignW { get; set; }
    /// <summary>只计算不写 MSR（安全预览）。</summary>
    public bool AutoCpuDryRun { get; set; }

    // —— 高级：目标温度 PI 模式（替代曲线，高手用）——
    public bool TargetTempMode { get; set; }
    public double TargetTempC { get; set; } = 80;
    public double Kp { get; set; } = 5.0;    // %/°C
    public double Ki { get; set; } = 0.35;   // %/(°C·s)

    // —— 高级：实测策略模型（tools/lab_model.py 产出的 model-*.json）——
    /// <summary>开启后忽略曲线/ PID，直接按实测阶梯表查表下发 cap 与风扇。</summary>
    public bool UsePolicyModel { get; set; }
    public string PolicyModelPath { get; set; } = "";
    /// <summary>取向：balanced / quiet / power。</summary>
    public string PolicyStrategy { get; set; } = "balanced";

    // —— 高级：数据采集日志 ——
    public bool EnableLogging { get; set; }
    public int LogEveryN { get; set; } = 2;

    public string LastCalibrationNote { get; set; } = "";

    public ThermalConfig Clone()
    {
        var c = (ThermalConfig)MemberwiseClone();
        c.FanCurve = FanCurve.Clone();
        c.FreqCurve = FreqCurve.Clone();
        return c;
    }
}

/// <summary>标定偏好（决定生成曲线的取向）。</summary>
public sealed class CalibrationPreference
{
    public string Name { get; set; } = "均衡";
    /// <summary>目标稳态温度：风扇升到把温度压在这里。</summary>
    public double TargetTemp { get; set; } = 80;
    /// <summary>噪声上限：日常允许的最大风扇%（仅超温突破）。</summary>
    public double NoiseCapPct { get; set; } = 100;
    public double MinFanPct { get; set; } = 25;
    /// <summary>激进系数：越大越早满转（静音 &lt; 1，性能 &gt; 1）。</summary>
    public double Aggressiveness { get; set; } = 1.0;

    public static CalibrationPreference Create(string name) => name switch
    {
        "静音" => new CalibrationPreference
        {
            Name = "静音", TargetTemp = 85, NoiseCapPct = 72, MinFanPct = 20, Aggressiveness = 0.75
        },
        "性能" => new CalibrationPreference
        {
            Name = "性能", TargetTemp = 74, NoiseCapPct = 100, MinFanPct = 35, Aggressiveness = 1.35
        },
        _ => new CalibrationPreference
        {
            Name = "均衡", TargetTemp = 80, NoiseCapPct = 100, MinFanPct = 25, Aggressiveness = 1.0
        }
    };
}

/// <summary>三个内置预设，UI 一键切换。</summary>
public static class Presets
{
    public static readonly string[] Names = { "静音", "均衡", "性能" };

    public static Curve Fan(string name) => new()
    {
        Points = name switch
        {
            "静音" => new List<CurvePoint>
            {
                new(40, 0), new(50, 22), new(60, 35), new(70, 50), new(80, 65), new(90, 82), new(95, 100)
            },
            "性能" => new List<CurvePoint>
            {
                new(35, 40), new(45, 55), new(55, 70), new(65, 82), new(75, 92), new(85, 100), new(95, 100)
            },
            _ => new List<CurvePoint>
            {
                new(40, 20), new(50, 30), new(60, 45), new(70, 60), new(80, 75), new(90, 90), new(95, 100)
            }
        }
    };

    public static Curve Freq(string name) => new()
    {
        Points = name switch
        {
            "静音" => new List<CurvePoint>
            {
                new(45, 100), new(55, 88), new(65, 72), new(75, 55), new(85, 40), new(95, 25)
            },
            "性能" => new List<CurvePoint>
            {
                new(70, 100), new(80, 96), new(85, 88), new(90, 75), new(95, 55)
            },
            _ => new List<CurvePoint>
            {
                new(55, 100), new(65, 95), new(75, 82), new(85, 65), new(90, 50), new(95, 30)
            }
        }
    };

    public static ThermalConfig Create(string name)
    {
        var cfg = new ThermalConfig
        {
            ProfileName = name,
            FanCurve = Fan(name),
            FreqCurve = Freq(name),
            TargetTempC = name switch { "静音" => 85, "性能" => 75, _ => 80 }
        };
        if (name == "静音")
        {
            cfg.FanRampDownPctPerSec = 6;
            cfg.FanHysteresisC = 4;
        }
        else if (name == "性能")
        {
            cfg.FanRampDownPctPerSec = 18;
            cfg.FanHysteresisC = 2;
        }
        return cfg;
    }
}

/// <summary>引擎对外的实时状态，UI 只读消费。</summary>
public sealed class EngineState
{
    public SensorSnapshot Snapshot { get; set; } = new();
    public double TargetFanPct { get; set; }
    /// <summary>**已确认生效**的占空比。写入失败时为 -1，绝不显示"我们以为的目标值"。</summary>
    public double AppliedFanPct { get; set; } = -1;
    /// <summary>风扇控制是否真的在生效（false = 指令发出去了但风扇仍由 NBFC/BIOS 自管）。</summary>
    public bool FanEffective { get; set; }
    /// <summary>风扇控制的当前状态说明（"已接管 46%"/"未生效：NBFC 已退回自动"）。</summary>
    public string FanNote { get; set; } = "";
    public double TargetProcMax { get; set; }
    public int AppliedProcMax { get; set; } = -1;
    public int BoostMode { get; set; } = -1;
    public bool Paused { get; set; }
    public string Status { get; set; } = "初始化";
    public string LastAction { get; set; } = "";
    public bool Faulted { get; set; }
    public string ControlModeHint { get; set; } = "";
    /// <summary>当前策略模型给出的一档（cap / fan / 预测温度），未启用时为空。</summary>
    public string PolicyHint { get; set; } = "";
    /// <summary>CPU 精细调控 / 智能自动 当前决策（PL1/PL2/τ/EPP/预测温度）。</summary>
    public string CpuHint { get; set; } = "";
    /// <summary>智能自动是否正在驱动输出。</summary>
    public bool AutoCpuActive { get; set; }
    /// <summary>CPU 交付性能指标（有效频率 / 包功率 / 谁在限制变快）。</summary>
    public string PerfText { get; set; } = "";
    /// <summary>
    /// 真正在卡频率的东西（温度墙 / 功率墙 / 睿频上限 / 轻载）。
    /// 仪表盘「CPU 频率」卡的副标题用它——旧版那里显示的是「上限 99%」，
    /// 那是本程序写给 Windows 电源计划的值，本机实测无效，属于会误导人的假信息。
    /// </summary>
    public string BindLimitText { get; set; } = "";

    public EngineState Copy() => (EngineState)MemberwiseClone();
}

// ===================== 诊断 =====================

public enum DiagStatus { Ok, Warn, Fail }

public sealed class DiagItem
{
    public string Name { get; init; } = "";
    public DiagStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";
}

// ===================== 标定 =====================

public enum StressKind { Idle, Cpu, GpuExternal, CpuGpu }

public sealed class CalibrationStage
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int DurationSec { get; init; }
    public StressKind Load { get; init; }

    /// <summary>强制的频率上限（%）；null 表示沿用当前曲线。</summary>
    public int? CapPercent { get; init; }

    /// <summary>强制的风扇输出（%）；null 表示沿用当前曲线。</summary>
    public double? FanOverride { get; init; }

    public bool FitsCapModel => CapPercent.HasValue && FanOverride is null;
    public bool FitsFanModel => FanOverride.HasValue;
}

public sealed class CalibrationSample
{
    public DateTime Time { get; init; }
    public string Stage { get; init; } = "";
    public double CpuTemp { get; init; }
    /// <summary>抗毛刺前的 CPU 原始读数（0 表示该行没有记录原始值，分析时按 CpuTemp 处理）。</summary>
    public double CpuTempRaw { get; init; }
    public double GpuTemp { get; init; }
    public double CpuLoad { get; init; }
    public double GpuLoad { get; init; }
    public double CpuMhz { get; init; }
    public double CpuPower { get; init; }
    public double GpuPower { get; init; }
    public int FanRpm { get; init; }
    public double FanOut { get; init; }
    public int Cap { get; init; }
    public double FanCmd { get; init; }
    /// <summary>GPU 热点（参考值；主温度是 GpuTemp=C 核心）。</summary>
    public double GpuHotSpot { get; init; }
    /// <summary>物理内存占用率（%）。</summary>
    public double RamPct { get; init; }
}

/// <summary>一阶热模型：稳态温度 T ≈ A + B·cap（%） 或 T ≈ A + B·fan（%）。</summary>
public sealed class ThermalModel
{
    public bool CapValid { get; set; }
    public double CapA { get; set; }
    public double CapB { get; set; }
    public double CapR2 { get; set; }

    public bool FanValid { get; set; }
    public double FanA { get; set; }
    public double FanB { get; set; }
    public double FanR2 { get; set; }

    public double IdleTemp { get; set; }
    public double FullLoadTemp { get; set; }

    public List<(double x, double y)> CapPoints { get; } = new();
    public List<(double x, double y)> FanPoints { get; } = new();

    /// <summary>把稳态温度反解成所需的频率上限（%）。</summary>
    public double CapForTemp(double temp)
        => CapValid && Math.Abs(CapB) > 0.001 ? Math.Clamp((temp - CapA) / CapB, 5, 100) : 100;

    /// <summary>把稳态温度反解成所需的风扇输出（%）。</summary>
    public double FanForTemp(double temp)
        => FanValid && Math.Abs(FanB) > 0.001 ? Math.Clamp((temp - FanA) / FanB, 0, 100) : 100;
}

public sealed class CalibrationResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public ThermalModel Model { get; init; } = new();
    public Curve FanCurve { get; init; } = new();
    public Curve FreqCurve { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public List<CalibrationSample> Samples { get; init; } = new();
    public string CsvPath { get; init; } = "";
    public int SampleCount => Samples.Count;
}
