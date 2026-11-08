using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ThermalTuner.Core;
using ThermalTuner.Core.Environment;
using ThermalTuner.App.Services.Msr;
using ThermalTuner.Core.Msr;
using ThermalTuner.Core.Stress;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App.Services;

public sealed record DecisionRow(
    string Time, string Rule, string Reason, string Pl1, string Direction, string Limit, string Path);

public sealed record AlertRow(string Time, string Level, string Title, string Detail)
{
    /// <summary>事件等级的中文短标签：用于事件中心左侧的彩色标记，避免只有颜色没有文字。</summary>
    public string LevelText => Level switch
    {
        "danger" => "故障",
        "warn" => "注意",
        _ => "信息"
    };
}

public sealed record SampleRow(
    string Time, string Temp, string Load, string Power, string Pl1, string Rule, string Fan = "--");

/// <summary>
/// 档位选中前的下发计划（v0.5.0）。纯计算结果，供 UI 二次确认展示，
/// 不写寄存器、不改档位；确认后再调 ApplyProfile 执行。
/// </summary>
public sealed record ProfileApplyPlan(
    string Profile, string ProfileLabel, double CeilingC,
    double CurrentTempC, double TargetPl1W, double TargetPl2W, double TargetTauS,
    double DeltaW, bool GatesOpen, string GateReason,
    bool NeedConfirmation, string ConfirmReason, bool HardBlocked,
    double CooldownRemainingS, bool LeaveManualMode, bool HasSavedManual);

/// <summary>一次下发的结果。Ok=true 仅代表「寄存器写入 + 回读校验成功」，不代表性能已收敛。</summary>
public sealed record WriteOutcome(bool Ok, string Message, int StepPl1W, bool Confirmed = false);

/// <summary>界面状态分级，驱动顶部状态条与侧栏角标。</summary>
public enum StatusLevel { Ok, Info, Warn, Danger }

/// <summary>两段式标定结果，交给界面决定是否应用推荐档位。</summary>
public sealed record CalibrationOutcome(
    bool Ok,
    double Rth,
    double IdleC,
    double HotC,
    double IdleW,
    double HotW,
    double BasisW,
    double PredictedAtMaxC,
    string Recommended,
    string Message);

/// <summary>
/// 会话状态：1Hz 主循环 + 历史缓冲 + 事件中心。
/// 页面全部以本对象为 DataContext，研发扩展视图不需要改这里。
/// </summary>
public sealed class SessionState : INotifyPropertyChanged, IDisposable
{
    public const double AmbientC = 25.0;
    private const int HistoryCap = 300;
    private const int TableCap = 120;
    private const int LogCap = 100;

    private readonly DispatcherTimer _timer = new();
    /// <summary>
    /// 遥测在启动后台线程里创建（枚举传感器约 2 秒）。为 null 表示「正在读取」，
    /// 此时采样直接跳过，界面显示「正在读取」而不是转圈卡住。
    /// </summary>
    private HardwareTelemetry? _telemetry;
    private RaplPowerMeter? _pendingRapl;
    private readonly AutoCpuController _controller;
    private readonly Dictionary<string, DateTime> _alertDebounce = new();
    private MsrGuard? _guard;
    private int _autoWriteFailures;
    private string _lastWriteText = "本次会话尚未下发任何写入";
    private WriteScheduler _scheduler = null!;
    private readonly ThermalEventRecorder _thermalEvents = new();

    /// <summary>
    /// v0.5.0：模式不再由「是否存在 L3 手动值」隐式推导（旧版 _manualLock 会静默锁死智能档位）。
    /// 单一真值源就是 Settings.TuningMode；手工模式下决策树只演算、不写寄存器。
    /// </summary>
    private bool IsManualMode =>
        string.Equals(Settings.TuningMode, TuningModes.Manual, StringComparison.OrdinalIgnoreCase);

    /// <summary>固件实际 PL1（0x610 读出）；0 表示读不到。</summary>
    private double _firmwarePl1W;
    /// <summary>固件当前 PL1/PL2/Tau（0x610 实读）与 EPP（0x774 [31:24] 实读）。</summary>
    private RaplLimits? _fwLimits;
    private double _fwEpp = double.NaN;
    private TurboRatioState? _fwTurbo;
    private HwpRequestState? _fwHwp;
    /// <summary>本次会话已成功下发的电压偏移（mV，0 = 未施加）。仅内存态，重启即失效。</summary>
    private int _appliedVoltageMv;
    private readonly List<RawSample> _raw = new();

    // 统一时序图：概览页的唯一监控数据源。各序列索引对齐，不可用写 NaN。
    private readonly MetricSeries _sTemp = new("temp", "温度", "°C", C("#B0453C"), 2.2,
        tooltip: "CPU 封装温度实测；右上角显示温度天花板标注");
    private readonly MetricSeries _sPl1 = new("pl1", "目标功耗", "W", C("#185FA5"), 1.4, dashed: true,
        tooltip: "决策树演算目标（非寄存器实值）");
    private readonly MetricSeries _sPower = new("power", "包功耗", "W", C("#A9702A"), 2.0,
        tooltip: "硬件功耗计量实测");
    private readonly MetricSeries _sCpu = new("cpu", "CPU 负载", "%", C("#534AB7"), 1.4,
        fixedMin: 0, fixedMax: 100, format: "F0",
        tooltip: "Windows 系统计时 · CPU 时间占比 0~100%（与任务管理器同口径）");
    private readonly MetricSeries _sGpu = new("gpu", "GPU 负载", "%", C("#639922"), 1.4,
        fixedMin: 0, fixedMax: 100, format: "F0",
        tooltip: "NVAPI / D3D · GPU Core 活动率（与任务管理器 GPU Engine 引擎利用率口径不同）");
    private readonly MetricSeries _sGpuMem = new("gpumem", "GPU 显存", "%", C("#888780"), 1.4,
        fixedMin: 0, fixedMax: 100, format: "F0",
        tooltip: "独显专用显存 Used/Total（不含任务管理器计入的共享显存）");
    private readonly MetricSeries _sFan = new("fan", "风扇档位", "%", C("#0F6E56"), 2.6,
        fixedMin: 0, fixedMax: 100, format: "F0",
        tooltip: "EC 只读档位刻度（非物理转速 RPM）；原始值连续不变时标注「疑似非实时」；软件不写 EC/PWM");
    private MetricSeries? _sFanRpm;
    public ObservableCollection<MetricSeries> Metrics { get; } = new();

    // 风扇读数「疑似非实时」时用警示色，其余用弱化色；纯 UI 状态，freeze 后可跨线程绑定。
    private static readonly Brush FanWarnBrush = C("#A9702A");
    private static readonly Brush FanMutedBrush = C("#8C9A95");

    private readonly record struct RawSample(
        DateTime At, double Temp, double Load, double Power, double Pl1, string Rule,
        double FanPct, double FanRaw);

    private StressLoad? _stress;
    private int _stressThreads = -1;
    private StressKernel _stressKernel = StressKernel.ScalarFloat;
    private double _stressDuty = 1.0;
    private double _prevTemp = double.NaN;
    // 稳态预测基线：会话内最低功耗（且温度不再上升）的轻载样本
    private double _basePowerW = double.NaN;
    private double _baseTempC = double.NaN;
    private int _noTempTicks;

    private RaplPowerMeter? _rapl;

    // 两段式标定：轻载基线 → 满载升温 → 稳态采样
    private enum CalPhase { None, Baseline, Ramp, Plateau }

    private CalPhase _calPhase = CalPhase.None;
    private int _calPhaseTicks;
    private int _calBaselineTicks;
    private int _calRampMaxTicks;
    private int _calPlateauTicks;
    private readonly List<(double T, double P)> _calIdlePts = new();
    private readonly List<(double T, double P)> _calHotPts = new();
    private readonly Queue<double> _calRecentTemps = new();

    /// <summary>
    /// 命令行自检 / 截图 / 冒烟自检用：同步准备好传感器，行为与旧版一致。
    /// 图形界面走 <see cref="SessionState(HardwareTelemetry?, CapabilitySnapshot?)"/>，由启动编排决定是否异步。
    /// </summary>
    public SessionState() : this(new HardwareTelemetry(), null) { }

    /// <summary>
    /// 由启动编排注入已经打开的遥测实例与已确认的能力快照：
    /// · telemetry 非空 → 直接复用，不再第二次枚举传感器（首次运行路径）；
    /// · telemetry 为空 → 后台线程创建，界面先出来（缓存命中路径）；
    /// · capability 非空 → 直接采用，不再重跑只读自检。
    /// </summary>
    public SessionState(HardwareTelemetry? telemetry, CapabilitySnapshot? capability)
    {
        Settings = SettingsStore.Load();
        ResolveTuningMode();
        Environment = HardwareProbe.Probe();
        CpuCaps = ThermalTuner.Core.Taxonomy.CpuCapability.Probe(Environment.BrandString);
        _controller = new AutoCpuController(HardwareLimitResolver.Resolve(Environment));
        _scheduler = new WriteScheduler(
            _controller.Limits.MinPL1, _controller.Limits.MaxPL1,
            Settings.RampUpCooldownSeconds);
        LoadPersistedCalibration();
        IsElevated = CheckElevated();
        // 档位与天花板必须一起从持久化设置恢复。此前只恢复了 CeilingC，
        // _profile 永远是字段默认值 balanced，重启后出现「档位 均衡 · 天花板 85°C」，
        // 且决策树拿到的是均衡的 EPP/PL2/Tau 而不是用户选的静音/性能。
        string saved = (Settings.Profile ?? "balanced").Trim().ToLowerInvariant();
        Profile = saved is "silent" or "performance" ? saved : "balanced";
        CeilingC = Math.Min(Settings.CeilingC, Limits.MaxSafeCeiling);
        InitMsr();
        InitMetrics();

        _timer.Interval = TimeSpan.FromMilliseconds(Settings.IntervalMs);
        _timer.Tick += (_, _) => Tick();

        if (capability is not null)
        {
            ApplyCapability(capability);
        }
        else
        {
            // 命令行自检 / 截图 / 冒烟自检没有初始化页，走这条兼容路径：
            // 有可用快照就复用（不再每次启动重跑），否则同步跑一次只读自检。
            InitializeEnvironmentCheck();
        }

        if (telemetry is not null) AttachTelemetry(telemetry);
        else BeginTelemetryWarmup();
    }

    /// <summary>接上遥测实例（可能来自启动编排，也可能来自后台线程）。</summary>
    private void AttachTelemetry(HardwareTelemetry telemetry)
    {
        _telemetry = telemetry;
        if (_pendingRapl is not null) telemetry.AttachRapl(_pendingRapl);
        _sensorsReady = true;
        N(nameof(SensorsReady));
        N(nameof(SensorsWarming));
        N(nameof(SensorStatusText));
        N(nameof(DriverAvailable));
        N(nameof(SensorCount));
        N(nameof(StatusText));
        N(nameof(StatusLevel));
    }

    private volatile bool _sensorsReady;

