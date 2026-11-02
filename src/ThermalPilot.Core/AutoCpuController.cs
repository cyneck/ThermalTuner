using ThermalPilot.Core.DecisionTree;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core;

public class AutoCpuController
{
    public const double SafetyCeilingC = 95.0;

    private readonly ThermalResistance _r = new();
    private readonly ThermalDecisionEngine _engine = new();
    private readonly TuningGains _gains = TuningGains.Default;
    private double _pl1 = 35.0;
    private double _currentFan = 50.0;
    private DateTime _last = DateTime.MinValue;

    public ThermalResistance Resistance => _r;
    public bool HasR => _r.Solved;
    public ThermalDecisionEngine Engine => _engine;

    // 最新一次决策的完整因果凭证
    public DecisionReceipt? LatestReceipt { get; private set; }

    public AutoCpuDecision Update(
        double tempC,
        double powerW,
        double ceilingC,
        double ambientC,
        string profile,
        double minW = 10,
        double maxW = 65,
        PerfSample? perf = null,
        double loadPct = double.NaN,
        double dTdt = double.NaN)
    {
        // 1. 构建强类型 HardwareLimits 与 UserProfile
        var hwLimits = new HardwareLimits
        {
            MinPL1 = minW,
            MaxPL1 = maxW,
            MaxSafeCeiling = SafetyCeilingC
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

        // 5. 应用状态更新
        _pl1 = receipt.Action.TargetPL1W;
        _currentFan = receipt.Action.TargetFanPercent;
        _last = DateTime.UtcNow;

        // 6. 返回兼容性 DTO，附带增强因果信息
        return new AutoCpuDecision
        {
            Valid = true,
            Pl1W = receipt.Action.TargetPL1W,
            Pl2W = receipt.Action.TargetPL2W,
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
        _pl1 = 35.0;
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
