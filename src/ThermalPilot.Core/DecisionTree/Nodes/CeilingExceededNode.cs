using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree.Nodes;

/// <summary>
/// 节点 3：温度上限抑制（Ceiling Exceeded Feedback Node）
/// 当瞬时温度超出用户设定的 Profile 温度天花板时，启动温控负反馈，阶梯式下调功耗。
/// </summary>
public class CeilingExceededNode : IDecisionNode
{
    public string NodeName => "CeilingExceededFeedback";

    public DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath)
    {
        auditPath.Add(NodeName);

        if (snapshot.CurrentTempC > profile.TargetTemperatureCeiling)
        {
            double nextPL1 = Math.Max(profile.Hardware.MinPL1, snapshot.CurrentPL1W - gains.StepDownWatts);
            var action = new ControllerAction
            {
                TargetPL1W = nextPL1,
                TargetPL2W = nextPL1 * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = profile.Epp,
                TargetFanPercent = profile.FanMaxPercent, // 顶格风扇进行压制
                Direction = -1,
                ForceImmediateApply = false
            };

            var receipt = new DecisionReceipt
            {
                TriggeredRuleName = NodeName,
                CausalReason = $"[温度上限抑制] 当前温度 ({snapshot.CurrentTempC:F1}°C) 超出 Profile 上限 ({profile.TargetTemperatureCeiling:F1}°C)，阶梯降低 PL1 至 {nextPL1:F1}W",
                ActiveLimit = BindingLimit.ThermalLimit,
                Action = action,
                EvaluatedPath = auditPath.ToArray()
            };

            return new DecisionResult(IsMatch: true, Receipt: receipt);
        }

        return new DecisionResult(IsMatch: false);
    }
}