    /// <summary>
    /// 后台准备传感器：Computer.Open 会枚举全部硬件（约 2 秒），不能压在首屏上。
    /// 失败也让状态变为「已尝试」，避免界面永远停在「正在读取」。
    /// </summary>
    private void BeginTelemetryWarmup()
    {
        Task.Run(() =>
        {
            HardwareTelemetry? created = null;
            try { created = new HardwareTelemetry(); }
            catch { /* 失败按不可用处理，界面显示 N/A */ }

            _sensorsReady = true;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Apply()
            {
                if (created is not null) AttachTelemetry(created);
                else
                {
                    N(nameof(SensorsReady));
                    N(nameof(SensorsWarming));
                    N(nameof(SensorStatusText));
                    N(nameof(StatusText));
                    N(nameof(StatusLevel));
                }
                AddAlert("info", "监测已就绪", "传感器读取完成，开始按采样间隔刷新指标。");
            }
            if (dispatcher is null || dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke(Apply);
        });
    }

    public bool SensorsReady => _sensorsReady;
    public bool SensorsWarming => !_sensorsReady;
    public string SensorStatusText => _sensorsReady ? "传感器就绪" : "正在读取传感器…";

    /// <summary>
    /// 存量配置迁移（v0.5.0）：旧版 settings.json 没有 TuningMode 字段。
    /// 有 L3 手动值 → manual（保持旧行为：重放手动值、决策树不接管），无值 → intelligent。
    /// 只在内存归一，不在这里落盘：离屏截图 / 冒烟自检构造会话时不能写 settings.json。
    /// 用户切档位、改设置或正常退出时自然会保存。
    /// </summary>
    private void ResolveTuningMode()
    {
        bool missing = string.IsNullOrWhiteSpace(Settings.TuningMode);
        Settings.TuningMode = missing
            ? (Settings.Manual is not null ? TuningModes.Manual : TuningModes.Intelligent)
            : TuningModes.Normalize(Settings.TuningMode);

        if (Settings.RampUpCooldownSeconds <= 0)
            Settings.RampUpCooldownSeconds = WriteScheduler.DefaultCooldownSeconds;
        Settings.RampUpCooldownSeconds = Math.Clamp(Settings.RampUpCooldownSeconds,
            WriteScheduler.MinCooldownSeconds, WriteScheduler.MaxCooldownSeconds);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>每拍结束后触发，供页面刷新图表等自绘元素。</summary>
    public event Action? Ticked;

    /// <summary>标定结束（成功或失败）时触发，界面据此询问是否应用推荐档位。</summary>
    public event Action<CalibrationOutcome>? CalibrationFinished;

    // ---------------- 静态信息 ----------------

    public AppSettings Settings { get; }
    public CpuEnvironment Environment { get; }
    public HardwareLimits Limits => _controller.Limits;
    public AutoCpuController Controller => _controller;
    public bool DriverAvailable => _telemetry?.DriverAvailable == true;
    public int SensorCount => _telemetry?.SensorCount ?? 0;
    public bool IsElevated { get; }

    public string CpuName => string.IsNullOrWhiteSpace(Environment.BrandString) ? "未知 CPU" : Environment.BrandString;
    public string PlatformText => Environment.Generation switch
    {
        0 => "未知平台",
        >= 100 => $"Core Ultra（{Environment.Generation} 系）",
        _ => $"{Environment.Vendor} Gen {Environment.Generation}"
    };
    public string FormText => Environment.FormFactor switch
    {
        CpuFormFactor.MobileUltraLowPower => "低功耗移动",
        CpuFormFactor.MobilePerformance => "高性能移动",
        CpuFormFactor.MobileMainstream => "主流移动",
        CpuFormFactor.Desktop => "桌面",
        CpuFormFactor.Server => "服务器",
        _ => "未识别"
    };
    public string DriverText => DriverAvailable ? "驱动就绪" : "驱动不可用";
    public bool RaplAvailable => _rapl?.Available == true;
    public string RaplText => _rapl?.Detail ?? "未接入（调优通道不可用）";
    public string ElevationText => IsElevated ? "管理员" : "普通用户";
    public string CoreText => $"{Environment.PhysicalCores}P / {Environment.LogicalCores}L" +
                              (Environment.IsHybridArchitecture ? " · 混合架构" : string.Empty);
    public string TjMaxText => $"TjMax {Limits.TjMax:F0}°C · 安全上限 {Limits.MaxSafeCeiling:F0}°C";
    public string PlRangeText => $"持续上限 {Limits.MinPL1:F0}–{Limits.MaxPL1:F0}W · 短时上限 {Limits.MinPL2:F0}–{Limits.MaxPL2:F0}W";

    /// <summary>
    /// 本机默认稳态功率：0x610 实读的出厂默认 PL1。
    /// 通用产品不预设任何机型瓦数——读到的就是这台机器的默认功率；读不到则为 NaN，绝不拿查表值冒充。
    /// </summary>
    public double DefaultPowerW
    {
        get
        {
            if (_guard?.StartupBackup is { } b && b.Pl1W > 0) return b.Pl1W;
            return _fwLimits is { } lim && lim.Pl1W > 0 ? lim.Pl1W : double.NaN;
        }
    }
    public string DefaultPowerText => double.IsNaN(DefaultPowerW) ? "--" : DefaultPowerW.ToString("F1");

    /// <summary>概览「决策 PL1」卡副标题：值缺失时明确写不可读，不标「实读」。</summary>
    public string FirmwarePl1Caption => double.IsNaN(DefaultPowerW)
        ? "出厂值不可读 · 目标为演算值"
        : $"出厂值 {DefaultPowerW:F1} W · 目标为演算值";

    /// <summary>预测稳态用的功耗基准：取实读 PL1（当前生效的默认功率），读不到才退回平台上限。</summary>
    public double PowerBasisW => _firmwarePl1W > 0
        ? _firmwarePl1W
        : (double.IsNaN(DefaultPowerW) ? Limits.MaxPL1 : DefaultPowerW);
    public string PowerBasisText => $"{PowerBasisW:F0}W";

    // ---------------- 固件实读值（高级参数页「当前生效」列） ----------------
    // 与决策目标值区分：只读遥控下两者不同，页面必须显示 MSR 实读的真值

    public string FwPl1Text => _fwLimits?.Pl1W.ToString("F1") ?? "--";
    public string FwPl2Text => _fwLimits?.Pl2W.ToString("F1") ?? "--";
    public string FwTauText => _fwLimits is null ? "--" : FormatTau(_fwLimits.Value.TauS);
    public string FwEppText => double.IsNaN(_fwEpp) ? "--" : _fwEpp.ToString("0");

    private static string FormatTau(double tau)
        => tau >= 3600 ? $"{tau:0} s（≈{tau / 3600:F1}h）" : tau.ToString("0.#");

    /// <summary>决策目标与固件实值的分歧说明（高级参数页顶部提示）。</summary>
    public string FwDivergeText
    {
        get
        {
            if (_fwLimits is null) return "固件实值不可读，当前生效列显示 --。";
            var def = _guard?.StartupBackup;
            string origin = def is not null && Math.Abs(def.Value.Pl1W - _fwLimits.Value.Pl1W) > 0.5
                ? "当前生效值已偏离固件出厂默认（软件重放或外部工具写入）"
                : "当前生效值即固件出厂默认";
            if (double.IsNaN(_pl1W) || _pl1W <= 0)
                return $"固件实读 PL1 {_fwLimits.Value.Pl1W:F1}W；决策目标尚未产生（等待首拍采样）。{origin}";
            double d = _fwLimits.Value.Pl1W - _pl1W;
            return Math.Abs(d) < 0.5
                ? $"决策目标与固件实值一致（{origin}）。"
                : $"决策目标 PL1 {_pl1W:F1}W，固件实值 {_fwLimits.Value.Pl1W:F1}W —— " +
                  (RealWriteEnabled ? "决策尚未写入固件（等待授权/下一拍）" : "只读遥控下目标不写入") + $"；{origin}";
        }
    }

    /// <summary>固件出厂默认（首次启动时备份，之后不再覆盖）；重放后「当前生效」会变，此锚点不变。</summary>
    public string FwDefaultText
    {
        get
        {
            var b = _guard?.StartupBackup;
            return b is null
                ? "固件出厂默认：--（启动时备份不可读）"
                : $"固件出厂默认（启动时备份）：PL1 {b.Value.Pl1W:F1}W · PL2 {b.Value.Pl2W:F1}W · Tau {b.Value.TauS:0.#}s";
        }
    }

    /// <summary>EPP 是否可编辑：0x774 在本后端完全不可访问时禁用，避免把决策默认值当成用户微调值保存。</summary>
    public bool EppEditable => !double.IsNaN(_fwEpp);
    public string EppHintText => EppEditable
        ? string.Empty
        : "本机读不到「能效偏好」这组参数，相关项已禁用；功耗三项不受影响。";

    // 「合法范围」列：不可读时直接说明不可读，而不是继续展示 [0,255] 让人以为能改。
    public string EppRangeText => EppEditable ? "[0, 255]" : "本机不可读";
    public string MinPerfRangeText => _fwHwp is { } h
        ? $"[0, {h.MaxPerf}] · 且 ≤ Max Perf" : "本机不可读";
    public string MaxPerfRangeText => _fwHwp is { } h
        ? $"[{h.MinPerf}, {h.MaxPerf}] · 不做超频" : "本机不可读";
    public string ActivityWindowRangeText => _fwHwp is null ? "本机不可读" : "[0, 1023]";

    // ---- 睿频倍率（MSR 0x1AD）：读得到 ≠ 写得了，两者都要满足才允许编辑 ----
    public bool TurboEditable => _fwTurbo is not null && (_guard?.TurboWritable ?? false);
    public string FwTurboText => _fwTurbo is { } t ? TurboRatioLimit.ShortTiers(t) : "--";
    public int FwTurboAllCoreRatio => _fwTurbo?.AllCore ?? 0;
    public int FwTurboMaxRatio => _fwTurbo?.MaxTier ?? 0;
    public string TurboRangeText => _fwTurbo is { } t
        ? $"[{TurboRatioLimit.MinRatio}, {t.MaxTier}] ×100MHz"
        : "--（本机不可读）";
    public string TurboHintText => _fwTurbo is not { } t
        ? "本机读不到睿频倍率，相关调节已禁用。"
        : _guard?.TurboWritable != true
            ? $"出厂档位 1C {t.OneCoreGhz:F1} / 全核 {t.AllCoreGhz:F1} GHz（只读）。" +
              "本机不支持改写睿频倍率表；" +
              "功耗与温度控制不受影响。"
            : $"出厂档位 1C {t.OneCoreGhz:F1} / 全核 {t.AllCoreGhz:F1} GHz；" +
              $"允许在 [8, {t.MaxTier}] 内做规格内档位配置（不做超频）。" +
              "本机 A/B 实测：拉平到 38x 后全核频率无提升，仅增加功耗与温度，不作为性能卖点。";

    // 供编辑页做基线对比；NaN = MSR 读不到，退回决策值
    public double FwPl1W => _fwLimits?.Pl1W ?? double.NaN;
    public double FwPl2W => _fwLimits?.Pl2W ?? double.NaN;
    public double FwTauS => _fwLimits?.TauS ?? double.NaN;
    public double FwEpp => _fwEpp;

    // ---- 三层参数模型：L1 固件固化 / L2 智能标定推荐 / L3 用户编辑值 ----

    /// <summary>CPU 能力预筛：型号判断只是提示，最终能力一律以写入自检实测为准。</summary>
    public ThermalTuner.Core.Taxonomy.CpuCapability CpuCaps { get; }

    /// <summary>0x774 当前实读（固件固化 + 软件重放后的当前生效值）。</summary>
    public HwpRequestState? FwHwp => _fwHwp;
    public string FwMinPerfText => _fwHwp?.MinPerf.ToString() ?? "--";
    public string FwMaxPerfText => _fwHwp?.MaxPerf.ToString() ?? "--";
    public string FwActivityWindowText => _fwHwp?.ActivityWindow.ToString() ?? "--";
    public bool HwpEditable => _fwHwp is not null;
    /// <summary>出厂 Max Perf 上限：Max Perf 不得超过它（规格内调参，不做超频）。</summary>
    public int FwHwpMaxPerf => _fwHwp?.MaxPerf ?? 0;

    /// <summary>
    /// 个性化可选微调是否可编辑：0x150 可读 + 型号预筛不排除 + 实机自检未判不可用。
    /// 型号只是提示；--voltage-probe 明确判 false 时 UI 直接禁用，避免用户白试。
    /// </summary>
    public bool VoltageEditable => (_guard?.VoltageReadable ?? false)
        && CpuCaps.VoltageOffsetPlausible
        && Settings.VoltageWritable != false;
    public int AppliedVoltageMv => _appliedVoltageMv;
    public string VoltageHintText => (Settings.VoltageWritable switch
    {
        true => "本机自检：电压偏移可写，可以使用。",
        false => "本机自检：本机拒绝电压偏移写入，该项已禁用；可到「诊断与日志」重新检测复核。",
        _ => "本机尚未验证过：写入可能被拒，失败会自动回零。"
    }) + CpuCaps.VoltageHint +
        "；写入易失寄存器，重启或退出程序即恢复，属相对安全但可能导致不稳定。";
    public string CpuCapabilityText => CpuCaps.MatrixText;

    /// <summary>
    /// 0x774 平台限制说明：本机实测（2026-11-05 --hwp-probe）写入瞬时成功、250ms 后
    /// 被系统电源策略 / Intel DTT 覆盖回原值。因此 HWP 项可编辑但不保证持续生效，
    /// L2 智能标定只对 0x610 的 PL1/PL2/Tau 做稳定推荐。
    /// </summary>
    public string HwpHintText =>
        "通用参数提示：能效偏好、性能档与响应窗口写入瞬时可成功，但部分平台会被 Windows 电源计划" +
        "周期性覆盖（本机实测 250ms 内即被还原）。改完可到「诊断与日志」点「重新检测」复核；" +
        "自动推荐的稳定范围只覆盖功耗三项。";

    /// <summary>L2 智能标定推荐（只含通用安全参数，不碰电压/睿频/温度墙）。</summary>
    public string CalibrationLayerText => Settings.Calibration is { } c
        ? $"自动推荐值：热阻 {c.Rth:F2} K/W" +
          (c.RecommendedPl1W > 0 ? $" · 建议持续功耗 {c.RecommendedPl1W:F1}W" : string.Empty) +
          (c.RecommendedEpp >= 0 ? $" · 能效偏好 {c.RecommendedEpp}（该项可能被系统电源策略覆盖，仅供参考）" : string.Empty) +
          $" · 电压上下文 {c.VoltageContextMv} mV · {c.SavedAt:yyyy-MM-dd HH:mm}" +
          " · 稳定推荐只覆盖功耗三项"
        : "自动推荐值：尚无，完成一次两段式检测后生成（只给出通用安全参数）";

    /// <summary>L3 用户编辑值说明：重新标定只更新 L2，绝不覆盖这里。</summary>
    public string ManualLayerText => Settings.Manual is not null
        ? "我的设置已保存；重新检测只更新推荐值，不会覆盖你的手动微调。"
        : "我的设置：尚无；在「手工微调」页应用后才会保存，并在下次启动时重放。";

    // ---- 首次安装启动的环境自检：能力快照决定 UI 哪些参数项不可编辑 ----

    private CapabilitySnapshot? _capability;
    public CapabilitySnapshot? Capability => _capability;
    public string CapabilitySummaryText => _capability?.Summary ?? "尚未完成环境自检";
    /// <summary>高级参数页常驻的一行状态；详情一律放诊断页，不在 UI 堆叠说明。</summary>
    public string CapabilityOneLineText => _capability is { } c
        ? $"自检 {c.Readable.Count(x => x.Value)}/{c.Readable.Count} 项可获取 · 禁用 {c.DisabledItems.Count} 项" +
          (c.InstallHints.Count > 0 ? $" · {c.InstallHints.Count} 条依赖提示见诊断页" : string.Empty)
        : "自检未完成";
    public string DependencyHintText => _capability?.DependencyText ?? "尚未完成环境自检";
    public string DisabledItemsText => _capability?.DisabledText ?? "尚未完成环境自检";
    public string CapabilityReportText => _capability is { } c ? EnvironmentSelfCheck.Report(c) : string.Empty;

    private string TelemetryHint =>
        $"温度 {(TempAvailable ? "可读" : "不可读")} · 功耗来源 {PowerCaption} · {RaplText}";
    /// <summary>遥测是否真的采到温度与实测功耗（供环境自检判定可读性）。</summary>
    public bool TempReadable => _tempAvailable;
    public bool PowerMeasured => _powerFromDriver;
    private (bool Temp, bool Power) TelemetryFlags => (TempReadable, PowerMeasured);

    /// <summary>待判定：等第一拍真实读数出来后再跑只读自检（可读性必须实测，不能用默认值猜）。</summary>
    private bool _capabilityPending;

    /// <summary>
    /// 命令行 / 截图 / 冒烟路径的能力快照准备：先用磁盘上的有效快照（按指纹校验），
    /// 快照失效时挂起，等第一拍真实读数出来后由 <see cref="ResolvePendingCapability"/> 补做。
    /// </summary>
    private void InitializeEnvironmentCheck()
    {
        if (EnvironmentSelfCheck.TryLoadValid(out CapabilitySnapshot? cached, out _) && cached is not null)
        {
            ApplyCapability(cached);
            return;
        }
        _capabilityPending = true;
    }

    private void ResolvePendingCapability()
    {
        if (!_capabilityPending) return;
        _capabilityPending = false;
        try
        {
            var f = TelemetryFlags;
            // 只读自检：无 UAC、不写任何寄存器
            ApplyCapability(EnvironmentSelfCheck.Run(false, TelemetryHint, f.Temp, f.Power));
        }
        catch (Exception ex)
        {
            AddAlert("warn", "环境自检未完成", ex.Message + "；参数项禁用判断可能不准，可到「诊断与日志」重新检测。");
        }
    }

    /// <summary>采用一份能力快照，并补齐通知与首次自检提示。</summary>
    public void ApplyCapability(CapabilitySnapshot snap)
    {
        _capability = snap;
        _capabilityPending = false;

        bool first = !Settings.EnvironmentChecked && !EnvironmentSelfCheck.IsCommandLineMode;
        if (first)
        {
            Settings.EnvironmentChecked = true;
            try { SettingsStore.Save(Settings); } catch { /* 落盘失败不影响本次运行 */ }
            int hints = snap.InstallHints.Count;
            AddAlert(hints > 0 ? "warn" : "info", "首次环境自检",
                (hints > 0
                    ? $"自检完成，发现 {hints} 条外部依赖 / 环境提示，请到「诊断与日志」查看。"
                    : "自检完成，外部依赖齐全。") +
                $" 能力摘要：{snap.Summary}；{snap.DisabledText}");
        }

        N(nameof(Capability));
        N(nameof(CapabilitySummaryText)); N(nameof(DependencyHintText));
        N(nameof(DisabledItemsText)); N(nameof(CapabilityReportText));
        N(nameof(CapabilityOneLineText));
    }

    /// <summary>
    /// 管理员下的完整自检（含无害写入探测：同值写回 / −10mV 后回零）。
    /// 界面「重新自检」按钮调用；非管理员返回提示而不是静默失败。
    /// </summary>
    public string RunFullEnvironmentCheck()
    {
        if (!IsElevated)
            return "需要管理员权限：请先点「以管理员重启」，再运行完整自检。";
        try
        {
            var f = TelemetryFlags;
            var snap = EnvironmentSelfCheck.Run(true, TelemetryHint, f.Temp, f.Power);
            Settings.EnvironmentChecked = true;
            try { SettingsStore.Save(Settings); } catch { /* 落盘失败不影响检测结果 */ }
            ApplyCapability(snap);
            AddAlert("info", "环境自检（含写入探测）", snap.Summary);
            return snap.Summary + "；" + snap.DisabledText;
        }
        catch (Exception ex)
        {
            return "自检失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 固件出厂默认 0x610 基线：MsrGuard 首次启动时备份、永不覆盖。
    /// 固件值本身不可持久化修改；软件退出或未授权时 CPU 按此默认值运转。
    /// 用它区分「出厂默认」与「开机重放后的当前生效值」。
    /// </summary>
    public RaplLimits? FirmwareDefaults => _guard?.StartupBackup;

    /// <summary>直读 MSR 刷新固件实值（只读操作，每拍调用开销可忽略）。</summary>
    private void RefreshFirmwareValues()
    {
        var guard = _guard;
        if (guard is null) return;
        try
        {
            if (guard.Channel.TryReadLimits(out var lim) && lim.Pl1W > 0)
            {
                bool changed = _fwLimits is null || _fwLimits.Value.Raw != lim.Raw;
                _fwLimits = lim;
                _firmwarePl1W = lim.Pl1W;
                if (changed)
                {
                    N(nameof(FwPl1Text)); N(nameof(FwPl2Text)); N(nameof(FwTauText));
                    N(nameof(PowerBasisText)); N(nameof(FwDivergeText));
                }
            }
            if (guard.Channel.TryRead(HwpRequest.Msr, out ulong hwpRaw))
            {
                var h = HwpRequest.Decode(hwpRaw);
                if (_fwHwp is null || _fwHwp.Value.Raw != hwpRaw)
                {
                    _fwHwp = h;
                    N(nameof(FwMinPerfText)); N(nameof(FwMaxPerfText));
                    N(nameof(FwActivityWindowText)); N(nameof(HwpEditable)); N(nameof(FwHwpMaxPerf));
                    N(nameof(MinPerfRangeText)); N(nameof(MaxPerfRangeText));
                    N(nameof(ActivityWindowRangeText));
                }
                double epp = h.Epp;
                if (epp != _fwEpp)
                {
                    _fwEpp = epp;
                    N(nameof(FwEppText)); N(nameof(EppEditable)); N(nameof(EppHintText));
                    N(nameof(EppRangeText));
                }
            }
            if (guard.Channel.TryRead(TurboRatioLimit.MsrTurboRatioLimit, out ulong turboRaw) && turboRaw != 0)
            {
                var t = TurboRatioLimit.Decode(turboRaw);
                if (_fwTurbo is null || _fwTurbo.Raw != turboRaw)
                {
                    _fwTurbo = t;
                    N(nameof(FwTurboText)); N(nameof(TurboEditable)); N(nameof(TurboHintText));
                    N(nameof(TurboRangeText)); N(nameof(FwTurboMaxRatio)); N(nameof(FwTurboAllCoreRatio));
                }
            }
        }
        catch { /* 只读刷新失败静默忽略，界面显示 -- */ }
        N(nameof(FwDefaultText)); N(nameof(FwDivergeText));
    }
    public string NotesText => Environment.Notes.Count == 0 ? "无" : string.Join("；", Environment.Notes);

    // ---------------- 实时值 ----------------

    private double _tempC = double.NaN;
    public double TempC
    {
        get => _tempC;
        private set { _tempC = value; N(nameof(TempC)); N(nameof(TempText)); }
    }
    public string TempText => double.IsNaN(_tempC) ? "N/A" : _tempC.ToString("F1");

    private double _loadPct;
    public double LoadPct
    {
        get => _loadPct;
        private set { _loadPct = value; N(nameof(LoadPct)); N(nameof(LoadText)); N(nameof(LoadBar)); }
    }
    public string LoadText => _loadPct.ToString("F0");
    public double LoadBar => _loadPct;

    private double _powerW;
    public double PowerW
    {
        get => _powerW;
        private set { _powerW = value; N(nameof(PowerW)); N(nameof(PowerText)); }
    }

    private bool _powerFromDriver = true;
    /// <summary>功耗是否实测（RAPL 或 LHM）；否则为按负载粗估（界面必须区分，避免误读）。</summary>
    public bool PowerFromDriver
    {
        get => _powerFromDriver;
        private set
        {
            _powerFromDriver = value;
            N(nameof(PowerFromDriver));
            N(nameof(PowerCaption));
            N(nameof(PowerSourceText));
            N(nameof(CanCalibrate));
            N(nameof(CalibrationBlockedReason));
        }
    }

    private string _powerSource = "未采样";
    public string PowerSource
    {
        get => _powerSource;
        private set { _powerSource = value; N(nameof(PowerSource)); N(nameof(PowerCaption)); N(nameof(PowerSourceText)); }
    }

    public string PowerCaption => _powerFromDriver ? $"包功耗（实测 {_powerSource}）" : "包功耗（估算）";
    /// <summary>包功耗卡副标题：只交代功耗来源，不再错绑 GPU 说明。</summary>
    public string PowerSourceText => _powerFromDriver ? $"实测 · {_powerSource}" : "按负载估算 · 非实测";
    public string PowerText => _powerW <= 0 ? "N/A" : _powerW.ToString("F0");

    private double _clockGhz;
    public double ClockGhz
    {
        get => _clockGhz;
        private set { _clockGhz = value; N(nameof(ClockGhz)); N(nameof(ClockText)); }
    }
    public string ClockText => _clockGhz <= 0 ? "N/A" : _clockGhz.ToString("F2");

    // 首拍前不显示硬编码 35W：决策目标尚未产生时用 "--"，InitMsr 成功后由 0x610 实读注入。
    private double _pl1W = double.NaN;
    public double Pl1W
    {
        get => _pl1W;
        private set { _pl1W = value; N(nameof(Pl1W)); N(nameof(Pl1Text)); N(nameof(FwDivergeText)); }
    }
    public string Pl1Text => double.IsNaN(_pl1W) || _pl1W <= 0 ? "--" : _pl1W.ToString("F1");

    private double _pl2W = double.NaN;
    public double Pl2W
    {
        get => _pl2W;
        private set { _pl2W = value; N(nameof(Pl2W)); N(nameof(Pl2Text)); }
    }
    public string Pl2Text => double.IsNaN(_pl2W) || _pl2W <= 0 ? "--" : _pl2W.ToString("F1");

    private double _tauS = double.NaN;
    public double TauS
    {
        get => _tauS;
        private set { _tauS = value; N(nameof(TauS)); N(nameof(TauText)); }
    }
    public string TauText => double.IsNaN(_tauS) || _tauS <= 0 ? "--" : _tauS.ToString("F0");

    private int _epp = 128;
    public int Epp
    {
        get => _epp;
        private set { _epp = value; N(nameof(Epp)); N(nameof(EppText)); }
    }
    public string EppText => _epp.ToString();

    private double _fanPercent;
    public double FanPercent
    {
        get => _fanPercent;
        private set { _fanPercent = value; N(nameof(FanPercent)); N(nameof(FanText)); }
    }
    public string FanText => _fanPercent <= 0 ? "--" : _fanPercent.ToString("F0");

    // ---- 监控面板扩展指标（全部来自 LHM 实测；NaN = 本机不可读，UI 显示 N/A）----
    private double _gpuLoadPct = double.NaN;
    private double _gpuMemPct = double.NaN;
    private double _gpuMemUsedMb = double.NaN;
    private double _gpuMemTotalMb = double.NaN;
    private double _fanRpm = double.NaN;
    private double _fanMeasuredPct = double.NaN;
    private string _fanSource = string.Empty;
    private double _fanRawValue = double.NaN;
    private int _fanStagnantReads;
    private bool _fanStagnant;
    private string _gpuName = string.Empty;
    private string _gpuSource = string.Empty;
    private double _lhmCpuTotal = double.NaN;   // 诊断对照，不进界面数值/决策树
    private double _gpuTempC = double.NaN;

    public string CpuLoadText => $"{LoadPct:F0}%";
    private string LhmCpuTotalText => double.IsNaN(_lhmCpuTotal) ? "N/A" : $"{_lhmCpuTotal:F0}%";

    /// <summary>CPU 卡 ToolTip：交代系统计时口径与任务管理器两个数字的来历。</summary>
    public string CpuLoadHintText =>
        "CPU 负载 = Windows 系统计时（GetSystemTimes）差分：非空闲时间占比，与任务管理器同一数据源。\n" +
        "任务管理器「利用率」按频率加权，轻载睿频时略高于本值；「速度」是瞬时有效频率，不代表满载。\n" +
        $"备选传感器读数（{LhmCpuTotalText}）为线程级估算，仅作诊断对照，不参与界面数值与决策。";

    public string GpuLoadText => double.IsNaN(_gpuLoadPct) ? "N/A" : $"{_gpuLoadPct:F0}%";
    public string GpuMemoryText => double.IsNaN(_gpuMemPct) ? "N/A" : $"{_gpuMemPct:F0}%";
    public string GpuMemoryDetailText =>
        double.IsNaN(_gpuMemUsedMb) || double.IsNaN(_gpuMemTotalMb)
            ? "专用显存容量不可用"
            : $"专用显存 {_gpuMemUsedMb:F0} / {_gpuMemTotalMb:F0} MB";

    /// <summary>显存卡 ToolTip：与任务管理器的「专用 / 共享 / 合计」三种口径对齐说明。</summary>
    public string GpuMemoryHintText =>
        "本卡按独显专用显存 Used/Total 计算，对应任务管理器的「专用 GPU 内存」。\n" +
        "任务管理器「GPU 内存」= 专用显存 + 共享显存（后者由系统内存映射），分母不同，百分比不可直接对比；共享显存不由本工具采集。\n" +
        "N/A 表示来源不可用，不用演算值冒充。";

    /// <summary>GPU 负载卡副标题：温度 + 口径标签，显卡全名放 ToolTip。</summary>
    public string GpuCardSubtitleText
    {
        get
        {
            string t = double.IsNaN(_gpuTempC) ? "温度 N/A" : $"GPU Core {_gpuTempC:F0}°C";
            string src = string.IsNullOrEmpty(_gpuSource) ? "来源不可用"
                : _gpuSource.Contains("NVAPI") ? "NVAPI 口径" : "D3D 口径";
            return $"{t} · {src}";
        }
    }
    /// <summary>风扇实测值；与决策树的「演算建议值（未下发）」严格区分。</summary>
    public string FanMeasuredText => !double.IsNaN(_fanRpm) ? $"{_fanRpm:F0} RPM"
        : !double.IsNaN(_fanMeasuredPct) ? $"{_fanMeasuredPct:F0}%" : "N/A";

    /// <summary>EC 原始字节；读不到显示破折号，绝不拿换算后的百分比倒推。</summary>
    public string FanRawText => double.IsNaN(_fanRawValue) ? "—" : _fanRawValue.ToString("F0");

    /// <summary>
    /// 风扇卡副标题：给出恒定检测结果。连续多次真实采样原始值不变时明确标注
    /// 「疑似非实时」，避免把恒定档位刻度误读成实时转速。
    /// </summary>
    public string FanMeasuredHintText
    {
        get
        {
            bool has = !double.IsNaN(_fanRpm) || !double.IsNaN(_fanMeasuredPct);
            if (!has) return "无可用风扇读数 · 非 RPM";
            if (_fanStagnant)
                return $"疑似非实时（连续 {_fanStagnantReads} 次不变）· 非 RPM";
            return (string.IsNullOrEmpty(_fanSource) ? "只读" : _fanSource) + " · 非 RPM";
        }
    }

    /// <summary>恒定警示态用 Warn 色，其余用弱化色。</summary>
    public Brush FanHintBrush => _fanStagnant ? FanWarnBrush : FanMutedBrush;

    /// <summary>风扇指标 ToolTip：交代原始 EC 值、换算与实时性判定，不占界面行数。</summary>
    public string FanDetailText
    {
        get
        {
            if (double.IsNaN(_fanMeasuredPct) && double.IsNaN(_fanRpm))
                return "当前没有可用的风扇读数；不拿决策树的演算建议值冒充实测。";

            string head = !double.IsNaN(_fanRawValue)
                ? $"EC 原始值 {_fanRawValue:F0} → 档位 {_fanMeasuredPct:F1}%（按生效配置 Min/Max 线性换算并钳到 0~100）。"
                : "来源：NBFC 服务按生效配置换算的档位百分比（未取得原始字节）。";
            string stability = _fanStagnant
                ? $"连续 {_fanStagnantReads} 次采样原始值不变，读数可能非实时，不能据此判断风扇是否在转。"
                : "原始值逐拍采集，约每 2.2 秒一次。";
            return head + "\n" + stability +
                   "\n本机无转速传感器；该值是 EC 档位刻度，软件只读不写。";
        }
    }
    public string GpuSourceText => string.IsNullOrEmpty(_gpuSource) ? "未检测到 GPU" : _gpuSource;
    public string GpuNameText => string.IsNullOrEmpty(_gpuName) ? "未检测到 GPU" : _gpuName;
    public string GpuHintText =>
        $"{GpuNameText} · {GpuSourceText}\n" +
        "主指标取整卡核心活动率（NVAPI GPU Core，核显回退 D3D 引擎），与温度 / 功耗同源，最贴近散热判断。\n" +
        "任务管理器取 PDH GPU Engine 的分引擎、分进程利用率（某些机型一次可枚举数百个实例），口径不同，数值不必逐拍一致。\n" +
        "显存按专用显存 Used/Total 计算，不含共享显存；N/A 表示来源不可用，不用演算值冒充。";

    private double _rth = double.NaN;
    public double Rth
    {
        get => _rth;
        private set { _rth = value; N(nameof(Rth)); N(nameof(RthText)); N(nameof(HasRth)); N(nameof(RthCalibrated)); N(nameof(RthSourceText)); }
    }
    public bool HasRth => !double.IsNaN(_rth);
    public string RthText => double.IsNaN(_rth) ? "--" : _rth.ToString("F2");

    /// <summary>热阻来源：标定稳态值 / 在线拟合 / 未测得。</summary>
    public bool RthCalibrated => _controller.HasCalibratedR;
    public string RthSourceText => RthCalibrated ? "稳态标定值" : HasRth ? "在线拟合" : "待测量";

    private double _predicted = double.NaN;
    public double PredictedSteadyC
    {
        get => _predicted;
        private set { _predicted = value; N(nameof(PredictedSteadyC)); N(nameof(PredictedText)); }
    }
    public string PredictedText => double.IsNaN(_predicted) ? "--" : _predicted.ToString("F1");

    private string _predictedBasis = "--";
    /// <summary>预测稳态所用的功耗基准，如「按 45W 外推」。</summary>
    public string PredictedBasisText
    {
        get => _predictedBasis;
        private set { _predictedBasis = value; N(nameof(PredictedBasisText)); }
    }

    /// <summary>预测所用的轻载基线，让「预测稳态」可手工复算：T_idle + (P_target − P_idle)·R_th。</summary>
    public string PredictedBaselineText => double.IsNaN(_basePowerW) || double.IsNaN(_baseTempC)
        ? "轻载基线：采集中"
        : $"轻载基线 {_baseTempC:F1}°C @ {_basePowerW:F1}W";

    private string _thermalCapText = "标定前馈未启用：完成两段式标定后，热模型会按天花板钳制决策上限";
    /// <summary>标定值是否真的参与了决策（钳制 PL1 上限），给用户可验证的闭环说明。</summary>
    public string ThermalCapText
    {
        get => _thermalCapText;
        private set { _thermalCapText = value; N(nameof(ThermalCapText)); }
    }

    private string _lastRule = "尚未决策";
    public string LastRule
    {
        get => _lastRule;
        private set { _lastRule = value; N(nameof(LastRule)); }
    }

    private string _lastReason = "开始监控后，每拍会给出可解释的决策凭证。";
    public string LastReason
    {
        get => _lastReason;
        private set { _lastReason = value; N(nameof(LastReason)); }
    }

    private string _lastPath = string.Empty;
    public string LastPath
    {
        get => _lastPath;
        private set { _lastPath = value; N(nameof(LastPath)); }
    }

    // ---------------- 运行态 ----------------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; N(nameof(IsRunning)); N(nameof(RunButtonText)); }
    }
    public string RunButtonText => _isRunning ? "停止监控" : "开始监控";

    private bool _tempAvailable;
    public bool TempAvailable
    {
        get => _tempAvailable;
        private set
        {
            _tempAvailable = value;
            N(nameof(TempAvailable));
            N(nameof(StatusText));
            N(nameof(StatusLevel));
            N(nameof(StatusAction));
            N(nameof(CanCalibrate));
            N(nameof(CalibrationBlockedReason));
            N(nameof(CalibrationGateHint));
            N(nameof(CalibrationGateVisibility));
        }
    }

    private bool _isCalibrating;
    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            _isCalibrating = value;
            N(nameof(IsCalibrating));
            N(nameof(CanCalibrate));
            N(nameof(CalibrationText));
            N(nameof(CalibrationBlockedReason));
            N(nameof(CalibrationGateHint));
            N(nameof(CalibrationGateVisibility));
        }
    }

    private double _calibrationProgress;
    public double CalibrationProgress
    {
        get => _calibrationProgress;
        private set { _calibrationProgress = value; N(nameof(CalibrationProgress)); N(nameof(CalibrationText)); }
    }

    /// <summary>标定是否可启动：需要温度与<b>实测</b>功耗，估算功耗下两点法没有意义。</summary>
    public bool CanCalibrate => !_isCalibrating && _tempAvailable && _powerFromDriver;

    public string CalibrationBlockedReason
    {
        get
        {
            if (_isCalibrating) return "标定进行中";
            if (!_tempAvailable) return "缺少温度读数：请以管理员身份重新启动，并确认调优组件可用";
            if (!_powerFromDriver) return MsrStatus switch
            {
                MsrStatus.NoAdmin => "缺少实测功耗：普通权限读不到功耗，请点「以管理员重启」",
                MsrStatus.NotInstalled => "缺少实测功耗：调优组件未安装，可在「诊断与日志」页安装",
                MsrStatus.DriverUnavailable => "缺少实测功耗：调优组件打不开，详见「诊断与日志」",
                MsrStatus.ModuleUnavailable => "缺少实测功耗：调优组件不完整或为只读版本",
                _ => "缺少实测功耗：功耗来源不可用，估算值无法用于稳态检测"
            };
            return "可以开始标定";
        }
    }

    /// <summary>非管理员时提示提权重启（真实功耗与 MSR 读写都需要管理员）。</summary>
    public System.Windows.Visibility ElevationHintVisibility =>
        IsElevated ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>以管理员身份重启当前程序；用户取消 UAC 时返回 false。</summary>
    public bool RestartElevated()
    {
        try
        {
            string? exe = System.Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            System.Diagnostics.Process.Start(psi);

            // 单实例互斥：新进程起来后主动退出旧实例
            System.Windows.Application.Current?.Shutdown();
            return true;
        }
        catch
        {
            return false;   // 用户点了「否」或 UAC 被策略拦截
        }
    }

    public string CalibrationText => _isCalibrating ? _calPhase switch
    {
        CalPhase.Baseline => $"第 1 段 · 轻载基线 {_calibrationProgress:F0}%（请保持空闲，勿开其他程序）",
        CalPhase.Ramp => $"第 2 段 · 满载升温 {_calibrationProgress:F0}%（等待温度平台，通常 60–90 秒）",
        CalPhase.Plateau => $"第 3 段 · 稳态采样 {_calibrationProgress:F0}%",
        _ => $"标定中 {_calibrationProgress:F0}%"
    } : "两段式：12 秒轻载基线 + 满载至温度平台，实测稳态热阻（约 1–3 分钟）";

    private bool _stressActive;
    public bool StressActive
    {
        get => _stressActive;
        private set { _stressActive = value; N(nameof(StressActive)); }
    }

    private string _profile = "balanced";
    public string Profile
    {
        get => _profile;
        private set
        {
            _profile = value;
            N(nameof(Profile));
            N(nameof(ProfileLabel));
            N(nameof(IsSilent));
            N(nameof(IsBalanced));
            N(nameof(IsPerformance));
        }
    }
    public string ProfileLabel => _profile switch
    {
        "silent" => "静音优先",
        "performance" => "性能优先",
        _ => "均衡"
    };
    public bool IsSilent => _profile == "silent";
    public bool IsBalanced => _profile == "balanced";
    public bool IsPerformance => _profile == "performance";

    private double _ceilingC = 88;
    public double CeilingC
    {
        get => _ceilingC;
        private set
        {
            _ceilingC = value;
            N(nameof(CeilingC)); N(nameof(CeilingText)); N(nameof(ThresholdLabelText));
        }
    }
    public string CeilingText => $"{_ceilingC:F0}°C";
    public string ThresholdLabelText => $"天花板 {CeilingText}";

    // ---------------- 环境状态条 ----------------

    public StatusLevel StatusLevel
    {
        get
        {
            if (!SensorsReady) return StatusLevel.Info;
            if (!DriverAvailable) return StatusLevel.Warn;
            if (!TempAvailable) return StatusLevel.Warn;
            if (Environment.Generation == 0) return StatusLevel.Info;
            return StatusLevel.Ok;
        }
    }

    public string StatusText
    {
        get
        {
            if (!SensorsReady) return "正在读取传感器…（首次约 2 秒）";
            if (!DriverAvailable) return "温度不可用 · 未检测到硬件访问驱动";
            if (!TempAvailable) return IsElevated ? "温度不可用 · 传感器无读数" : "受限权限 · 读取温度需要管理员";
            if (Environment.Generation == 0) return "保守基线 · 未识别平台，已按 7 代区间运行";
            return "运行正常";
        }
    }

    public string StatusAction => DriverAvailable ? "查看诊断" : "以管理员重启";

    /// <summary>
    /// 产品版本：唯一来源是程序集版本（csproj 的 Version）。界面、自检报告、关于页都读这里，
    /// 避免再出现「侧栏写一个版本、关于页写另一个版本」的漂移。
    /// </summary>
    public string VersionText { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var v = typeof(SessionState).Assembly.GetName().Version;
        return v is null ? "v0.0.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---------------- 集合 ----------------

    public ObservableCollection<double> TempHistory { get; } = new();
    public ObservableCollection<double> Pl1History { get; } = new();
    public ObservableCollection<double> PowerHistory { get; } = new();
    public ObservableCollection<DecisionRow> Decisions { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();
    public ObservableCollection<SampleRow> Samples { get; } = new();

    /// <summary>统一时序图的时间窗说明；采样间隔可调，不写死 5 分钟。</summary>
    public string HistoryCaptionText =>
        $"最近 {TempHistory.Count * Settings.IntervalMs / 1000.0:F0} 秒 · " +
        $"采样 {1000.0 / Math.Max(1, Settings.IntervalMs):0.#} Hz · {TempHistory.Count} 点";
    public string HistoryStartText => $"-{TempHistory.Count * Settings.IntervalMs / 1000.0:F0} s";

    /// <summary>图表口径说明：一条固定短句，条件描述与 MetricChart 实际行为一致。</summary>
    public string HistoryHintText =>
        "各序列按自身量程独立归一化、共用时间轴，交叉点无物理含义。天花板参考线只在落入温度量程时绘制，右上角标注始终可见。";

    /// <summary>
    /// 注册统一图表序列。风扇只登记「只读实测」口径：
    /// 决策树的演算建议值 TargetFanPercent 从不进入图表，也不冒充实测转速。
    /// 本机 EC 寄存器只是档位刻度，不是 RPM；真实 RPM 可用时才懒加载第二条序列。
    /// </summary>
    private void InitMetrics()
    {
        Metrics.Add(_sTemp);
        Metrics.Add(_sPl1);
        Metrics.Add(_sPower);
        Metrics.Add(_sCpu);
        Metrics.Add(_sGpu);
        Metrics.Add(_sGpuMem);
        Metrics.Add(_sFan);
    }

    private static Brush C(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void PushMetric(MetricSeries series, double value) => series.Push(value, HistoryCap);

    private void EnsureFanRpmSeries()
    {
        if (_sFanRpm is not null) return;
        _sFanRpm = new MetricSeries("fanrpm", "风扇转速", "RPM", C("#D4537E"), 1.6,
            format: "F0", tooltip: "LHM 风扇转速传感器实测（本机无此传感器）");
        Metrics.Add(_sFanRpm);
    }

    /// <summary>UI 截图演示数据入口：只填历史点，不写寄存器、不触发决策。</summary>
    public void AppendDemoHistory(double temp, double pl1, double power)
    {
        Push(TempHistory, temp, HistoryCap);
        Push(Pl1History, pl1, HistoryCap);
        Push(PowerHistory, power, HistoryCap);
        PushMetric(_sTemp, temp);
        PushMetric(_sPl1, pl1);
        PushMetric(_sPower, power);
        PushMetric(_sCpu, Math.Clamp(20 + power, 0, 100));
        PushMetric(_sGpu, double.NaN);
        PushMetric(_sGpuMem, double.NaN);
        PushMetric(_sFan, Math.Clamp(50 + power * 0.4, 0, 100));
        N(nameof(HistoryCaptionText));
        N(nameof(HistoryStartText));
    }

    private int _alertCount;
    public int AlertCount
    {
        get => _alertCount;
        private set { _alertCount = value; N(nameof(AlertCount)); }
    }

    // ---------------- MSR 通道 ----------------

    public MsrStatus MsrStatus { get; private set; } = MsrStatus.NotInstalled;
    public string MsrDetail { get; private set; } = "未探测";
    public string MsrStatusText => MsrChannel.StatusText(MsrStatus);
    public bool MsrWritable => MsrStatus == MsrStatus.Writable;
    public bool RealWriteEnabled => Settings.AllowRealWrite && MsrWritable;
    public bool HasMsrBackup => _guard?.StartupBackup is not null;

    /// <summary>写入闸门：管理员 + PawnIO / IntelMSR 通道可写 + 设置页显式授权。</summary>
    public bool WriteGatesOpen => MsrWritable && Settings.AllowRealWrite && IsElevated;

    /// <summary>动态自动通道（决策树每拍）是否允许写；选中档位 / 手工保存不受它约束。</summary>
    public bool AutoApplyAllowed => !IsManualMode && WriteGatesOpen && Settings.AutoTakeover;

    /// <summary>
    /// 「之后持续动态微调」开关的唯一数据源：开关就放在智能微调页的档位卡里，
    /// 与档位同屏，用户不用跨页推理「点了档位之后还会不会自己动」。
    /// </summary>
    public bool AutoTakeoverOn => Settings.AutoTakeover;

    /// <summary>手工模式下档位只演算，这个开关没有作用对象，置灰并给常驻原因。</summary>
    public bool AutoTakeoverToggleEnabled => IsIntelligentMode;

    /// <summary>
    /// 档位卡下方「之后会怎样」的常驻说明（四态）：
    /// 手工模式 / 当前不可写 / 已开启 / 已关闭。置灰与关闭都必须有文字，不能只靠 ToolTip。
    /// </summary>
    public string AutoTakeoverHintText => IsManualMode
        ? "手工微调模式下档位只演算，自动修正不参与；实际写入在「手工微调」页保存并下发。"
        : !WriteGatesOpen
            ? $"当前不可写（{GateReason}）：开关状态会记住，但要有写入授权后才会自动修正。"
            : Settings.AutoTakeover
                ? "开启：每秒按 1W 小步修正，连续 3 拍同向才动，升档前有冷却；高温只降不升。"
                : "关闭：只保留选中档位那一次下发，之后不再自动改动功耗上限。";

    public bool IsIntelligentMode => !IsManualMode;
    public bool IsManualModePublic => IsManualMode;
    public string TuningModeText => IsManualMode ? "手工微调" : "智能微调";
    public string CooldownText => $"升档冷却 {Settings.RampUpCooldownSeconds:F0}s" +
        (IsIntelligentMode ? $" · 剩余 {_scheduler.CooldownRemaining(DateTime.UtcNow):F0}s" : string.Empty);

    /// <summary>最近一次真实下发结果（成功或跳过原因），不把演算值说成已下发。</summary>
    public string LastWriteText => _lastWriteText;

    /// <summary>概览页全局状态条：模式 + 是否真会写 + 具体原因，避免「点一下以为下发了」。</summary>
    public string ModeStatusText => IsManualMode
        ? $"手工微调模式 · 智能档位只演算，寄存器由「手工微调」页保存并下发 · {_lastWriteText}"
        : !WriteGatesOpen
            ? $"智能微调 · {ProfileLabel} · 只读演算（{GateReason}）· 选中档位会二次确认"
            : $"智能微调 · {ProfileLabel} · 选中即下发 · " +
              (Settings.AutoTakeover ? "决策树持续阶梯微调" : "决策树持续微调已关闭") +
              $" · {CooldownText} · {_lastWriteText}";

    /// <summary>底栏一行状态：明确模式与写入能力。</summary>
    public string FooterMode => IsManualMode
        ? "手工微调（智能档位只演算）"
        : WriteGatesOpen ? (Settings.AutoTakeover ? "智能微调 · 已授权" : "智能微调 · 仅选中下发")
                         : "智能微调 · 只读演算";

    public string GateReason => IsElevated
        ? (MsrWritable ? (Settings.AllowRealWrite ? "闸门已开" : "设置页未勾选「允许写入硬件参数」")
                       : $"调优通道当前只读：{MsrStatusText}")
        : "需要管理员权限";

    /// <summary>智能微调页档位卡标题：按真实写入能力切换，只读态不承诺下发。</summary>
    public string ProfileCardTitle => IsManualMode
        ? "使用档位 · 仅演算"
        : WriteGatesOpen ? "使用档位 · 选中即生效" : "使用档位 · 选中仅演算";

    /// <summary>档位卡说明：与标题同源双态，避免「说明说会写、状态框说不能写」。</summary>
    public string ProfileCardHint => IsManualMode
        ? "手工模式下手动选档只更新意图与演算结果，不写寄存器；要写入请到「手工微调」页。"
        : WriteGatesOpen
            ? "点选档位立即重算决策并下发一次，风险场景先二次确认；之后按 1W 台阶与升档冷却低频微调。PL1 由温度与热阻决定，不是写死的瓦数。"
            : $"当前不可写（{GateReason}）：点选档位只重算决策并给出下发计划，硬件保持不变。";

    /// <summary>标定按钮的常驻原因：禁用控件弹不出 ToolTip，所以原因必须写在页面上。</summary>
    public string CalibrationGateHint => CanCalibrate ? string.Empty : CalibrationBlockedReason;

    public System.Windows.Visibility CalibrationGateVisibility =>
        CanCalibrate ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>概览页写入链路说明：随真实闸门状态变化，不把「可写」说成「已在写」。</summary>
    public string MonitorHintText => IsManualMode
        ? "手工微调模式：智能档位与决策树只演算，不写寄存器。寄存器由「手工微调」页的「保存并下发」控制；切回智能档位会先二次确认。"
        : !WriteGatesOpen
            ? $"只读演算：选中档位会弹二次确认，但当前无法写硬件（{GateReason}）；链路含启动备份 / 回读校验 / 退出恢复 / 开机重放。"
            : !Settings.AutoTakeover
                ? "选中档位立即下发一次；「自动接管」关闭后，决策树不再每拍动态微调。升档冷却与 1W 阶梯量化始终生效。"
                : "选中档位立即下发一次；之后由阶梯调度器按 1W 台阶、连续 3 拍迟滞与升档冷却低频动态微调；高温只降不升，EPP 与风扇不写入。";

    public string MsrSummary => MsrWritable
        ? (Settings.AllowRealWrite ? "可写 · 已授权" : "可写 · 未授权")
        : MsrStatusText;

    /// <summary>
    /// 开机加载已保存的实测标定：标定值是软件侧热模型，不是固件值，
    /// 掉电不会丢（存 settings.json），不必每次开机重新满载标定。
    /// </summary>
    private void LoadPersistedCalibration()
    {
        var c = Settings.Calibration;
        if (c is null || c.Rth <= 0 || double.IsNaN(c.Rth)) return;

        _controller.ApplyCalibratedRth(c.Rth);
        Rth = c.Rth;                       // 首屏即可显示，不必等第一拍
        _basePowerW = c.IdleW;             // 稳态预测基线也用标定时的轻载点
        _baseTempC = c.IdleC;
        _controller.SetThermalBaseline(c.IdleC, c.IdleW);   // 让标定值开机即参与决策前馈
    }

    public string CalibrationSummaryText => Settings.Calibration is { } c
        ? $"已保存标定：Rth {c.Rth:F2} K/W · {c.SavedAt:yyyy-MM-dd HH:mm}（开机自动加载，无需重测）"
        : "尚无已保存的标定结果；完成一次两段式标定后会自动保存到本机。";

    /// <summary>标定成功后持久化热模型（含轻载基线，供预测与开机重放）。</summary>
    private void SaveCalibrationSnapshot(CalibrationOutcome o)
    {
        // L2 推荐只包含通用安全参数：稳态热模型反解的 PL1 + 档位 EPP。
        // 电压偏移 / 睿频倍率 / 温度墙属于个性化或危险项，标定与应用标定一律不碰。
        double recommendPl1 = Math.Clamp(
            ThermalCalibration.MaxPowerForCeiling(o.Rth, o.IdleC, o.IdleW, CeilingC),
            Limits.MinPL1, Limits.MaxPL1);
        Settings.Calibration = new CalibrationSnapshot
        {
            Rth = o.Rth, IdleC = o.IdleC, IdleW = o.IdleW,
            HotC = o.HotC, HotW = o.HotW, BasisW = o.BasisW,
            PredictedC = o.PredictedAtMaxC, Recommended = o.Recommended,
            RecommendedPl1W = double.IsNaN(recommendPl1) ? 0 : recommendPl1,
            RecommendedEpp = AutoCpuController.EppForProfile(o.Recommended),
            VoltageContextMv = _appliedVoltageMv,
            SavedAt = DateTime.Now
        };
        SettingsStore.Save(Settings);
        N(nameof(CalibrationSummaryText)); N(nameof(CalibrationLayerText));
        AddAlert("info", "标定推荐已更新（L2）",
            "热阻与推荐参数写入 settings.json，下次开机自动加载；固件默认值不受影响。" +
            "标定只产出通用安全参数（PL1 / PL2 / Tau / EPP），不碰电压偏移、睿频倍率与温度墙。" +
            "注意：能效偏好与性能档可能被系统电源策略覆盖，稳定生效的是功耗三项。" +
            (Settings.Manual is not null
                ? "检测到已有用户编辑值（L3）：本次标定不会覆盖它，是否应用新推荐由你决定。"
                : "尚无用户编辑值，可在高级参数页应用推荐。") +
            (_appliedVoltageMv < 0
                ? $" 注意：当前机器有 {_appliedVoltageMv}mV 电压偏移，热模型已随之改变，推荐值可能不同。"
                : string.Empty));
    }

    private void InitMsr()
    {
        try
        {
            var channel = MsrChannel.Open();
            MsrStatus = channel.Status;
            MsrDetail = channel.Detail;

            if (channel.Status is MsrStatus.Writable or MsrStatus.ReadOnly)
            {
                _guard = new MsrGuard(channel, SettingsStore.DataDirectory);

                // 固件实际 PL1：预测稳态时用它做基准，比平台表里的 MaxPL1 更贴机器。
                // 同时注入决策树首拍基准，避免用硬编码 35W 冒充本机寄存器实值。
                if (channel.TryReadLimits(out RaplLimits lim) && lim.Pl1W > 0)
                {
                    _firmwarePl1W = lim.Pl1W;
                    _fwLimits = lim;
                    _controller.SetCurrentPl1W(lim.Pl1W);
                    Pl1W = lim.Pl1W; Pl2W = lim.Pl2W; TauS = lim.TauS;
                    N(nameof(PowerBasisText)); N(nameof(FwPl1Text)); N(nameof(FwPl2Text)); N(nameof(FwTauText));
                }

                // 睿频倍率表实读（0x1AD），供高级参数页做「当前生效」与出厂上限基线
                _fwTurbo = _guard.ReadTurbo();
                N(nameof(FwTurboText)); N(nameof(TurboEditable)); N(nameof(TurboHintText));
                N(nameof(TurboRangeText));

                // RAPL 只读计量：复用同一个 Ring0 驱动句柄，避免重复加载内核驱动
                _rapl = new RaplPowerMeter(channel, Environment.Vendor);
                _pendingRapl = _rapl;
                // 遥测可能是后台线程后建的，先存起来，接上时自动挂载
                _telemetry?.AttachRapl(_rapl);
                if (!_rapl.Available)
                    AddAlert("warn", "功耗计量不可用", _rapl.Detail + "；功耗将退回估算值，标定会被禁用。");
                if (_guard.HasPendingRestore)
                {
                    var r = _guard.RestorePendingIfAny();
                    AddAlert(r.Ok ? "warn" : "danger", "上次未正常退出", r.Message);
                }
                if (_guard.HasTurboPendingRestore)
                {
                    var r = _guard.RestorePendingTurboIfAny();
                    AddAlert(r.Ok ? "warn" : "danger", "上次未正常退出（睿频）", r.Message);
                }

                // 先取 L1 固件实读基线（含 0x774 全字段），再谈重放
                RefreshFirmwareValues();

                // 开机重放：把软件侧保存的微调参数写回寄存器
                // 软件不运行 / 未授权时固件保持出厂默认，按电脑默认散热逻辑运转
                ReplayPersistedTuning();

                // 重放可能已改写 0x610：再读一次「当前生效」值作为决策起点与首帧显示值。
                // 这样界面不会拿固件默认 45W 或硬编码 35W 冒充已重放的软件微调值。
                if (channel.TryReadLimits(out RaplLimits effective) && effective.Pl1W > 0)
                {
                    _fwLimits = effective;
                    _controller.SetCurrentPl1W(effective.Pl1W);
                    // v0.5.0：让阶梯调度器以「寄存器当前实读值」为台阶基准。
                    // 不这么做的话，智能模式（开机不重放 L3）下 LastAppliedStep 一直为 null，
                    // 首拍会被误判成「台阶未变」而永远跳过自动下发。
                    _scheduler.SeedAppliedStep(effective.Pl1W, DateTime.UtcNow);
                    Pl1W = effective.Pl1W; Pl2W = effective.Pl2W; TauS = effective.TauS;
                    N(nameof(FwPl1Text)); N(nameof(FwPl2Text)); N(nameof(FwTauText));
                    N(nameof(FwDivergeText)); N(nameof(FwDefaultText));
                }
            }
            else
            {
                channel.Dispose();
            }
        }
        catch (Exception ex)
        {
            MsrStatus = MsrStatus.ModuleUnavailable;
            MsrDetail = "MSR 初始化异常：" + ex.Message;
        }
        N(nameof(MsrStatus));
        N(nameof(MsrStatusText));
        N(nameof(MsrDetail));
        N(nameof(MsrSummary));
        // 重放可能改写了 0x610 / 0x774，最后再刷一遍「当前生效」列
        RefreshFirmwareValues();
        N(nameof(CalibrationLayerText)); N(nameof(ManualLayerText));
    }

    /// <summary>
    /// 开机重放已持久化的手动微调值。固件默认值不可持久化，软件存在的意义就是
    /// 每次开机把它写进寄存器；软件不运行/未授权时 CPU 自然按固件默认运转。
    /// </summary>
    private void ReplayPersistedTuning()
    {
        if (_guard is null) return;
        var m = Settings.Manual;

        // v0.5.0：模式是真值源。智能模式下即使残留 L3 值也不重放，
        // 首拍决策由阶梯调度器按当前档位下发（选中档位时走事件通道）。
        if (IsIntelligentMode)
        {
            if (m is not null)
                AddAlert("info", "智能微调模式", "已保存的手工微调值未重放；切到「手工微调」页保存并下发才会写寄存器。");
            return;
        }
        if (m is null)
        {
            AddAlert("info", "手工微调模式", "尚无保存的手工参数；到「手工微调」页编辑并保存后才会写入寄存器。");
            return;
        }

        if (!Settings.ReplayOnStartup)
        {
            AddAlert("info", "已保存微调参数未重放", "开机重放已关闭，CPU 当前按固件默认值运转。");
            return;
        }
        if (!RealWriteEnabled)
        {
            AddAlert("warn", "已保存微调参数未生效",
                "开机重放需要管理员权限与设置页的写入授权；CPU 当前按出厂默认值运转。");
            return;
        }

        var r = _guard.Apply(m.Pl1W, m.Pl2W, m.TauS, m.WriteTimeWindow);
        if (r.Ok)
        {
            // 开机重放是「恢复用户保存的状态」，原样写回，不经过 1W 阶梯量化节流；
            // 但必须同步调度器的台阶基准，否则首拍会把用户保存值误判成一次升档。
            _scheduler.SeedAppliedStep(m.Pl1W, DateTime.UtcNow);
            _lastWriteText = $"开机重放 {m.Pl1W:F1}W（手工微调）";
            AddAlert("info", "开机重放微调参数",
                $"已写入 PL1 {m.Pl1W:F1}W / PL2 {m.Pl2W:F1}W / Tau {m.TauS:F0}s" +
                $"（{m.SavedAt:yyyy-MM-dd HH:mm} 保存）。{r.Message}");
        }
        else
        {
            AddAlert("danger", "开机重放失败", r.Message + "；CPU 保持固件默认值。");
        }

        if (r.Ok && _guard.EppSupported && m.Epp >= 0)
        {
            var er = _guard.ApplyEpp(m.Epp);
            AddAlert(er.Ok ? "info" : "warn", er.Ok ? "EPP 已重放" : "EPP 重放未生效", er.Message);
        }

        // L3 通用 HWP 扩展字段：-1 表示用户未编辑该项，保持原值、不重放。
        // 智能标定（L2）永远不写这些字段，只有用户编辑过的 L3 值才会开机重放。
        if (r.Ok && (m.MinPerf >= 0 || m.MaxPerf >= 0 || m.ActivityWindow >= 0))
        {
            var hr = _guard.ApplyHwp(m.MinPerf, m.MaxPerf, -1, m.ActivityWindow, FwHwpMaxPerf);
            AddAlert(hr.Ok ? "info" : "warn", hr.Ok ? "HWP 通用参数已重放" : "HWP 重放未生效", hr.Message);
        }

        // L3 个性化电压偏移：只在用户显式保存过降压值时重放；失败不影响其它参数。
        // 该寄存器易失，重启/退出即回零，属于「相对安全」但需要型号预筛与二次确认的可选微调。
        if (m.VoltageOffsetMv < 0 && _guard.VoltageReadable && CpuCaps.VoltageOffsetPlausible)
        {
            var vr = _guard.ApplyVoltage(m.VoltageOffsetMv);
            if (vr.Ok) _appliedVoltageMv = m.VoltageOffsetMv;
            AddAlert(vr.Ok ? "warn" : "info", vr.Ok ? "电压偏移已重放" : "电压偏移未生效", vr.Message);
        }

        // 睿频倍率表独立于 0x610：即使功耗参数重放失败，也尝试恢复用户保存的倍率
        if (m.TurboRatio > 0)
        {
            var tr = _guard.ApplyTurbo(m.TurboRatio);
            AddAlert(tr.Ok ? "info" : "warn",
                tr.Ok ? "睿频倍率已重放" : "睿频倍率重放未生效", tr.Message);
        }
    }

    /// <summary>
    /// L3：应用成功后才保存用户编辑值（只有真实写入成功才落盘）。
    /// 新增的扩展项都带默认值，旧调用点无需改动；-1 = 该项未编辑、不重放。
    /// </summary>
    public void SaveManualTuning(double pl1, double pl2, double tau, int epp,
        bool writeTimeWindow, int turboRatio = 0,
        int minPerf = -1, int maxPerf = -1, int activityWindow = -1, int voltageOffsetMv = 0)
    {
        Settings.Manual = new ManualTuning
        {
            Pl1W = pl1, Pl2W = pl2, TauS = tau, Epp = epp,
            WriteTimeWindow = writeTimeWindow, TurboRatio = turboRatio,
            MinPerf = minPerf, MaxPerf = maxPerf,
            ActivityWindow = activityWindow, VoltageOffsetMv = voltageOffsetMv,
            SavedAt = DateTime.Now
        };
        // v0.5.0：进入手工模式（单一真值源）。手工模式下决策树只演算、不写寄存器；
        // 不再静默关掉「自动接管」——那个开关现在只管智能模式的持续动态微调。
        Settings.TuningMode = TuningModes.Manual;
        _scheduler.Reset();
        SettingsStore.Save(Settings);
        NotifyModeChanged();
        N(nameof(SavedTuningText)); N(nameof(ManualLayerText));
        AddAlert("info", "微调参数已保存（L3）",
            $"PL1 {pl1:F1}W / PL2 {pl2:F1}W / Tau {tau:F0}s" +
            (epp >= 0 ? $" / EPP {epp}" : "（EPP 本机不可读，未保存）") +
            (maxPerf >= 0 ? $" / MaxPerf {maxPerf}" : string.Empty) +
            (voltageOffsetMv < 0 ? $" / 电压 {voltageOffsetMv}mV（个性化项，需二次确认）" : string.Empty) +
            " 已写入 settings.json；下次开机自动重放到寄存器。已切换到「手工微调」模式，智能档位只演算。" +
            "重新标定只更新 L2 推荐值，不会覆盖本次编辑。");
    }

    public string SavedTuningText => Settings.Manual is { } m
        ? $"已保存的用户编辑值（L3）：PL1 {m.Pl1W:F1}W · PL2 {m.Pl2W:F1}W · Tau {m.TauS:F0}s · " +
          (m.Epp >= 0 ? $"EPP {m.Epp}" : "EPP 未保存") +
          (m.MaxPerf >= 0 ? $" · MaxPerf {m.MaxPerf}" : string.Empty) +
          (m.VoltageOffsetMv < 0 ? $" · 电压 {m.VoltageOffsetMv}mV" : string.Empty) + " · " +
          (m.TurboRatio > 0 ? $"睿频 {m.TurboRatio}x" : "睿频未调节") +
          $"（{m.SavedAt:yyyy-MM-dd HH:mm}）"
        : "无已保存的用户编辑值（CPU 按固件默认值运转）";

    public void ClearManualTuning()
    {
        bool wasManual = IsManualMode;
        Settings.Manual = null;
        // 清掉 L3 后不留一个「没有值的手工模式」把用户卡住：自动回到智能模式。
        if (wasManual) Settings.TuningMode = TuningModes.Intelligent;
        _scheduler.Reset();
        SettingsStore.Save(Settings);
        N(nameof(SavedTuningText));
        NotifyModeChanged();
        AddAlert("info", "已清除保存的微调参数",
            wasManual ? "已切回「智能微调」模式；下次开机不再重放手工值，CPU 保持固件默认值。"
                      : "下次开机不再重放，CPU 保持固件默认值。");
    }

    /// <summary>从手工模式切回智能模式（档位选中或设置页触发，UI 先做二次确认）。</summary>
    public void EnterIntelligentMode(bool clearManualValues)
    {
        if (!IsManualMode) return;
        Settings.TuningMode = TuningModes.Intelligent;
        if (clearManualValues) Settings.Manual = null;
        _scheduler.Reset();
        SettingsStore.Save(Settings);
        NotifyModeChanged();
        N(nameof(SavedTuningText));
        AddAlert("info", "已切换到智能微调模式",
            clearManualValues
                ? "已停止重放并清除手工微调值；下次开机按智能档位决策下发。"
                : "手工微调值保留在 settings.json，但不再重放；可在设置页清除。");
    }

    /// <summary>
    /// v0.5.0 语义：自动接管只控制「智能模式下决策树是否每拍持续动态微调」，
    /// 不影响选中档位的事件下发，也不影响手工模式。
    /// </summary>
    public void SetAutoTakeover(bool on)
    {
        Settings.AutoTakeover = on;
        SettingsStore.Save(Settings);
        N(nameof(AutoApplyAllowed)); N(nameof(AutoApplyGateText));
        N(nameof(AutoTakeoverOn)); N(nameof(AutoTakeoverHintText)); N(nameof(AutoTakeoverToggleEnabled));
        N(nameof(MonitorHintText)); N(nameof(ModeStatusText)); N(nameof(FooterMode));
        AddAlert("info", on ? "持续动态微调已开启" : "持续动态微调已关闭",
            on ? "决策树按 1W 台阶 + 3 拍迟滞 + 升档冷却低频下发；选中档位仍立即下发。"
               : "选中档位仍立即下发一次，决策树每拍只演算、不写寄存器。");
    }

    /// <summary>设置页可调升档冷却（5~120s）。只影响升档写入，降档与高温降档不受限。</summary>
    public void SetRampUpCooldown(double seconds)
    {
        Settings.RampUpCooldownSeconds = Math.Clamp(seconds,
            WriteScheduler.MinCooldownSeconds, WriteScheduler.MaxCooldownSeconds);
        _scheduler.CooldownSeconds = Settings.RampUpCooldownSeconds;
        SettingsStore.Save(Settings);
        N(nameof(CooldownText)); N(nameof(ModeStatusText));
        AddAlert("info", "升档冷却已调整",
            $"设为 {Settings.RampUpCooldownSeconds:F0}s；只约束升档下发，高温降档立即放行。");
    }

    /// <summary>模式相关绑定统一通知，避免漏掉某一处导致界面显示旧模式。</summary>
    private void NotifyModeChanged()
    {
        N(nameof(IsManualModePublic)); N(nameof(IsIntelligentMode));
        N(nameof(TuningModeText)); N(nameof(ModeStatusText)); N(nameof(FooterMode));
        N(nameof(MonitorHintText)); N(nameof(AutoApplyAllowed)); N(nameof(AutoApplyGateText));
        N(nameof(AutoTakeoverOn)); N(nameof(AutoTakeoverHintText)); N(nameof(AutoTakeoverToggleEnabled));
        N(nameof(CooldownText));
        N(nameof(ProfileCardTitle)); N(nameof(ProfileCardHint));
        N(nameof(CalibrationGateHint)); N(nameof(CalibrationGateVisibility));
    }

    public void SetReplayOnStartup(bool on)
    {
        Settings.ReplayOnStartup = on;
        SettingsStore.Save(Settings);
    }

    public void SetAllowRealWrite(bool on)
    {
        // 关闭写入授权时，持续动态微调失去作用对象，必须同时关掉；
        // 但这是一次隐式联动，必须在事件中心说明，否则用户看到开关自己变了会以为是 bug。
        bool droppedTakeover = !on && Settings.AutoTakeover;
        Settings.AllowRealWrite = on;
        if (!on) Settings.AutoTakeover = false;
        SettingsStore.Save(Settings);
        N(nameof(RealWriteEnabled));
        N(nameof(MsrSummary));
        N(nameof(MonitorHintText)); N(nameof(ModeStatusText)); N(nameof(GateReason));
        N(nameof(FooterMode)); N(nameof(AutoApplyAllowed)); N(nameof(AutoApplyGateText));
        N(nameof(AutoTakeoverOn)); N(nameof(AutoTakeoverHintText)); N(nameof(AutoTakeoverToggleEnabled));
        N(nameof(ProfileCardTitle)); N(nameof(ProfileCardHint));
        N(nameof(CalibrationGateHint)); N(nameof(CalibrationGateVisibility));
        if (droppedTakeover)
            AddAlert("info", "持续动态微调已同时关闭",
                "写入授权关闭后不会再改硬件，持续修正随之停止；重新授权后可在「智能微调」页档位卡里再次开启。");
    }

    /// <summary>真实写入；未授权或通道不可写时直接拒绝（不静默降级成演练）。</summary>
    public MsrWriteResult ApplyLimits(double pl1, double pl2, double tau, bool writeTimeWindow = false)
    {
        if (_guard is null)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"调优通道不可用：{MsrDetail}");
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入硬件参数」（设置页）");

        var result = _guard.Apply(pl1, pl2, tau, writeTimeWindow);

        // 写入失败可能让通道自我降级，刷新状态供 UI 立即反映
        MsrStatus = _guard.Channel.Status;
        MsrDetail = _guard.Channel.Detail;
        N(nameof(MsrStatus));
        N(nameof(MsrStatusText));
        N(nameof(MsrDetail));
        N(nameof(MsrSummary));
        N(nameof(MsrWritable));

        AddAlert(result.Ok ? "info" : "danger",
            result.Ok ? "参数已写入硬件" : "参数写入失败", result.Message);
        return result;
    }

    /// <summary>写入 EPP（0x774）；机型不支持时返回说明，不影响 PL1/PL2 的写入结果。</summary>
    public MsrWriteResult ApplyEppIfSupported(int epp)
    {
        if (_guard is null || !_guard.EppSupported)
            return new MsrWriteResult(MsrWriteOutcome.Rejected,
                "本机读不到能效偏好，该项不可用（功耗三项不受影响）");
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入 MSR」");

        var r = _guard.ApplyEpp(epp);
        AddAlert(r.Ok ? "info" : "warn", r.Ok ? "EPP 已写入" : "EPP 写入未生效", r.Message);
        return r;
    }

    /// <summary>
    /// 通用 HWP 参数（L2 标定可推荐 / L3 用户可编辑）：Min/Max Perf、EPP、Activity Window。
    /// -1 = 保持原值。绝不写 Desired Perf / Package Control。
    /// </summary>
    public MsrWriteResult ApplyHwpIfSupported(int minPerf = -1, int maxPerf = -1,
        int epp = -1, int activityWindow = -1)
    {
        if (_guard is null || !_guard.EppSupported)
            return new MsrWriteResult(MsrWriteOutcome.Rejected, "本机读不到能效偏好，该项不可用");
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入 MSR」");

        var r = _guard.ApplyHwp(minPerf, maxPerf, epp, activityWindow, FwHwpMaxPerf);
        RefreshFirmwareValues();
        AddAlert(r.Ok ? "info" : "warn", r.Ok ? "HWP 参数已写入" : "HWP 写入未生效", r.Message);
        return r;
    }

    /// <summary>
    /// 个性化可选微调（仅 L3 用户手动）：Core/Cache 电压偏移。
    /// 需型号预筛通过 + 0x150 可读；真正可写性由写入自检实测，失败强制回零。
    /// </summary>
    public MsrWriteResult ApplyVoltageIfSupported(int offsetMv)
    {
        if (_guard is null || !_guard.VoltageReadable)
            return new MsrWriteResult(MsrWriteOutcome.Rejected, "本机读不到电压偏移，该项不可用");
        if (!CpuCaps.VoltageOffsetPlausible)
            return new MsrWriteResult(MsrWriteOutcome.Rejected, CpuCaps.VoltageHint);
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入 MSR」");

        var r = _guard.ApplyVoltage(offsetMv);
        if (r.Ok) _appliedVoltageMv = offsetMv;
        else { _appliedVoltageMv = 0; _guard.RestoreVoltage(); }
        N(nameof(AppliedVoltageMv));
        AddAlert(r.Ok ? "warn" : "info", r.Ok ? "电压偏移已下发" : "电压偏移未生效", r.Message);
        return r;
    }

    /// <summary>
    /// 写入睿频倍率上限（0x1AD）。ratio 为 ×100MHz，必须 ≤ 出厂单核上限；
    /// 机型不支持或超出范围时返回说明，不影响 PL1/PL2/Tau 的写入结果。
    /// </summary>
    public MsrWriteResult ApplyTurboIfSupported(int ratio)
    {
        if (_guard is null || !_guard.TurboSupported)
            return new MsrWriteResult(MsrWriteOutcome.Rejected,
                "本机读不到睿频倍率，该项不可用（功耗三项不受影响）");
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入 MSR」");

        var r = _guard.ApplyTurbo(ratio);
        MsrStatus = _guard.Channel.Status;
        MsrDetail = _guard.Channel.Detail;
        N(nameof(MsrStatus)); N(nameof(MsrStatusText)); N(nameof(MsrDetail));
        N(nameof(MsrSummary)); N(nameof(MsrWritable));
        AddAlert(r.Ok ? "info" : "warn", r.Ok ? "睿频倍率已写入" : "睿频倍率写入未生效", r.Message);
        return r;
    }

    /// <summary>
    /// 当前应持久化的睿频倍率：只有当实读倍率表「各档统一」且「偏离出厂默认」时才返回值，
    /// 否则返回 0（不重放，CPU 本来就是出厂状态）。避免把「本次没动睿频」误清成未保存。
    /// </summary>
    public int PersistedTurboRatio()
    {
        var t = _guard?.ReadTurbo();
        var def = _guard?.StartupTurboBackup;
        if (t is null || def is null || t.Raw == def.Raw) return 0;
        return TurboRatioLimit.AllTiersEqual(t.Raw, t.AllCore) ? t.AllCore : 0;
    }

    /// <summary>出厂睿频倍率表的全核档位（用于判断「是否回到默认」）。</summary>
    public int FirmwareTurboAllCore => _guard?.StartupTurboBackup?.AllCore ?? 0;

    public MsrWriteResult RestoreOriginal()
    {
        if (_guard is null)
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, $"调优通道不可用：{MsrDetail}");

        var result = _guard.Restore();
        var epp = _guard.RestoreEpp();
        var turbo = _guard.RestoreTurbo();
        var voltage = _guard.RestoreVoltage();
        _appliedVoltageMv = 0;
        N(nameof(AppliedVoltageMv));
        if (epp.Ok) result = result with { Message = result.Message + "；EPP 已恢复固件默认值" };
        if (turbo.Ok) result = result with { Message = result.Message + "；睿频倍率表已恢复固件默认" };
        if (voltage.Ok) result = result with { Message = result.Message + "；电压偏移已回零" };
        AddAlert(result.Ok ? "info" : "danger",
            result.Ok ? "已恢复原值" : "恢复失败", result.Message);
        return result;
    }

    /// <summary>
    /// 诊断用：说明当前档位决策到底会不会落到寄存器。
    /// 只供 --smoke / --env-check 报告使用，不绑定 UI，避免主界面堆说明文字。
    /// </summary>
    public string AutoApplyGateText => IsManualMode
        ? "手工微调模式：智能档位/决策树只演算；寄存器由「手工微调」页保存并下发"
        : !WriteGatesOpen
            ? $"写入闸门未全开（{GateReason}）：选中档位会二次确认，但当前只能演算"
            : !Settings.AutoTakeover
                ? "选中档位立即下发一次；决策树持续动态微调已关闭（设置页「自动接管」）"
                : $"选中档位立即下发一次；决策树按 1W 台阶 + 3 拍迟滞 + {Settings.RampUpCooldownSeconds:F0}s 升档冷却低频微调";

    // ---------------- v0.5.0 阶梯下发（单入口） ----------------

    private ProfileApplyPlan? _pendingPlan;
    private bool _pendingProfileWrite;

    private static string NormalizeProfile(string? profile) =>
        (profile ?? "balanced").Trim().ToLowerInvariant() is "silent" or "performance" or "balanced"
            ? (profile ?? "balanced").Trim().ToLowerInvariant() : "balanced";

    /// <summary>
    /// 选中档位前的「下发计划」：只重算一次决策树，给出温度、目标 PL1、Δ、
    /// 闸门与风险原因，供 UI 二次确认展示。不改档位、不写寄存器。
    /// </summary>
    public ProfileApplyPlan PrepareProfileApply(string profile)
    {
        string p = NormalizeProfile(profile);
        double ceiling = Math.Min(AutoCpuController.TargetCForProfile(p), Limits.MaxSafeCeiling);

        // 温度尚未采到（首屏 / 普通权限）时不预估目标瓦数，交由首拍事件通道下发，
        // 避免拿占位温度伪造一个「目标 PL1」写进确认框。
        var dec = double.IsNaN(TempC) ? null : RecomputeDecision(ceiling, p);
        double targetPl1 = dec?.Pl1W ?? double.NaN;
        double targetPl2 = dec?.Pl2W ?? Pl2W;
        double targetTau = dec?.TauS ?? TauS;

        int step = double.IsNaN(targetPl1) ? 0
            : (int)Math.Round(Math.Clamp(targetPl1, Limits.MinPL1, Limits.MaxPL1), MidpointRounding.AwayFromZero);
        int applied = _scheduler.LastAppliedStep ?? step;
        double delta = double.IsNaN(targetPl1) ? 0 : step - applied;
        double remaining = _scheduler.CooldownRemaining(DateTime.UtcNow);
        bool gates = WriteGatesOpen;
        bool leaveManual = IsManualMode;
        bool hasManual = Settings.Manual is not null;
        bool hot = !double.IsNaN(TempC) && TempC >= Limits.MaxSafeCeiling;
        bool hardBlocked = hot && delta > 0;   // 高温只降不升，二次确认不可解除

        var reasons = new List<string>();
        if (!gates) reasons.Add($"写入闸门未全开（{GateReason}）：确认后仍只能演算");
        if (leaveManual) reasons.Add("将停止开机重放并清除已保存的手工微调值（L3）");
        if (delta > 0) reasons.Add($"跨档升功率 Δ +{delta:F0}W");
        if (remaining > 0 && delta > 0) reasons.Add($"升档冷却剩余 {remaining:F0}s，确认后强制下发一次");
        if (hot) reasons.Add($"当前 {TempC:F1}°C 已到安全上限 {Limits.MaxSafeCeiling:F0}°C");
        if (double.IsNaN(targetPl1)) reasons.Add("温度尚未采到：将在首拍采样后自动下发");

        bool needConfirmation = !gates || leaveManual || hardBlocked || delta > 0 ||
                                (remaining > 0 && delta > 0) || double.IsNaN(targetPl1);
        string confirmReason = reasons.Count == 0
            ? "档位切换（同台阶或降档），无需额外风险确认"
            : string.Join("；", reasons);

        _pendingPlan = new ProfileApplyPlan(
            p, Label(p), ceiling, TempC, targetPl1, targetPl2, targetTau, delta,
            gates, GateReason, needConfirmation, confirmReason, hardBlocked,
            remaining, leaveManual, hasManual);
        return _pendingPlan;
    }

    /// <summary>丢弃缓存的计划，避免下一拍复用旧温度/旧目标值。</summary>
    public void ClearPendingPlan() => _pendingPlan = null;

    private static string Label(string profile) => profile switch
    {
        "silent" => "静音优先",
        "performance" => "性能优先",
        _ => "均衡"
    };

    /// <summary>按指定天花板与档位重算一次决策（供选中事件使用；Tick 仍走自己的每拍 Update）。</summary>
    private AutoCpuDecision RecomputeDecision(double ceilingC, string profile) =>
        _controller.Update(
            tempC: TempC,
            powerW: PowerW,
            ceilingC: ceilingC,
            ambientC: AmbientC,
            profile: profile,
            perf: new PerfSample { CoreClocksGhz = ClockGhz, MaxTurboGhz = 0 },
            loadPct: LoadPct,
            dTdt: 0,
            firmwarePl1W: _firmwarePl1W > 0 ? _firmwarePl1W : double.NaN);

    /// <summary>
    /// 执行「选中即生效」：切换模式（如需）→ 更新档位与天花板 → 立即经阶梯调度器下发一次。
    /// force=true 只能来自 UI 二次确认；高温只降不升硬闸与写入熔断仍然不可绕过。
    /// </summary>
    public WriteOutcome ApplyProfile(string profile, bool force)
    {
        string p = NormalizeProfile(profile);
        var plan = _pendingPlan is { } cached && cached.Profile == p
            ? cached : PrepareProfileApply(p);

        if (plan.HardBlocked)
            return new WriteOutcome(false,
                $"已拒绝：{plan.ConfirmReason}。只降不升是硬件安全红线，二次确认不能解除。", 0);

        // 从手工模式切回智能：二次确认已在 UI 完成，这里才真正停止重放并清除 L3。
        if (plan.LeaveManualMode) EnterIntelligentMode(clearManualValues: true);

        // 意图层立即生效并落盘（档位与天花板），与硬件下发解耦。
        SetProfile(p);

        if (double.IsNaN(plan.TargetPl1W))
        {
            _pendingProfileWrite = true;
            _lastWriteText = $"已切到「{plan.ProfileLabel}」，等待首拍采样后下发";
            N(nameof(LastWriteText)); N(nameof(ModeStatusText));
            AddAlert("info", "档位已切换（待首拍下发）",
                "当前还没有有效温度采样，选中意图已生效；首拍采样后会经阶梯调度器立即下发一次。");
            return new WriteOutcome(false, _lastWriteText, 0, force);
        }

        _pendingPlan = null;
        return DispatchWrite(WriteChannel.ProfileEvent,
            plan.TargetPl1W, plan.TargetPl2W, plan.TargetTauS, force);
    }

    /// <summary>
    /// 手工微调页「保存并下发」走同一入口，保证审计与节流口径一致。
    /// 手工页允许显式改写 Tau（writeTimeWindow），其它通道一律保持寄存器原值。
    /// </summary>
    public WriteOutcome ApplyManualWrite(double pl1, double pl2, double tau,
        bool writeTimeWindow = false, bool force = true)
        => DispatchWrite(WriteChannel.ManualSave, pl1, pl2, tau, force, writeTimeWindow);

    /// <summary>
    /// 所有 0x610 下发的唯一入口：选中事件、决策树每拍、手工保存、开机重放。
    /// 阶梯量化 / 迟滞 / 升档冷却 / 高温降档 / 熔断都在 WriteScheduler 里，
    /// 这里只负责闸门、真实写入、回读校验结果与审计告警。
    /// </summary>
    private WriteOutcome DispatchWrite(WriteChannel channel, double pl1, double pl2, double tau,
        bool force = false, bool writeTimeWindow = false)
    {
        string ChannelText() => channel switch
        {
            WriteChannel.ProfileEvent => "选中档位",
            WriteChannel.ManualSave => "手工微调",
            WriteChannel.StartupReplay => "开机重放",
            _ => "决策树"
        };

        WriteOutcome Refuse(string reason, WriteVerdict verdict)
        {
            if (channel != WriteChannel.AutoTick)
            {
                _lastWriteText = $"未下发（{ChannelText()}）：{reason}";
                N(nameof(LastWriteText)); N(nameof(ModeStatusText));
                AddAlert(verdict == WriteVerdict.HardBlocked ? "danger" : "info",
                    "下发被拦下", _lastWriteText);
            }
            return new WriteOutcome(false, reason, _scheduler.LastAppliedStep ?? 0);
        }

        if (_guard is null) return Refuse("MSR 通道不可用", WriteVerdict.HardBlocked);
        if (IsCalibrating) return Refuse("标定进行中，暂停下发", WriteVerdict.Skip);
        if (channel is not (WriteChannel.ManualSave or WriteChannel.StartupReplay) && IsManualMode)
            return Refuse("手工微调模式下智能档位/决策只演算", WriteVerdict.Skip);
        if (channel == WriteChannel.AutoTick && !Settings.AutoTakeover)
            return new WriteOutcome(false, "自动接管已关闭：决策树每拍只演算",
                _scheduler.LastAppliedStep ?? 0);

        var now = DateTime.UtcNow;
        var req = new WriteRequest(channel, pl1, TempC, now, WriteGatesOpen, Limits.MaxSafeCeiling);
        // 自动通道永远不允许 force；事件通道的 force 只能来自 UI 二次确认。
        var decision = force && channel != WriteChannel.AutoTick
            ? _scheduler.ForceWrite(req) : _scheduler.Evaluate(req);

        if (decision.Verdict == WriteVerdict.Skip)
            return Refuse(decision.Reason, WriteVerdict.Skip);
        if (decision.Verdict != WriteVerdict.Write)
            return Refuse(decision.Reason, decision.Verdict);

        // 第二道硬闸：接近安全上限时只降不升，即使调度器因量化误差放行也不写。
        if (!double.IsNaN(TempC) && TempC >= Limits.MaxSafeCeiling - 3 &&
            decision.StepPl1W > (_scheduler.LastAppliedStep ?? int.MaxValue))
            return Refuse($"温度 {TempC:F1}°C 接近安全上限：只降不升", WriteVerdict.HardBlocked);

        double pl2W = Math.Clamp(Math.Round(pl2), Limits.MinPL2, Limits.MaxPL2);
        // 手工微调页保留用户输入的精确瓦数与 Tau 改写能力；智能通道一律写 1W 量化台阶。
        double pl1W = channel == WriteChannel.ManualSave
            ? Math.Clamp(pl1, Limits.MinPL1, Limits.MaxPL1)
            : Math.Clamp(decision.StepPl1W, Limits.MinPL1, Limits.MaxPL1);
        var r = _guard.Apply(pl1W, pl2W, tau, writeTimeWindow: writeTimeWindow);
        _scheduler.Commit(decision.StepPl1W, now, r.Ok);

        if (r.Ok)
        {
            _autoWriteFailures = 0;
            _lastWriteText = $"已下发 {pl1W:F1}W（{ChannelText()}{ (force ? " · 二次确认" : "") }）";
            N(nameof(LastWriteText)); N(nameof(ModeStatusText)); N(nameof(CooldownText));
            AddAlert("info", "寄存器已下发",
                $"持续功耗 {pl1W:F1}W / 短时功耗 {pl2W:F0}W 已写入并回读校验；{decision.Reason}。" +
                "功耗/频率收敛仍需 Tau 量级时间，不等于性能立刻变化。");
            return new WriteOutcome(true, r.Message, decision.StepPl1W, force);
        }

        _lastWriteText = $"下发失败（{ChannelText()}）：{r.Message}";
        N(nameof(LastWriteText)); N(nameof(ModeStatusText));
        if (++_autoWriteFailures >= 2 && channel == WriteChannel.AutoTick)
        {
            Settings.AutoTakeover = false;
            SettingsStore.Save(Settings);
            AddAlert("danger", "动态微调已熔断", r.Message + "；已关闭「自动接管」，选中档位仍可显式下发。");
        }
        return new WriteOutcome(false, r.Message, decision.StepPl1W);
    }

    // ---------------- 控制 ----------------

    public void Start()
    {
        if (IsRunning) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, Settings.IntervalMs));
        _timer.Start();
        IsRunning = true;
        PushFirst(Alerts, new AlertRow(Now(), "info", "开始监控", $"采样间隔 {Settings.IntervalMs}ms，档位 {ProfileLabel}"));
        AlertCount = Alerts.Count;
    }

