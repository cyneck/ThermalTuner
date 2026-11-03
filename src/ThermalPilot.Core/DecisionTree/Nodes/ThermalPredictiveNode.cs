using ThermalPilot.Core;
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

            // 预测若提升至 potentialNextPL1 时的稳态温度估算。
            // ΔP 用「PL1 设定值之差」而非「功率传感器读数之差」：
            // 包功耗读数存在 1~3 秒滞后，用它会把滞后误当成热阻，产生 120°C 这类假预警。
            double deltaPl1 = potentialNextPL1 - snapshot.CurrentPL1W;
            // 与标定报告、界面预测卡片共用唯一入口，禁止各自用瞬态温度另算一套
            double predictedSteadyTemp = ThermalCalibration.PredictSteady(
                rth, snapshot.CurrentTempC, snapshot.CurrentPL1W, potentialNextPL1);


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
                    CausalReason = $"[热阻稳态预警] 当前在线热阻 {rth:F2} K/W，若 PL1 由 {snapshot.CurrentPL1W:F0}W 提至 {potentialNextPL1:F0}W" +
                                   $"（+{deltaPl1:F0}W）预计稳态温度 {predictedSteadyTemp:F1}°C，超出天花板 {profile.TargetTemperatureCeiling:F0}°C，提前锁定当前功耗",
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
