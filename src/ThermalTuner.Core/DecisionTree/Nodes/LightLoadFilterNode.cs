using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.DecisionTree.Nodes;

/// <summary>
/// 节点 2：轻载/空闲过滤器（Light Load Filter Node）
/// 过滤 CPU 空闲或轻度打字/浏览状态，避免扰动 PL 设定值，保持静音与稳定性。
/// </summary>
public class LightLoadFilterNode : IDecisionNode
{
    public string NodeName => "LightLoadFilter";

    public DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath)
    {
        auditPath.Add(NodeName);

        if (snapshot.LoadPercent < gains.MinLoadPercentage || snapshot.PackagePowerW < gains.MinPackagePowerWatts)
        {
            // 「保持当前设置」必须锚定固件实读值：snapshot.CurrentPL1W 是控制器上一拍的
            // 决策目标，若曾在高负载被推到平台上限，轻载时保持它会形成只升不降的棘轮，
            // 授权写入后甚至会在 7% 负载下把 PL1 从固件默认 45W 抬到 65W。
            // 读不到固件值时才回退到决策值（兼容测试与无驱动环境）。
            bool hasFw = !double.IsNaN(snapshot.FirmwarePL1W) && snapshot.FirmwarePL1W > 0;
            double holdPl1 = hasFw ? snapshot.FirmwarePL1W : snapshot.CurrentPL1W;

            var action = new ControllerAction
            {
                TargetPL1W = holdPl1,
                TargetPL2W = holdPl1 * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = profile.Epp,
                TargetFanPercent = Math.Min(snapshot.CurrentFanPercent, profile.FanMaxPercent),
                Direction = 0,
                ForceImmediateApply = false
            };

            var receipt = new DecisionReceipt
            {
                TriggeredRuleName = NodeName,
                CausalReason = $"[低载维持] 当前负载 ({snapshot.LoadPercent:F1}%) 与包功耗 ({snapshot.PackagePowerW:F1}W) 处于基线轻载区，保持当前设置避免频繁步进",
                ActiveLimit = BindingLimit.LightLoad,
                Action = action,
                EvaluatedPath = auditPath.ToArray()
            };

            return new DecisionResult(IsMatch: true, Receipt: receipt);
        }

        return new DecisionResult(IsMatch: false);
    }
}