    public void Stop()
    {
        _timer.Stop();
        IsRunning = false;
    }

    /// <summary>
    /// 只改「意图层」并落盘：档位 + 天花板。硬件下发由 ApplyProfile / DispatchWrite 独立完成，
    /// 两者解耦，避免把「选中」和「写寄存器」混成同一条链路。
    /// </summary>
    public void SetProfile(string profile)
    {
        string p = NormalizeProfile(profile);
        Profile = p;
        Settings.Profile = p;
        CeilingC = Math.Min(AutoCpuController.TargetCForProfile(p), Limits.MaxSafeCeiling);
        Settings.CeilingC = CeilingC;
        SettingsStore.Save(Settings);
        N(nameof(ModeStatusText));
    }

    public void SetCeiling(double ceiling)
    {
        CeilingC = Math.Clamp(ceiling, 40, Limits.TjMax);
        Settings.CeilingC = CeilingC;
        SettingsStore.Save(Settings);
    }

    public void SetInterval(int ms)
    {
        Settings.IntervalMs = ms;
        _timer.Interval = TimeSpan.FromMilliseconds(ms);
        SettingsStore.Save(Settings);
    }

    public void SetStress(bool on) => SetStress(on, Math.Max(2, System.Environment.ProcessorCount - 2));

    /// <summary>兼容旧入口：只指定线程数，使用标量浮点内核。</summary>
    public void SetStress(bool on, int threads) => SetStress(on, threads, StressKernel.ScalarFloat, 1.0);

