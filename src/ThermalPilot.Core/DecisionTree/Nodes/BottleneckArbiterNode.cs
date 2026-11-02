using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree.Nodes;

/// <summary>
/// 节点 5：性能瓶颈仲裁（Bottleneck Arbiter Node）
/// 当系统安全、未超温且热阻允许时，识别系统当前受制于何种物理瓶颈，实现“温度约束下的性能最大化”。
/// </summary>
public class BottleneckArbiterNode : IDecisionNode
{
    public string NodeName => "BottleneckArbiter";

    public DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath)
    {
        auditPath.Add(NodeName);

        var activeLimit = snapshot.DetectActiveLimit(profile.TargetTemperatureCeiling);

        // 场景 A: 功耗墙受限（Power Wall）且拥有温度余量 -> 升功耗以榨取性能
        if (activeLimit == BindingLimit.PowerWall)
        {
            double thermalHeadroom = profile.TargetTemperatureCeiling - snapshot.CurrentTempC;
            if (thermalHeadroom >= gains.DeadbandDeltaT && snapshot.CurrentPL1W < profile.Hardware.MaxPL1)
            {
                double nextPL1 = Math.Min(profile.Hardware.MaxPL1, snapshot.CurrentPL1W + gains.StepUpWatts);
                var action = new ControllerAction
                {
                    TargetPL1W = nextPL1,
                    TargetPL2W = nextPL1 * profile.Pl2Ratio,
                    TargetTauS = profile.TauSeconds,
                    TargetEpp = profile.Epp,
                    TargetFanPercent = Math.Min(profile.FanMaxPercent, snapshot.CurrentFanPercent + 5.0),
                    Direction = 1,
                    ForceImmediateApply = false
                };

                var receipt = new DecisionReceipt
                {
                    TriggeredRuleName = NodeName,
                    CausalReason = $"[功耗墙突破] 系统受限于 PL1 功耗，且尚有 {thermalHeadroom:F1}°C 温度安全余量，执行升功耗至 {nextPL1:F1}W 提升 CPU 运算性能",
                    ActiveLimit = BindingLimit.PowerWall,
                    Action = action,
                    EvaluatedPath = auditPath.ToArray()
                };

                return new DecisionResult(IsMatch: true, Receipt: receipt);
            }
        }

        // 场景 B: 频率达上限（Frequency Cap） -> 保持功耗
        if (activeLimit == BindingLimit.FrequencyCap)
        {
            var action = new ControllerAction
            {
                TargetPL1W = snapshot.CurrentPL1W,
                TargetPL2W = snapshot.CurrentPL1W * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = profile.Epp,
                TargetFanPercent = snapshot.CurrentFanPercent,
                Direction = 0,
                ForceImmediateApply = false
            };

            var receipt = new DecisionReceipt
            {
                TriggeredRuleName = NodeName,
                CausalReason = $"[已达睿频极限] CPU 已达最大睿频时钟 ({snapshot.CoreClockGhz:F2} GHz)，继续追加功耗无法带来性能收益，维持现状以节能降噪",
                ActiveLimit = BindingLimit.FrequencyCap,
                Action = action,
                EvaluatedPath = auditPath.ToArray()
            };

            return new DecisionResult(IsMatch: true, Receipt: receipt);
        }

        // 场景 C: 动态稳态死区（Equilibrium Deadband）
        {
            var action = new ControllerAction
            {
                TargetPL1W = snapshot.CurrentPL1W,
                TargetPL2W = snapshot.CurrentPL1W * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = profile.Epp,
                TargetFanPercent = snapshot.CurrentFanPercent,
                Direction = 0,
                ForceImmediateApply = false
            };

            var receipt = new DecisionReceipt
            {
                TriggeredRuleName = NodeName,
                CausalReason = $"[系统热平衡] 当前处于目标死区范围（温差 {profile.TargetTemperatureCeiling - snapshot.CurrentTempC:F1}°C < {gains.DeadbandDeltaT:F1}°C），维持稳定工作点",
                ActiveLimit = activeLimit,
                Action = action,
                EvaluatedPath = auditPath.ToArray()
            };

            return new DecisionResult(IsMatch: true, Receipt: receipt);
        }
    }
}
