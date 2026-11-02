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
    private double _currentFan = 50.0;
    private DateTime _last = DateTime.MinValue;

    public ThermalResistance Resistance => _r;
    public bool HasR => _r.Solved;
    public ThermalDecisionEngine Engine => _engine;

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
        // 初始 PL1 钳制在硬件合法区间中位，避免越界
        _pl1 = Math.Clamp(35.0, _limits.MinPL1, _limits.MaxPL1);
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
        double dTdt = double.NaN)
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
            QualityDescription = _r.Solved ? "Solved" : _r.LastReject,
            PredictedSteadyTempC = _r.Solved ? tempC + ((_pl1 - powerW) * _r.Value) : tempC
        };

        // 3. 构建当前拍只读物理快照
        var snapshot = new ThermalSnapshot
        {
            CurrentTempC = tempC,
            AmbientTempC = ambientC,
            PackagePowerW = powerW,
            CurrentPL1W = _pl1,
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

        // 5. 应用状态更新（输出钳制在硬件物理边界内，防止任何节点越界下发）
        _pl1 = Math.Clamp(receipt.Action.TargetPL1W, hwLimits.MinPL1, hwLimits.MaxPL1);
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
            RConfident = _r.Solved,
            RQuality = identifiedState.QualityDescription,
            PredictedSteadyC = identifiedState.PredictedSteadyTempC,
            Reason = receipt.CausalReason,
            Limit = receipt.ActiveLimit,
            Direction = receipt.Action.Direction
        };
    }

    public void Reset()
    {
        _pl1 = Math.Clamp(35.0, _limits.MinPL1, _limits.MaxPL1);
        _r.Reset();
        LatestReceipt = null;
    }

    public void ResetResistance() => _r.Reset();

    public static double TargetCForProfile(string profile) => UserProfile.FromName(profile).TargetTemperatureCeiling;
    public static double FanMaxForProfile(string profile) => UserProfile.FromName(profile).FanMaxPercent;
    public static int EppForProfile(string profile) => UserProfile.FromName(profile).Epp;
    public static double Pl2Ratio(string profile) => UserProfile.FromName(profile).Pl2Ratio;
    public static double TauForProfile(string profile) => UserProfile.FromName(profile).TauSeconds;
}