    /// <summary>按标准场景对象施加负载（场景库生成的线程数 / 内核 / 占空比）。</summary>
    public void SetStress(bool on, StressScenario scenario)
        => SetStress(on, Math.Max(1, scenario.Threads), scenario.Kernel, scenario.DutyCycle);

    /// <summary>当前压测场景描述，供界面与报告显示。</summary>
    public string StressScenarioText => StressActive
        ? $"{_stressKernel} · {_stressThreads} 线程 · {_stressDuty * 100:F0}%"
        : "未施加";

    /// <summary>
    /// 压测负载开关：可指定线程数、负载内核与占空比。
    /// 场景切换时重建负载；参数完全相同的重复调用直接复用，避免每拍重建线程。
    /// </summary>
    public void SetStress(bool on, int threads, StressKernel kernel, double dutyCycle = 1.0)
    {
        int n = Math.Max(1, threads);
        double duty = double.IsNaN(dutyCycle) ? 1.0 : Math.Clamp(dutyCycle, 0.05, 1.0);
        if (on && StressActive && _stressThreads == n && _stressKernel == kernel &&
            Math.Abs(_stressDuty - duty) < 0.01) return;

        _stress?.Dispose();
        _stress = null;
        _stressThreads = on ? n : -1;
        _stressKernel = kernel;
        _stressDuty = duty;
        if (on) _stress = new StressLoad(n, kernel, duty);
        StressActive = on;
        N(nameof(StressScenarioText));
    }

