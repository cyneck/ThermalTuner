using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;
using ThermalPilot.Core;
using ThermalPilot.Core.Environment;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Msr;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.App.Services;

public sealed record DecisionRow(
    string Time, string Rule, string Reason, string Pl1, string Direction, string Limit, string Path);

public sealed record AlertRow(string Time, string Level, string Title, string Detail);

public sealed record SampleRow(
    string Time, string Temp, string Load, string Power, string Pl1, string Rule);

/// <summary>界面状态分级，驱动顶部状态条与侧栏角标。</summary>
public enum StatusLevel { Ok, Info, Warn, Danger }

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
    private readonly HardwareTelemetry _telemetry = new();
    private readonly AutoCpuController _controller;
    private readonly Dictionary<string, DateTime> _alertDebounce = new();
    private MsrGuard? _guard;
    private DateTime _lastAutoWrite = DateTime.MinValue;
    private double _lastAppliedPl1001;
    private int _autoWriteFailures;
    private readonly List<RawSample> _raw = new();

    private readonly record struct RawSample(
        DateTime At, double Temp, double Load, double Power, double Pl1, string Rule);

    private StressLoad? _stress;
    private double _prevTemp = double.NaN;
    private int _noTempTicks;
    private int _calibrationTicksLeft;
    private int _calibrationTotalTicks;

    public SessionState()
    {
        Settings = SettingsStore.Load();
        Environment = HardwareProbe.Probe();
        _controller = new AutoCpuController(HardwareLimitResolver.Resolve(Environment));
        IsElevated = CheckElevated();
        CeilingC = Math.Min(Settings.CeilingC, Limits.MaxSafeCeiling);
        InitMsr();

        _timer.Interval = TimeSpan.FromMilliseconds(Settings.IntervalMs);
        _timer.Tick += (_, _) => Tick();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>每拍结束后触发，供页面刷新图表等自绘元素。</summary>
    public event Action? Ticked;

    // ---------------- 静态信息 ----------------

    public AppSettings Settings { get; }
    public CpuEnvironment Environment { get; }
    public HardwareLimits Limits => _controller.Limits;
    public AutoCpuController Controller => _controller;
    public bool DriverAvailable => _telemetry.DriverAvailable;
    public int SensorCount => _telemetry.SensorCount;
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
    public string ElevationText => IsElevated ? "管理员" : "普通用户";
    public string CoreText => $"{Environment.PhysicalCores}P / {Environment.LogicalCores}L" +
                              (Environment.IsHybridArchitecture ? " · 混合架构" : string.Empty);
    public string TjMaxText => $"TjMax {Limits.TjMax:F0}°C · 安全上限 {Limits.MaxSafeCeiling:F0}°C";
    public string PlRangeText => $"PL1 [{Limits.MinPL1:F0}–{Limits.MaxPL1:F0}W] · PL2 [{Limits.MinPL2:F0}–{Limits.MaxPL2:F0}W]";
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

    private bool _powerFromDriver;
    /// <summary>功耗是否来自驱动实测；否则为按负载粗估（界面必须区分，避免误读）。</summary>
    public bool PowerFromDriver
    {
        get => _powerFromDriver;
        private set { _powerFromDriver = value; N(nameof(PowerFromDriver)); N(nameof(PowerCaption)); }
    }
    public string PowerCaption => _powerFromDriver ? "包功耗（实测）" : "包功耗（估算）";
    public string PowerText => _powerW <= 0 ? "N/A" : _powerW.ToString("F0");

    private double _clockGhz;
    public double ClockGhz
    {
        get => _clockGhz;
        private set { _clockGhz = value; N(nameof(ClockGhz)); N(nameof(ClockText)); }
    }
    public string ClockText => _clockGhz <= 0 ? "N/A" : _clockGhz.ToString("F2");

    private double _pl1W = 35.0;
    public double Pl1W
    {
        get => _pl1W;
        private set { _pl1W = value; N(nameof(Pl1W)); N(nameof(Pl1Text)); }
    }
    public string Pl1Text => _pl1W.ToString("F1");

    private double _pl2W = 44.0;
    public double Pl2W
    {
        get => _pl2W;
        private set { _pl2W = value; N(nameof(Pl2W)); N(nameof(Pl2Text)); }
    }
    public string Pl2Text => _pl2W.ToString("F1");

    private double _tauS = 16;
    public double TauS
    {
        get => _tauS;
        private set { _tauS = value; N(nameof(TauS)); N(nameof(TauText)); }
    }
    public string TauText => _tauS.ToString("F0");

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
    public string FanText => _fanPercent <= 0 ? "N/A" : _fanPercent.ToString("F0");

    private double _rth = double.NaN;
    public double Rth
    {
        get => _rth;
        private set { _rth = value; N(nameof(Rth)); N(nameof(RthText)); N(nameof(HasRth)); }
    }
    public bool HasRth => !double.IsNaN(_rth);
    public string RthText => double.IsNaN(_rth) ? "--" : _rth.ToString("F2");

    private double _predicted = double.NaN;
    public double PredictedSteadyC
    {
        get => _predicted;
        private set { _predicted = value; N(nameof(PredictedSteadyC)); N(nameof(PredictedText)); }
    }
    public string PredictedText => double.IsNaN(_predicted) ? "--" : _predicted.ToString("F1");

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
        private set { _tempAvailable = value; N(nameof(TempAvailable)); N(nameof(StatusText)); N(nameof(StatusLevel)); N(nameof(StatusAction)); }
    }

    private bool _isCalibrating;
    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set { _isCalibrating = value; N(nameof(IsCalibrating)); }
    }

    private double _calibrationProgress;
    public double CalibrationProgress
    {
        get => _calibrationProgress;
        private set { _calibrationProgress = value; N(nameof(CalibrationProgress)); N(nameof(CalibrationText)); }
    }
    public string CalibrationText => _isCalibrating
        ? $"标定中 {_calibrationProgress:F0}%（保持负载，勿操作系统）"
        : "20 秒采集轻载/负载两点，拟合热阻并给出推荐档位";

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
        private set { _ceilingC = value; N(nameof(CeilingC)); N(nameof(CeilingText)); }
    }
    public string CeilingText => $"{_ceilingC:F0}°C";

    // ---------------- 环境状态条 ----------------

    public StatusLevel StatusLevel
    {
        get
        {
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
            if (!DriverAvailable) return "温度不可用 · 未检测到硬件访问驱动";
            if (!TempAvailable) return IsElevated ? "温度不可用 · 传感器无读数" : "受限权限 · 读取温度需要管理员";
            if (Environment.Generation == 0) return "保守基线 · 未识别平台，已按 7 代区间运行";
            return "运行正常";
        }
    }

    public string StatusAction => DriverAvailable ? "查看诊断" : "安装 PawnIO";

    // ---------------- 集合 ----------------

    public ObservableCollection<double> TempHistory { get; } = new();
    public ObservableCollection<double> Pl1History { get; } = new();
    public ObservableCollection<double> PowerHistory { get; } = new();
    public ObservableCollection<DecisionRow> Decisions { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();
    public ObservableCollection<SampleRow> Samples { get; } = new();

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
    /// <summary>底栏一行状态：当前到底会不会真写硬件。</summary>
    public string FooterMode => Settings.AllowRealWrite && MsrWritable ? "写入已授权" : "只读监控";

    public string MsrSummary => MsrWritable
        ? (Settings.AllowRealWrite ? "可写 · 已授权" : "可写 · 未授权")
        : MsrStatusText;

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
                if (_guard.HasPendingRestore)
                {
                    var r = _guard.RestorePendingIfAny();
                    AddAlert(r.Ok ? "warn" : "danger", "上次未正常退出", r.Message);
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
    }

    public void SetAllowRealWrite(bool on)
    {
        Settings.AllowRealWrite = on;
        if (!on) Settings.AutoTakeover = false;
        SettingsStore.Save(Settings);
        N(nameof(RealWriteEnabled));
        N(nameof(MsrSummary));
    }

    /// <summary>真实写入；未授权或通道不可写时直接拒绝（不静默降级成演练）。</summary>
    public MsrWriteResult ApplyLimits(double pl1, double pl2, double tau, bool writeTimeWindow = false)
    {
        if (_guard is null)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"MSR 通道不可用：{MsrDetail}");
        if (!Settings.AllowRealWrite)
            return new MsrWriteResult(MsrWriteOutcome.NotWritable, "未开启「允许写入 MSR」（设置页）");

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

    public MsrWriteResult RestoreOriginal()
    {
        if (_guard is null)
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, $"MSR 通道不可用：{MsrDetail}");

        var result = _guard.Restore();
        AddAlert(result.Ok ? "info" : "danger",
            result.Ok ? "已恢复原值" : "恢复失败", result.Message);
        return result;
    }

    /// <summary>自动接管：双开关 + 冷却保护 + 失败熔断（PRD G-08）。</summary>
    private void TryAutoApply(double targetPl1, double targetPl2, double targetTau)
    {
        if (_guard is null) return;
        if (!MsrWritable || !Settings.AllowRealWrite || !Settings.AutoTakeover) return;
        if ((DateTime.UtcNow - _lastAutoWrite).TotalSeconds < 3) return;

        bool hot = TempC >= Limits.MaxSafeCeiling - 3;
        if (hot && targetPl1 > _lastAppliedPl1001) return;   // 高温只许降不许升

        if (Math.Abs(targetPl1 - _lastAppliedPl1001) < 1.0) return;

        _lastAutoWrite = DateTime.UtcNow;
        var r = _guard.Apply(Math.Clamp(targetPl1, Limits.MinPL1, Limits.MaxPL1),
                             Math.Clamp(targetPl2, Limits.MinPL2, Limits.MaxPL2), targetTau, writeTimeWindow: false);

        if (r.Ok)
        {
            _lastAppliedPl1001 = targetPl1;
            _autoWriteFailures = 0;
        }
        else if (++_autoWriteFailures >= 2)
        {
            Settings.AutoTakeover = false;
            SettingsStore.Save(Settings);
            AddAlert("danger", "自动接管已熔断", r.Message);
        }
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

    public void SetProfile(string profile)
    {
        Profile = profile;
        Settings.Profile = profile;
        CeilingC = Math.Min(AutoCpuController.TargetCForProfile(profile), Limits.MaxSafeCeiling);
        Settings.CeilingC = CeilingC;
        SettingsStore.Save(Settings);
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

    public void SetStress(bool on)
    {
        if (on == StressActive) return;
        if (on)
            _stress = new StressLoad(Math.Max(2, System.Environment.ProcessorCount - 2));
        else
        {
            _stress?.Dispose();
            _stress = null;
        }
        StressActive = on;
    }

    /// <summary>一键标定：20 秒压测采集，结束后给出热阻与推荐档位。</summary>
    public void BeginCalibration()
    {
        if (IsCalibrating) return;
        if (!TempAvailable && !IsRunning) Start();

        IsCalibrating = true;
        _calibrationTotalTicks = 20 * 1000 / Math.Max(250, Settings.IntervalMs);
        _calibrationTicksLeft = _calibrationTotalTicks;
        CalibrationProgress = 0;
        _controller.ResetResistance();
        SetStress(true);
    }

    public void CancelCalibration()
    {
        if (!IsCalibrating) return;
        IsCalibrating = false;
        CalibrationProgress = 0;
        SetStress(false);
    }

    private void FinishCalibration()
    {
        IsCalibrating = false;
        CalibrationProgress = 100;
        SetStress(false);

        if (!HasRth)
        {
            AddAlert("warn", "标定未成功", "未能拟合出可信热阻（数据波动或负载不足），继续使用默认模型。");
            return;
        }

        double predicted = TempC + (Pl1W - PowerW) * _rth;
        string recommended = predicted > Limits.MaxSafeCeiling - 3 ? "silent"
            : predicted < Limits.MaxSafeCeiling - 15 ? "performance"
            : "balanced";

        AddAlert("info", "标定完成",
            $"热阻 {_rth:F2} K/W，预测稳态 {predicted:F1}°C，推荐档位「{LabelOf(recommended)}」。");
    }

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
        var s = _telemetry.Read();

        LoadPct = double.IsNaN(s.LoadPercent) ? 0 : s.LoadPercent;
        if (!double.IsNaN(s.PowerW)) PowerW = s.PowerW;
        PowerFromDriver = s.Source == "LHM";
        if (!double.IsNaN(s.MaxCoreGhz)) ClockGhz = s.MaxCoreGhz;

        TempAvailable = !double.IsNaN(s.TempC);
        if (!TempAvailable)
        {
            _noTempTicks++;
            if (_noTempTicks == 3)
                AddAlert("warn", "温度读数不可用",
                    DriverAvailable
                        ? "驱动可用但传感器无读数，请检查是否被虚拟化/固件屏蔽。"
                        : "未检测到硬件访问驱动：安装 PawnIO 后以管理员重启。");
            Ticked?.Invoke();
            return;
        }
        _noTempTicks = 0;
        TempC = s.TempC;

        double dt = double.IsNaN(_prevTemp) ? 0.0
            : Math.Clamp((TempC - _prevTemp) / (_timer.Interval.TotalSeconds), -5, 5);
        _prevTemp = TempC;

        var dec = _controller.Update(
            tempC: TempC,
            powerW: PowerW,
            ceilingC: CeilingC,
            ambientC: AmbientC,
            profile: Profile,
            perf: new PerfSample { CoreClocksGhz = ClockGhz, MaxTurboGhz = 0 },
            loadPct: LoadPct,
            dTdt: dt);

        Pl1W = dec.Pl1W;
        Pl2W = dec.Pl2W;
        TauS = dec.TauS;
        Epp = dec.Epp;
        FanPercent = _controller.LatestReceipt?.Action.TargetFanPercent ?? 0;
        Rth = dec.RConfident ? dec.Reff : double.NaN;
        PredictedSteadyC = dec.PredictedSteadyC;
        LastRule = _controller.LatestReceipt?.TriggeredRuleName ?? "—";
        LastReason = dec.Reason;
        LastPath = string.Join(" → ", _controller.LatestReceipt?.EvaluatedPath ?? Array.Empty<string>());

        Push(TempHistory, TempC, HistoryCap);
        Push(Pl1History, Pl1W, HistoryCap);
        Push(PowerHistory, PowerW, HistoryCap);

        string arrow = dec.Direction switch { > 0 => "↑", < 0 => "↓", _ => "→" };
        var row = new DecisionRow(
            Now(), LastRule, dec.Reason, $"{dec.Pl1W:F1}W {arrow}", arrow, dec.Limit.ToString(), LastPath);
        PushFirst(Decisions, row, LogCap);
        PushFirst(Samples, new SampleRow(
            Now(),
            $"{TempC:F1}",
            $"{LoadPct:F0}%",
            PowerW <= 0 ? "N/A" : $"{PowerW:F0}W",
            $"{dec.Pl1W:F1}W",
            LastRule), TableCap);

        _raw.Add(new RawSample(DateTime.Now, TempC, LoadPct, PowerW, dec.Pl1W,
            LastRule.Replace(",", "，")));
        if (_raw.Count > 3600) _raw.RemoveAt(0);

        // 事件：触顶 / 硬件热保护 / 恢复
        if (TempC >= CeilingC)
            AddAlert("warn", "温度触顶", $"当前 {TempC:F1}°C 已到目标天花板 {CeilingC:F0}°C，决策为降功耗。");
        if (TempC >= Limits.MaxSafeCeiling)
            AddAlert("danger", "接近硬件上限", $"{TempC:F1}°C ≥ 安全上限 {Limits.MaxSafeCeiling:F0}°C，应降低负载或加强散热。");

        if (!IsCalibrating) TryAutoApply(dec.Pl1W, dec.Pl2W, dec.TauS);

        if (IsCalibrating)
        {
            _calibrationTicksLeft--;
            CalibrationProgress = 100.0 * (_calibrationTotalTicks - _calibrationTicksLeft) / Math.Max(1, _calibrationTotalTicks);
            if (_calibrationTicksLeft <= 0) FinishCalibration();
        }

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

    /// <summary>导出 CSV（UTF-8 BOM，Excel 直接打开不乱码，AC-18）。</summary>
    public int ExportCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,温度(°C),负载(%),包功耗(W),决策PL1(W),命中规则");
        foreach (var r in _raw)
            sb.AppendLine($"{r.At:yyyy-MM-dd HH:mm:ss},{r.Temp:F1},{r.Load:F0},{r.Power:F0},{r.Pl1:F1},{r.Rule}");

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

    public IEnumerable<string> DumpSensors() => _telemetry.DumpSensors();

    public void Dispose()
    {
        _timer.Stop();
        _stress?.Dispose();
        _telemetry.Dispose();
        _guard?.Dispose();
    }
}
