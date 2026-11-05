using ThermalPilot.Core.DecisionTree;
using ThermalPilot.Core.Environment;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core;

public class AutoCpuController
{
    /// <summary>历史兼容常量。实际安全天花板优先取注入 HardwareLimits.MaxSafeCeiling（= TjMax - 5）。</summary>
    public const double SafetyCeilingC = 95.0;

    private readonly ThermalResistance _r = new();
    private readonly ThermalDecisionEngine _engine = new();
    private readonly TuningGains _gains = TuningGains.Default;
    private readonly HardwareLimits _limits;
    private double _pl1;
    // 决策起点：未注入实读值时是平台区间的演算中点（通用公式），运行时由 App 层用 0x610 实读值覆盖。
    // 不注入时决策树仍可跑，但 HasMeasuredPl1=false，界面不得把它当作「当前寄存器实值」。
    private double _initialPl1;
    // 风扇演算基线：软件不写 EC/PWM，此值只参与决策树内部演算，不代表实测转速。
    private double _currentFan = 50.0;
    private DateTime _last = DateTime.MinValue;
    // 稳态标定基线：标定时/会话轻载的同源 (T, P) 样本，供每拍前馈钳制
    private double _idleTempC = double.NaN;
    private double _idlePowerW = double.NaN;
    private double _thermalCapW = double.NaN;

    public ThermalResistance Resistance => _r;
    public bool HasR => _r.Solved;
    public ThermalDecisionEngine Engine => _engine;
    /// <summary>标定反解的 PL1 安全上限；NaN = 尚未标定或基线无效。</summary>
    public double ThermalCapW => _thermalCapW;

    /// <summary>本控制器绑定的物理硬件约束（只读，由探测器/注入决定）。</summary>
    public HardwareLimits Limits => _limits;

    /// <summary>最新一次决策的完整因果凭证。</summary>
    public DecisionReceipt? LatestReceipt { get; private set; }

    /// <summary>
    /// 默认构造：自动探测当前机器硬件约束（广泛兼容入口）。
    /// 探测失败时内部回退保守 Intel Mobile 基线，不会抛出。
    /// </summary>
    public AutoCpuController() : this(HardwareLimitResolver.ResolveForCurrentMachine())
    {
    }

    /// <summary>注入式构造：由调用方显式指定硬件约束（测试/仿真用）。</summary>
    public AutoCpuController(HardwareLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        // 未注入实读值时，用平台合法区间的中点作为纯演算占位（通用公式，不是任何机型的默认值）。
        // 真实运行时必须在 InitMsr 后调用 SetCurrentPl1W 注入 0x610 实读值；
        // 在此之前 HasMeasuredPl1 = false，界面不得把它当作「当前寄存器实值」。
        _initialPl1 = Math.Clamp((_limits.MinPL1 + _limits.MaxPL1) / 2.0,
                                 _limits.MinPL1, _limits.MaxPL1);
        _pl1 = _initialPl1;
    }

    /// <summary>
    /// 是否已注入 MSR 0x610 实读的默认 PL1。
    /// false 时 _pl1 只是平台区间的演算占位，不代表任何一台机器的真实功率。
    /// </summary>
    public bool HasMeasuredPl1 { get; private set; }

    /// <summary>本机默认稳态功率（0x610 实读 PL1）；未注入时为 NaN。</summary>
    public double MeasuredDefaultPl1W { get; private set; } = double.NaN;

    /// <summary>
    /// 注入 MSR 0x610 实读的当前 PL1，作为决策树首拍与 Reset 的基准。
    /// 通用产品不预设任何机型的瓦数：默认功率以实读为准，读不到就明确标记未注入。
    /// </summary>
    public void SetCurrentPl1W(double pl1W)
    {
        if (double.IsNaN(pl1W) || pl1W <= 0) return;
        MeasuredDefaultPl1W = pl1W;
        HasMeasuredPl1 = true;
        _initialPl1 = Math.Clamp(pl1W, _limits.MinPL1, _limits.MaxPL1);
        _pl1 = _initialPl1;
    }