    /// <summary>
    /// 一键标定（两段式稳态法）：
    ///   1. 轻载基线 12 秒 —— 记录空闲温度与功耗；
    ///   2. 满载升温 —— 拉满核心，直到温度进入平台（最近 10 拍波动 &lt; 0.6°C）或 150 秒超时；
    ///   3. 稳态采样 10 秒 —— 取两段稳态均值做 R_th = ΔT / ΔP。
    /// 与旧的 20 秒单点压测的区别：不再用上升沿斜率（会被热容拉低、把热阻估小），只用两个稳态点。
    /// </summary>
    public void BeginCalibration()
    {
        if (IsCalibrating) return;

        if (!TempAvailable || !PowerFromDriver)
        {
            AddAlert("warn", "无法标定", CalibrationBlockedReason);
            return;
        }

        if (!IsRunning) Start();
        SetStress(false);   // 基线必须是真的空载

        int ms = Math.Max(250, Settings.IntervalMs);
        _calBaselineTicks = Math.Max(6, 12_000 / ms);
        _calRampMaxTicks = Math.Max(40, 150_000 / ms);
        _calPlateauTicks = Math.Max(6, 10_000 / ms);

        _calIdlePts.Clear();
        _calHotPts.Clear();
        _calRecentTemps.Clear();
        _calPhase = CalPhase.Baseline;
        _calPhaseTicks = 0;

        IsCalibrating = true;
        CalibrationProgress = 0;

        AddAlert("info", "标定开始",
            $"第 1 段记录轻载基线（{_calBaselineTicks * ms / 1000} 秒），请保持系统空闲。");
    }

