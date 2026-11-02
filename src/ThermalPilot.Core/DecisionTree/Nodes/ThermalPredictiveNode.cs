using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree.Nodes;

/// <summary>
/// 节点 4：热阻稳态预测（Thermal Predictive Feasibility Node）
/// 基于在线估计热阻 R_th，在准备提功耗前预估稳态温度。若预期超出上限，则提前冻结功耗，杜绝过冲震荡。
/// </summary>
public class ThermalPredictiveNode : IDecisionNode
{
    public string NodeName => "ThermalPredictiveFeasibility";

    public DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath)
    {
        auditPath.Add(NodeName);

        // 如果模型有效并且处于准稳态 (|dT/dt| 小于阈值)
        if (snapshot.ThermalModel.IsSolved && Math.Abs(snapshot.DtDt) <= gains.QuasiSteadyDtThreshold)
        {
            double rth = snapshot.ThermalModel.EstimatedRth;
            double potentialNextPL1 = snapshot.CurrentPL1W + gains.StepUpWatts;
            
            // 预测若提升至 potentialNextPL1 时的稳态温度估算:
            // T_steady = T_now + (P_target - P_now) * R_th
            double projectedTempRise = (potentialNextPL1 - snapshot.PackagePowerW) * rth;
            double predictedSteadyTemp = snapshot.CurrentTempC + projectedTempRise;

            if (predictedSteadyTemp > profile.TargetTemperatureCeiling)
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
                    CausalReason = $"[热阻稳态预警] 当前在线热阻为 {rth:F2} K/W，模型预测若升功耗至 {potentialNextPL1:F1}W 将导致稳态温度攀升至 {predictedSteadyTemp:F1}°C (超标)，提前锁定当前功耗",
                    ActiveLimit = BindingLimit.TempCeiling,
                    Action = action,
                    EvaluatedPath = auditPath.ToArray()
                };

                return new DecisionResult(IsMatch: true, Receipt: receipt);
            }
        }

        return new DecisionResult(IsMatch: false);
    }
}
