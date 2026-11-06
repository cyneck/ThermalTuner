using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Principal;
using System.Text;
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

public sealed record AlertRow(string Time, string Level, string Title, string Detail);

public sealed record SampleRow(
    string Time, string Temp, string Load, string Power, string Pl1, string Rule);

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
    private readonly HardwareTelemetry _telemetry = new();
    private readonly AutoCpuController _controller;
    private readonly Dictionary<string, DateTime> _alertDebounce = new();
    private MsrGuard? _guard;
    private DateTime _lastAutoWrite = DateTime.MinValue;
    private double _lastAppliedPl1001;
    private int _autoWriteFailures;
    /// <summary>存在手动微调值时，决策树不得自动写寄存器覆盖用户的优化结果。</summary>
    private bool _manualLock;

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

    private readonly record struct RawSample(
        DateTime At, double Temp, double Load, double Power, double Pl1, string Rule);

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

    public SessionState()
    {
        Settings = SettingsStore.Load();
        Environment = HardwareProbe.Probe();
        CpuCaps = ThermalTuner.Core.Taxonomy.CpuCapability.Probe(Environment.BrandString);
        _controller = new AutoCpuController(HardwareLimitResolver.Resolve(Environment));
        LoadPersistedCalibration();
        IsElevated = CheckElevated();
        CeilingC = Math.Min(Settings.CeilingC, Limits.MaxSafeCeiling);
        InitMsr();

        _timer.Interval = TimeSpan.FromMilliseconds(Settings.IntervalMs);
        _timer.Tick += (_, _) => Tick();
        // 首次安装启动 + 每次启动都刷新一次只读能力快照：
        // 提权重启后寄存器可读性会变，禁用清单必须跟着更新，不能沿用旧快照。
        InitializeEnvironmentCheck();
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
    public bool RaplAvailable => _rapl?.Available == true;
    public string RaplText => _rapl?.Detail ?? "未接入（MSR 通道不可用）";
    public string ElevationText => IsElevated ? "管理员" : "普通用户";
    public string CoreText => $"{Environment.PhysicalCores}P / {Environment.LogicalCores}L" +
                              (Environment.IsHybridArchitecture ? " · 混合架构" : string.Empty);
    public string TjMaxText => $"TjMax {Limits.TjMax:F0}°C · 安全上限 {Limits.MaxSafeCeiling:F0}°C";
    public string PlRangeText => $"PL1 [{Limits.MinPL1:F0}–{Limits.MaxPL1:F0}W] · PL2 [{Limits.MinPL2:F0}–{Limits.MaxPL2:F0}W]";

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

    /// <summary>EPP 是否可编辑：0x774 读不到时禁用，避免把决策默认值当成用户微调值保存。</summary>
    public bool EppEditable => !double.IsNaN(_fwEpp);
    public string EppHintText => EppEditable
        ? string.Empty
        : "当前通道读不到 0x774：EPP 微调已禁用，PL1 / PL2 / Tau 不受影响。";

    // ---- 睿频倍率（MSR 0x1AD）：0x1AD 读不到时禁用编辑，绝不拿决策值冒充用户微调值 ----
    public bool TurboEditable => _fwTurbo is not null;
    public string FwTurboText => _fwTurbo is { } t ? TurboRatioLimit.ShortTiers(t) : "--";
    public int FwTurboAllCoreRatio => _fwTurbo?.AllCore ?? 0;
    public int FwTurboMaxRatio => _fwTurbo?.MaxTier ?? 0;
    public string TurboRangeText => _fwTurbo is { } t
        ? $"[{TurboRatioLimit.MinRatio}, {t.MaxTier}] ×100MHz"
        : "--（0x1AD 不可读）";
    public string TurboHintText => _fwTurbo is { } t
        ? $"出厂档位 1C {t.OneCoreGhz:F1} / 全核 {t.AllCoreGhz:F1} GHz；" +
          $"允许在 [8, {t.MaxTier}] 内做规格内档位配置（不做超频）。" +
          "本机 A/B 实测：拉平到 38x 后全核频率无提升，仅增加功耗与温度，不作为性能卖点。"
        : "当前通道读不到 0x1AD：睿频倍率调节已禁用。";

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
        true => "本机自检：0x150 可写，电压偏移可用。",
        false => "本机自检：0x150 写入被 BIOS / 微码拒绝，该个性化项已禁用；重跑 --voltage-probe 可复核。",
        _ => "本机尚未跑 --voltage-probe：写入可能被拒，失败会自动回零。"
    }) + CpuCaps.VoltageHint +
        "；写入易失寄存器，重启或退出程序即恢复，属相对安全但可能导致不稳定。";
    public string CpuCapabilityText => CpuCaps.MatrixText;

    /// <summary>
    /// 0x774 平台限制说明：本机实测（2026-11-05 --hwp-probe）写入瞬时成功、250ms 后
    /// 被系统电源策略 / Intel DTT 覆盖回原值。因此 HWP 项可编辑但不保证持续生效，
    /// L2 智能标定只对 0x610 的 PL1/PL2/Tau 做稳定推荐。
    /// </summary>
    public string HwpHintText =>
        "通用参数提示：0x774 的 EPP / Min-Max Perf / Activity Window 写入瞬时可成功，但部分平台会被" +
        "Windows 电源计划或 Intel DTT 周期性覆盖（本机实测 250ms 内即被还原）。" +
        "请在改完后用 --hwp-probe 复核；标定的稳定推荐只覆盖 0x610 的 PL1 / PL2 / Tau。";

    /// <summary>L2 智能标定推荐（只含通用安全参数，不碰电压/睿频/温度墙）。</summary>
    public string CalibrationLayerText => Settings.Calibration is { } c
        ? $"标定推荐（L2）：Rth {c.Rth:F2} K/W" +
          (c.RecommendedPl1W > 0 ? $" · 建议 PL1 {c.RecommendedPl1W:F1}W" : string.Empty) +
          (c.RecommendedEpp >= 0 ? $" · EPP {c.RecommendedEpp}（0x774 项可能被系统覆盖，仅供参考）" : string.Empty) +
          $" · 电压上下文 {c.VoltageContextMv} mV · {c.SavedAt:yyyy-MM-dd HH:mm}" +
          " · 稳定推荐仅覆盖 0x610 的 PL1/PL2/Tau"
        : "标定推荐（L2）：尚无，完成一次两段式标定后生成（只产出通用安全参数）";

    /// <summary>L3 用户编辑值说明：重新标定只更新 L2，绝不覆盖这里。</summary>
    public string ManualLayerText => Settings.Manual is not null
        ? "用户编辑值（L3）已保存；重新标定只更新推荐值，不会覆盖你的手动微调。"
        : "用户编辑值（L3）：尚无；高级参数页应用后才会保存并开机重放。";

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

    private void InitializeEnvironmentCheck()
    {
        try
        {
            bool first = !Settings.EnvironmentChecked && !EnvironmentSelfCheck.IsCommandLineMode;
            // 只读自检：无 UAC、不写任何 MSR，每次启动刷新，保证提权后禁用清单及时解除
            var f = TelemetryFlags;
            _capability = EnvironmentSelfCheck.Run(false, TelemetryHint, f.Temp, f.Power);
            if (first)
            {
                Settings.EnvironmentChecked = true;
                SettingsStore.Save(Settings);
                int hints = _capability.InstallHints.Count;
                AddAlert(hints > 0 ? "warn" : "info", "首次环境自检",
                    (hints > 0
                        ? $"自检完成，发现 {hints} 条外部依赖 / 环境提示，请到「诊断与日志」或高级参数页查看。"
                        : "自检完成，外部依赖齐全。") +
                    $" 能力摘要：{_capability.Summary}；{_capability.DisabledText}");
            }
            N(nameof(CapabilitySummaryText)); N(nameof(DependencyHintText));
            N(nameof(DisabledItemsText)); N(nameof(CapabilityReportText));
            N(nameof(CapabilityOneLineText));
        }
        catch (Exception ex)
        {
            AddAlert("warn", "环境自检未完成", ex.Message + "；参数项禁用判断可能不准，可手动重跑 --env-check。");
        }
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
            _capability = EnvironmentSelfCheck.Run(true, TelemetryHint, f.Temp, f.Power);
            Settings.EnvironmentChecked = true;
            SettingsStore.Save(Settings);
            N(nameof(CapabilitySummaryText)); N(nameof(DependencyHintText));
            N(nameof(DisabledItemsText)); N(nameof(CapabilityReportText));
            N(nameof(CapabilityOneLineText));
            AddAlert("info", "环境自检（含写入探测）", _capability.Summary);
            return _capability.Summary + "；" + _capability.DisabledText;
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
                }
                double epp = h.Epp;
                if (epp != _fwEpp)
                {
                    _fwEpp = epp;
                    N(nameof(FwEppText)); N(nameof(EppEditable)); N(nameof(EppHintText));
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
            N(nameof(CanCalibrate));
            N(nameof(CalibrationBlockedReason));
        }
    }

    private string _powerSource = "未采样";
    public string PowerSource
    {
        get => _powerSource;
        private set { _powerSource = value; N(nameof(PowerSource)); N(nameof(PowerCaption)); }
    }

    public string PowerCaption => _powerFromDriver ? $"包功耗（实测 {_powerSource}）" : "包功耗（估算）";
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
            if (!_tempAvailable) return "缺少温度读数：请以管理员重启并确认 WinRing0 驱动已加载，或检查固件是否屏蔽温度";
            if (!_powerFromDriver) return MsrStatus switch
            {
                MsrStatus.NoAdmin => "缺少实测功耗：普通权限读不到 MSR，请点「以管理员重启」后标定",
                MsrStatus.NotInstalled => "缺少实测功耗：以管理员重启即可加载 WinRing0 驱动并读取 RAPL 功耗",
                MsrStatus.DriverUnavailable => "缺少实测功耗：WinRing0 驱动未加载（检查 HVCI 内存完整性 / 安全软件拦截）",
                MsrStatus.ModuleUnavailable => "缺少实测功耗：当前 LHM 程序集不提供 Ring0 MSR 接口（需 0.9.4）",
                _ => "缺少实测功耗：RAPL 与 LHM 都不可用，估算功耗无法做稳态标定"
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

    public string StatusAction => DriverAvailable ? "查看诊断" : "以管理员重启";

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

    /// <summary>概览页写入链路说明：随授权状态变化，避免把已实现的写入能力说成未来里程碑。</summary>
    public string MonitorHintText => RealWriteEnabled
        ? "写入已授权：决策值经启动备份 / 回读校验后写入 MSR，退出可恢复，开机按已保存的微调值重放。"
        : "只读监控：决策值仅展示，未写入 MSR。写入需管理员 + 设置页「允许写入 MSR」；链路含启动备份 / 回读校验 / 退出恢复 / 开机重放。";

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
            "注意：0x774 项（EPP / Min-Max Perf / Activity Window）可能被系统电源策略覆盖，稳定生效的是 0x610 三项。" +
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
                _telemetry.AttachRapl(_rapl);
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
        var m = Settings.Manual;
        if (m is null || _guard is null) return;
        _manualLock = true;   // 手动微调优先，禁止决策树自动覆盖

        if (!Settings.ReplayOnStartup)
        {
            AddAlert("info", "已保存微调参数未重放", "开机重放已关闭，CPU 当前按固件默认值运转。");
            return;
        }
        if (!RealWriteEnabled)
        {
            AddAlert("warn", "已保存微调参数未生效",
                "开机重放需要管理员 + 设置页「允许写入 MSR」；CPU 当前按固件默认值运转。");
            return;
        }

        var r = _guard.Apply(m.Pl1W, m.Pl2W, m.TauS, m.WriteTimeWindow);
        if (r.Ok)
        {
            _lastAppliedPl1001 = m.Pl1W;
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
        // 手动微调优先：关掉自动接管，避免决策树每拍把用户保存的值覆盖掉
        _manualLock = true;
        Settings.AutoTakeover = false;
        SettingsStore.Save(Settings);
        N(nameof(SavedTuningText)); N(nameof(ManualLayerText));
        AddAlert("info", "微调参数已保存（L3）",
            $"PL1 {pl1:F1}W / PL2 {pl2:F1}W / Tau {tau:F0}s" +
            (epp >= 0 ? $" / EPP {epp}" : "（EPP 本机不可读，未保存）") +
            (maxPerf >= 0 ? $" / MaxPerf {maxPerf}" : string.Empty) +
            (voltageOffsetMv < 0 ? $" / 电压 {voltageOffsetMv}mV（个性化项，需二次确认）" : string.Empty) +
            " 已写入 settings.json；下次开机自动重放到寄存器。自动接管已关闭。" +
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
        Settings.Manual = null;
        _manualLock = false;
        SettingsStore.Save(Settings);
        N(nameof(SavedTuningText));
        AddAlert("info", "已清除保存的微调参数", "下次开机不再重放，CPU 保持固件默认值。");
    }

    public void SetReplayOnStartup(bool on)
    {
        Settings.ReplayOnStartup = on;
        SettingsStore.Save(Settings);
    }

    public void SetAllowRealWrite(bool on)
    {
        Settings.AllowRealWrite = on;
        if (!on) Settings.AutoTakeover = false;
        SettingsStore.Save(Settings);
        N(nameof(RealWriteEnabled));
        N(nameof(MsrSummary));
        N(nameof(MonitorHintText));
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

    /// <summary>写入 EPP（0x774）；机型不支持时返回说明，不影响 PL1/PL2 的写入结果。</summary>
    public MsrWriteResult ApplyEppIfSupported(int epp)
    {
        if (_guard is null || !_guard.EppSupported)
            return new MsrWriteResult(MsrWriteOutcome.Rejected,
                "当前通道读不到 0x774，EPP 微调不可用（PL1/PL2/Tau 不受影响）");
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
            return new MsrWriteResult(MsrWriteOutcome.Rejected, "0x774 不可读，HWP 参数微调不可用");
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
            return new MsrWriteResult(MsrWriteOutcome.Rejected, "0x150 不可读，电压偏移不可用");
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
                "当前通道读不到 0x1AD，睿频倍率调节不可用（PL1/PL2/Tau 不受影响）");
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
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, $"MSR 通道不可用：{MsrDetail}");

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

    /// <summary>自动接管：双开关 + 冷却保护 + 失败熔断（PRD G-08）。</summary>
    private void TryAutoApply(double targetPl1, double targetPl2, double targetTau)
    {
        if (_guard is null) return;
        if (_manualLock) return;   // 已保存手动微调，决策树不接管寄存器
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
        var s = _telemetry.Read();

        LoadPct = double.IsNaN(s.LoadPercent) ? 0 : s.LoadPercent;
        if (!double.IsNaN(s.PowerW)) PowerW = s.PowerW;
        PowerSource = s.Source;
        PowerFromDriver = s.PowerMeasured;
        if (!double.IsNaN(s.MaxCoreGhz)) ClockGhz = s.MaxCoreGhz;

        TempAvailable = !double.IsNaN(s.TempC);
        if (!TempAvailable)
        {
            _noTempTicks++;
            if (_noTempTicks == 3)
                AddAlert("warn", "温度读数不可用",
                    DriverAvailable
                        ? "驱动可用但传感器无读数，请检查是否被虚拟化/固件屏蔽。"
                        : "未检测到硬件访问驱动：请以管理员重启（WinRing0 内核驱动需要管理员权限）。");

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
            : $"标定前馈已生效：决策 PL1 上限 {dec.ThermalCapW:F0}W（天花板 {CeilingC:F0}°C）";
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
            double.IsNaN(FwPl1W) ? $"{dec.Pl1W:F1}W*" : $"{FwPl1W:F1}W",
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

        if (IsCalibrating) StepCalibration();

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