    public void CancelCalibration()
    {
        if (!IsCalibrating) return;
        _calPhase = CalPhase.None;
        IsCalibrating = false;
        CalibrationProgress = 0;
        SetStress(false);
        AddAlert("info", "标定已取消", "未采用本次采样结果，仍使用原热模型。");
    }

    /// <summary>每拍推进标定状态机（由 Tick 调用）。</summary>
    private void StepCalibration()
    {
        _calPhaseTicks++;

        switch (_calPhase)
        {
            case CalPhase.Baseline:
                if (_calPhaseTicks > 2) _calIdlePts.Add((TempC, PowerW));   // 丢前两拍，避开计时抖动
                CalibrationProgress = 20.0 * _calPhaseTicks / Math.Max(1, _calBaselineTicks);
                if (_calPhaseTicks >= _calBaselineTicks)
                {
                    if (_calIdlePts.Count == 0) { FailCalibration("未采到有效轻载样本"); return; }

                    _calPhase = CalPhase.Ramp;
                    _calPhaseTicks = 0;
                    _calRecentTemps.Clear();
                    // 标定满载段用可达功耗最高的场景（全逻辑核 + SIMD/FMA），
                    // 让 ΔP 足够大、温度平台更快到达、热阻拟合更可信；
                    // 其余负载内核由 --scenario-sweep 的场景阶梯覆盖。
                    SetStress(true, Math.Max(2, Environment.LogicalCores), StressKernel.SimdFma);
                    AddAlert("info", "进入满载段",
                        "已记录轻载基线，改用全核 SIMD/FMA 高功耗场景拉满核心，等待温度平台。");
                }
                break;

            case CalPhase.Ramp:
            {
                _calRecentTemps.Enqueue(TempC);
                while (_calRecentTemps.Count > 10) _calRecentTemps.Dequeue();

                CalibrationProgress = Math.Min(85.0,
                    20.0 + 65.0 * _calPhaseTicks / Math.Max(1, _calRampMaxTicks));

                bool plateau = _calPhaseTicks >= 20
                               && _calRecentTemps.Count == 10
                               && _calRecentTemps.Max() - _calRecentTemps.Min() < 0.6;

                if (plateau || _calPhaseTicks >= _calRampMaxTicks)
                {
                    if (!plateau)
                        AddAlert("warn", "未检测到温度平台",
                            "满载已跑满 150 秒，按当前读数取样，热阻可能略偏低。");

                    _calPhase = CalPhase.Plateau;
                    _calPhaseTicks = 0;
                }
                break;
            }

            case CalPhase.Plateau:
                _calHotPts.Add((TempC, PowerW));
                CalibrationProgress = 85.0 + 13.0 * _calPhaseTicks / Math.Max(1, _calPlateauTicks);
                if (_calPhaseTicks >= _calPlateauTicks) FinishCalibration();
                break;
        }
    }