    public AutoCpuDecision Update(
        double tempC,
        double powerW,
        double ceilingC,
        double ambientC,
        string profile,
        double? minW = null,
        double? maxW = null,
        PerfSample? perf = null,
        double loadPct = double.NaN,
        double dTdt = double.NaN,
        double firmwarePl1W = double.NaN)
    {
        // 1. 构建强类型 HardwareLimits 与 UserProfile（minW/maxW 未显式给出时采用探测到的平台约束）
        var hwLimits = _limits with
        {
            MinPL1 = minW ?? _limits.MinPL1,
            MaxPL1 = maxW ?? _limits.MaxPL1
        };

        var userProfile = UserProfile.FromName(profile, hwLimits) with
        {
            TargetTemperatureCeiling = Math.Min(ceilingC, hwLimits.MaxSafeCeiling)
        };

        // 2. 采样更新在线热阻模型
        if (!double.IsNaN(loadPct))
        {
            _r.TryAdd(powerW, tempC, ambientC, loadPct, out _);
        }

        var identifiedState = new IdentifiedThermalState
        {
            EstimatedRth = _r.Solved ? _r.Value : double.NaN,
            IsConfident = _r.Solved,
            QualityDescription = _r.Solved ? "Solved" : _r.LastReject
        };

        // 3. 构建当前拍只读物理快照
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = tempC,
            AmbientTempC = ambientC,
            PackagePowerW = powerW,
            CurrentPL1W = _pl1,
            FirmwarePL1W = firmwarePl1W,
            CurrentFanPercent = _currentFan,
            LoadPercent = double.IsNaN(loadPct) ? 50.0 : loadPct,
            DtDt = double.IsNaN(dTdt) ? 0.0 : dTdt,
            IsProchotActive = perf?.Prochot == true,
            CoreClockGhz = perf?.CoreClocksGhz ?? 0.0,
            MaxTurboGhz = perf?.MaxTurboGhz ?? 0.0,
            ThermalModel = identifiedState
        };

        // 4. 执行决策树短路求值
        var receipt = _engine.EvaluateTick(snapshot, userProfile, _gains);
        LatestReceipt = receipt;

        // 5. 应用状态更新（输出钳制在硬件边界内）
        //    标定前馈：用稳态模型反解「不越过天花板」的最大 PL1，钳住任何节点的输出。
        //    只在有实测标定值时启用；在线拟合未收敛时不干预，避免拿不可信模型误钳。
        double upper = hwLimits.MaxPL1;
        _thermalCapW = double.NaN;
        if (_r.Calibrated)
        {
            double cap = ThermalCalibration.MaxPowerForCeiling(
                _r.Value, _idleTempC, _idlePowerW, userProfile.TargetTemperatureCeiling);
            if (!double.IsNaN(cap))
            {
                _thermalCapW = Math.Clamp(cap, hwLimits.MinPL1, hwLimits.MaxPL1);
                upper = _thermalCapW;
            }
        }

        _pl1 = Math.Clamp(receipt.Action.TargetPL1W, hwLimits.MinPL1, upper);
        _currentFan = receipt.Action.TargetFanPercent;
        _last = DateTime.UtcNow;

        // 6. 返回兼容性 DTO，附带增强因果信息
        return new AutoCpuDecision
        {
            Valid = true,
            Pl1W = _pl1,
            Pl2W = Math.Clamp(receipt.Action.TargetPL2W, hwLimits.MinPL2, hwLimits.MaxPL2),
            TauS = receipt.Action.TargetTauS,
            Epp = receipt.Action.TargetEpp,
            Reff = _r.Solved ? _r.Value : 0.8,
            ThermalCapW = _thermalCapW,
            RConfident = _r.Solved,
            RQuality = identifiedState.QualityDescription,
            Reason = receipt.CausalReason,
            Limit = receipt.ActiveLimit,
            Direction = receipt.Action.Direction
        };
    }

    /// <summary>设置稳态基线（标定点或会话内轻载稳态点），供每拍前馈钳制与预测共用。</summary>
    public void SetThermalBaseline(double idleTempC, double idlePowerW)
    {
        if (double.IsNaN(idleTempC) || double.IsNaN(idlePowerW) || idlePowerW < 0) return;
        _idleTempC = idleTempC;
        _idlePowerW = idlePowerW;
    }

    public void Reset()
    {
        _pl1 = _initialPl1;
        _r.Reset();
        LatestReceipt = null;
    }

    public void ResetResistance() => _r.Reset();

    /// <summary>写入两段式稳态标定的热阻，作为在线拟合的先验基准。</summary>
    public void ApplyCalibratedRth(double rthKW) => _r.ApplyCalibration(rthKW);

    /// <summary>当前热阻是否来自标定（决策凭证与 UI 需要区分来源）。</summary>
    public bool HasCalibratedR => _r.Calibrated;

    public static double TargetCForProfile(string profile) => UserProfile.FromName(profile).TargetTemperatureCeiling;
    public static double FanMaxForProfile(string profile) => UserProfile.FromName(profile).FanMaxPercent;
    public static int EppForProfile(string profile) => UserProfile.FromName(profile).Epp;
    public static double Pl2Ratio(string profile) => UserProfile.FromName(profile).Pl2Ratio;
    public static double TauForProfile(string profile) => UserProfile.FromName(profile).TauSeconds;
}