    private void FinishCalibration()
    {
        _calPhase = CalPhase.None;
        IsCalibrating = false;
        CalibrationProgress = 100;
        SetStress(false);

        var idle = Tail(_calIdlePts, 6);
        var hot = Tail(_calHotPts, 8);
        if (idle.Count == 0 || hot.Count == 0) { FailCalibration("稳态样本不足"); return; }

        double ti = idle.Average(x => x.T), pi = idle.Average(x => x.P);
        double th = hot.Average(x => x.T), ph = hot.Average(x => x.P);

        if (!ThermalCalibration.TryTwoPoint(ti, pi, th, ph, out double rth, out string reason))
        {
            FailCalibration(reason);
            return;
        }

        _controller.ApplyCalibratedRth(rth);
        Rth = rth;   // 立即刷新卡片，不必等下一拍（否则会短暂显示标定前的在线值）

        // 稳态模型 T(P) = T_idle + (P − P_idle)·R_th，与界面卡片、决策树共用唯一入口
        double basisW = PowerBasisW;
        double predictedAtMax = ThermalCalibration.PredictSteady(rth, ti, pi, basisW);
        string recommended = predictedAtMax > Limits.MaxSafeCeiling - 3 ? "silent"
            : predictedAtMax < Limits.MaxSafeCeiling - 15 ? "performance"
            : "balanced";

        var outcome = new CalibrationOutcome(true, rth, ti, th, pi, ph, basisW, predictedAtMax, recommended,
            $"{reason}；满载 {ph:F0}W 稳态 {th:F1}°C，按本机 PL1 {basisW:F0}W 预测 {predictedAtMax:F1}°C");

        SaveCalibrationSnapshot(outcome);
        // 轻载基线同时用于在线预测与决策前馈，开机后由 LoadPersistedCalibration 恢复
        _basePowerW = pi;
        _baseTempC = ti;
        _controller.SetThermalBaseline(ti, pi);
        AddAlert("info", "标定完成", outcome.Message + $"，推荐档位「{LabelOf(recommended)}」。");
        CalibrationFinished?.Invoke(outcome);
    }

    private void FailCalibration(string reason)
    {
        _calPhase = CalPhase.None;
        IsCalibrating = false;
        CalibrationProgress = 0;
        SetStress(false);
        AddAlert("warn", "标定未成功", reason + "；继续使用原热模型。");
        CalibrationFinished?.Invoke(new CalibrationOutcome(false, double.NaN, TempC, TempC, PowerW, PowerW,
            PowerBasisW, double.NaN, string.Empty, reason));
    }

    /// <summary>取列表尾部 n 个样本（稳态均值只取最后几拍）。</summary>
    private static List<(double T, double P)> Tail(List<(double T, double P)> src, int n) =>
        src.Count <= n
            ? new List<(double T, double P)>(src)
            : src.GetRange(src.Count - n, n);

    private static string LabelOf(string p) => p switch
    {
        "silent" => "静音优先",
        "performance" => "性能优先",
        _ => "均衡"
    };

    public void AddAlert(string level, string title, string detail)
    {
        // 同类事件 30s 内只记一次，避免刷屏
        string key = level + "|" + title;
        if (_alertDebounce.TryGetValue(key, out var last) && (DateTime.Now - last).TotalSeconds < 30)
            return;
        _alertDebounce[key] = DateTime.Now;

        PushFirst(Alerts, new AlertRow(Now(), level, title, detail), LogCap);
        AlertCount = Alerts.Count;
    }

    // ---------------- 主循环 ----------------

    /// <summary>冒烟/单测用：立即执行一拍采样与决策，不依赖定时器。</summary>
    public void SampleOnce() => Tick();

    private void Tick()
    {
        if (_telemetry is not { } telemetry)
        {
            // 传感器还在后台枚举（约 2 秒）：这一拍跳过采样与决策，只把「正在读取」状态推到界面。
            // 不写任何值，避免指标卡先显示 0 再跳变。
            N(nameof(SensorsReady)); N(nameof(SensorsWarming)); N(nameof(SensorStatusText));
            N(nameof(StatusText)); N(nameof(StatusLevel));
            return;
        }

        var s = telemetry.Read();

        LoadPct = double.IsNaN(s.LoadPercent) ? 0 : s.LoadPercent;

        // 监控面板扩展指标：逐拍刷新，读不到就保持 NaN，界面显示 N/A
        _gpuLoadPct = s.GpuLoadPercent;
        _gpuMemPct = s.GpuMemoryPercent;
        _gpuMemUsedMb = s.GpuMemoryUsedMb;
        _gpuMemTotalMb = s.GpuMemoryTotalMb;
        _fanRpm = s.FanRpm;
        _fanMeasuredPct = s.FanControlPercent;
        _fanSource = s.FanSource;
        _fanRawValue = s.FanRawValue;
        _fanStagnantReads = s.FanStagnantReads;
        _fanStagnant = s.FanStagnant;
        _gpuName = s.GpuName;
        _gpuSource = s.GpuSource;
        _lhmCpuTotal = s.LhmCpuTotal;
        _gpuTempC = s.GpuTempC;
        N(nameof(CpuLoadText)); N(nameof(CpuLoadHintText));
        N(nameof(GpuLoadText)); N(nameof(GpuMemoryText));
        N(nameof(GpuMemoryDetailText)); N(nameof(GpuMemoryHintText));
        N(nameof(GpuCardSubtitleText)); N(nameof(FanMeasuredText));
        N(nameof(FanRawText)); N(nameof(FanMeasuredHintText)); N(nameof(FanDetailText));
        N(nameof(FanHintBrush)); N(nameof(GpuSourceText)); N(nameof(GpuNameText));
        N(nameof(GpuHintText));

        if (!double.IsNaN(s.PowerW)) PowerW = s.PowerW;
        PowerSource = s.Source;
        PowerFromDriver = s.PowerMeasured;
        if (!double.IsNaN(s.MaxCoreGhz)) ClockGhz = s.MaxCoreGhz;

        TempAvailable = !double.IsNaN(s.TempC);
        // 第一拍真实读数出来后再判定「哪些参数可取」，避免用默认值猜可读性
        ResolvePendingCapability();
        if (!TempAvailable)
        {
            _noTempTicks++;
            if (_noTempTicks == 3)
                AddAlert("warn", "温度读数不可用",
                    DriverAvailable
                        ? "驱动可用但传感器无读数，请检查是否被虚拟化/固件屏蔽。"
                        : "未检测到硬件访问能力：请以管理员身份重新启动，并在「诊断与日志」页确认调优组件已就绪。");

            if (IsCalibrating) FailCalibration("标定过程中温度读数中断");
            Ticked?.Invoke();
            return;
        }
        _noTempTicks = 0;
        TempC = s.TempC;

        double dt = double.IsNaN(_prevTemp) ? 0.0
            : Math.Clamp((TempC - _prevTemp) / (_timer.Interval.TotalSeconds), -5, 5);
        _prevTemp = TempC;

        // 稳态预测基线：轻载且温度稳定时记录「同源」的 (T, P) 样本。
        // 旧逻辑只在功耗创新低时才更新温度，基线温度会停在标定刚结束的高温残留，
        // 与界面当前读数不同时刻，预测值无法复算（曾出现 86.7℃ 对不上 48℃@10W）。
        if (LoadPct < 15 && Math.Abs(dt) < 0.5 && !double.IsNaN(PowerW) &&
            (double.IsNaN(_basePowerW) || PowerW <= _basePowerW + 1.0))
        {
            _basePowerW = PowerW;
            _baseTempC = TempC;
            _controller.SetThermalBaseline(_baseTempC, _basePowerW);
            N(nameof(PredictedBaselineText));
        }

        // 刷新固件实读值（高级参数页「当前生效」列 / 功耗基准）
        RefreshFirmwareValues();

        var dec = _controller.Update(
            tempC: TempC,
            powerW: PowerW,
            ceilingC: CeilingC,
            ambientC: AmbientC,
            profile: Profile,
            perf: new PerfSample { CoreClocksGhz = ClockGhz, MaxTurboGhz = 0 },
            loadPct: LoadPct,
            dTdt: dt,
            firmwarePl1W: _firmwarePl1W > 0 ? _firmwarePl1W : double.NaN);

        Pl1W = dec.Pl1W;
        Pl2W = dec.Pl2W;
        TauS = dec.TauS;
        Epp = dec.Epp;
        FanPercent = _controller.LatestReceipt?.Action.TargetFanPercent ?? 0;
        Rth = dec.RConfident ? dec.Reff : double.NaN;

        // 预测稳态：功耗基准取「固件实际 PL1」与「决策目标」较小者——
        // 只读遥控下决策写不进 MSR，长期功耗就是固件值，按决策值外推会虚高
        double predBasis = Math.Min(PowerBasisW, dec.Pl1W);
        PredictedBasisText = HasRth ? $"按 {predBasis:F0}W 外推" : "--";
        PredictedSteadyC = dec.RConfident
            ? (double.IsNaN(_basePowerW)
                ? ThermalCalibration.PredictSteady(dec.Reff, TempC, PowerW, predBasis)
                : ThermalCalibration.PredictSteady(dec.Reff, _baseTempC, _basePowerW, predBasis))
            : double.NaN;
        ThermalCapText = double.IsNaN(dec.ThermalCapW)
            ? "标定前馈未启用：完成两段式标定后，热模型会按天花板钳制决策上限"
            : $"标定前馈已生效：目标功耗上限 {dec.ThermalCapW:F0}W（天花板 {CeilingC:F0}°C）";
        LastRule = _controller.LatestReceipt?.TriggeredRuleName ?? "—";
        LastReason = dec.Reason;
        LastPath = string.Join(" → ", _controller.LatestReceipt?.EvaluatedPath ?? Array.Empty<string>());

        Push(TempHistory, TempC, HistoryCap);
        Push(Pl1History, Pl1W, HistoryCap);
        Push(PowerHistory, PowerW, HistoryCap);

        // 统一时序图：每拍对所有序列各推一点，不可读写 NaN，保证时间轴索引对齐。
        PushMetric(_sTemp, TempC);
        PushMetric(_sPl1, dec.Pl1W);
        PushMetric(_sPower, PowerW);
        PushMetric(_sCpu, LoadPct);
        PushMetric(_sGpu, _gpuLoadPct);
        PushMetric(_sGpuMem, _gpuMemPct);
        // 图例同步显示 EC 原始字节；恒定判定由采集层逐拍给出，这里只呈现不加工。
        _sFan.Extra = double.IsNaN(_fanRawValue) ? "" : $" (原始 {_fanRawValue:F0})";
        PushMetric(_sFan, _fanMeasuredPct);
        if (!double.IsNaN(_fanRpm))
        {
            EnsureFanRpmSeries();
            PushMetric(_sFanRpm!, _fanRpm);
        }
        else if (_sFanRpm is not null)
        {
            PushMetric(_sFanRpm, double.NaN);
        }
        N(nameof(HistoryCaptionText));
        N(nameof(HistoryStartText));

        string arrow = dec.Direction switch { > 0 => "↑", < 0 => "↓", _ => "→" };
        var row = new DecisionRow(
            Now(), LastRule, dec.Reason, $"{dec.Pl1W:F1}W {arrow}", arrow, dec.Limit.ToString(), LastPath);
        PushFirst(Decisions, row, LogCap);
        PushFirst(Samples, new SampleRow(
            Now(),
            $"{TempC:F1}",
            $"{LoadPct:F0}%",
            PowerW <= 0 ? "N/A" : $"{PowerW:F0}W",
            double.IsNaN(FwPl1W) ? $"{dec.Pl1W:F1}W*" : $"{FwPl1W:F1}W",
            LastRule,
            double.IsNaN(_fanMeasuredPct) ? "--" : $"{_fanMeasuredPct:F0}%"), TableCap);

        _raw.Add(new RawSample(DateTime.Now, TempC, LoadPct, PowerW, dec.Pl1W,
            LastRule.Replace(",", "，"), _fanMeasuredPct, _fanRawValue));
        if (_raw.Count > 3600) _raw.RemoveAt(0);

        // 事件：触顶 / 硬件热保护 / 恢复
        if (TempC >= CeilingC)
            AddAlert("warn", "温度触顶", $"当前 {TempC:F1}°C 已到目标天花板 {CeilingC:F0}°C，决策为降功耗。");
        if (TempC >= Limits.MaxSafeCeiling)
            AddAlert("danger", "接近硬件上限", $"{TempC:F1}°C ≥ 安全上限 {Limits.MaxSafeCeiling:F0}°C，应降低负载或加强散热。");

        // v0.5.0 研究埋点：每个真实触顶热事件落一行 CSV（本期不做自适应冷却公式）。
        ObserveThermalEvent();

        if (!IsCalibrating)
        {
            if (_pendingProfileWrite)
            {
                // 首屏选中档位时还没有温度采样：这里用首拍真实决策补齐一次事件下发。
                _pendingProfileWrite = false;
                DispatchWrite(WriteChannel.ProfileEvent, dec.Pl1W, dec.Pl2W, dec.TauS);
            }
            else
            {
                DispatchWrite(WriteChannel.AutoTick, dec.Pl1W, dec.Pl2W, dec.TauS);
            }
        }

        if (IsCalibrating) StepCalibration();

        N(nameof(CooldownText)); N(nameof(ModeStatusText));
        Ticked?.Invoke();
    }

    // ---------------- 工具 ----------------

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    private static void Push<T>(ObservableCollection<T> list, T item, int cap = HistoryCap)
    {
        list.Add(item);
        while (list.Count > cap) list.RemoveAt(0);
    }

    /// <summary>最新在上：流水与明细表用倒序插入。</summary>
    private static void PushFirst<T>(ObservableCollection<T> list, T item, int cap = LogCap)
    {
        list.Insert(0, item);
        while (list.Count > cap) list.RemoveAt(list.Count - 1);
    }

    // ---------------- v0.5.0 热事件研究埋点 ----------------

    private static string ThermalEventCsvPath =>
        Path.Combine(SettingsStore.DataDirectory, "thermal-events.csv");

    /// <summary>
    /// 每拍观察一次热事件。本期只采数、不实现「超温幅度 → 冷却时长」公式：
    /// 本机 1001 个历史压测样本最高 78°C，零个 ≥85°C 触顶事件，任何系数都是未验证假设。
    /// </summary>
    private void ObserveThermalEvent()
    {
        var done = _thermalEvents.Observe(TempC, CeilingC,
            _scheduler.LastAppliedStep ?? 0, DateTime.UtcNow);
        if (done is not null) AppendThermalEvent(done);
    }

    private void AppendThermalEvent(ThermalEventRecord e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Num(double v) => double.IsNaN(v) ? string.Empty : v.ToString("F2", inv);
            var line =
                $"{e.StartedAt:yyyy-MM-dd HH:mm:ss}," +
                $"{e.PeakC.ToString("F1", inv)},{e.CeilingC.ToString("F1", inv)}," +
                $"{e.OvershootC.ToString("F1", inv)},{e.StepDownCount},{e.StepDownWatts.ToString("F1", inv)}," +
                $"{e.EventDurationS.ToString("F1", inv)},{Num(e.FallRateP50)},{Num(e.FallRateP95)}," +
                $"{e.CooldownSeconds.ToString("F1", inv)},{e.SecondaryTouches60s},{e.EndReason}";

            if (!File.Exists(ThermalEventCsvPath))
            {
                File.WriteAllText(ThermalEventCsvPath,
                    "开始时间(UTC),峰值温度(°C),天花板(°C),超温幅度(°C),降档次数,降档总瓦数(W)," +
                    "事件时长(s),回落速率P50(°C/s),回落速率P95(°C/s),实际冷却(s)," +
                    "恢复后60s二次触顶次数,结束原因" + System.Environment.NewLine + line + System.Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            else
            {
                File.AppendAllText(ThermalEventCsvPath, line + System.Environment.NewLine, new UTF8Encoding(false));
            }

            AddAlert("info", "热事件已记录",
                $"峰值 {e.PeakC:F1}°C / 天花板 {e.CeilingC:F0}°C，超温 {e.OvershootC:F1}°C，" +
                $"降档 {e.StepDownCount} 次共 {e.StepDownWatts:F0}W，恢复后 60s 二次触顶 {e.SecondaryTouches60s} 次。" +
                "已写入 thermal-events.csv（研究埋点，不参与冷却策略）。");
        }
        catch
        {
            // 埋点失败绝不影响主循环与热控安全
        }
    }

    /// <summary>导出 CSV（UTF-8 BOM，Excel 直接打开不乱码，AC-18）。</summary>
    public int ExportCsv(string path)
    {
        var sb = new StringBuilder();
        // 风扇两列分开：换算档位百分比 + EC 原始字节，便于事后核对读数是否真的在变化。
        sb.AppendLine("时间,温度(°C),负载(%),包功耗(W),决策PL1(W),风扇(%),风扇原始值,命中规则");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var r in _raw)
            sb.AppendLine($"{r.At:yyyy-MM-dd HH:mm:ss},{r.Temp:F1},{r.Load:F0},{r.Power:F0},{r.Pl1:F1}," +
                          $"{(double.IsNaN(r.FanPct) ? "" : r.FanPct.ToString("F1", inv))}," +
                          $"{(double.IsNaN(r.FanRaw) ? "" : r.FanRaw.ToString("F0", inv))},{r.Rule}");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return _raw.Count;
    }

    private void N(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<string> DumpSensors() =>
        _telemetry?.DumpSensors() ?? new[] { "传感器尚未就绪" };

    public void Dispose()
    {
        _timer.Stop();
        // 未完结的热事件在退出时收尾，避免关机直接终止进程导致丢样本。
        var pending = _thermalEvents.Flush(DateTime.UtcNow);
        if (pending is not null) AppendThermalEvent(pending);
        _stress?.Dispose();
        _telemetry?.Dispose();
        _guard?.Dispose();
    }
}
