using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree.Nodes;

/// <summary>
/// 节点 1：安全看门狗（Safety Watchdog Node）
/// 具备最高优先级。检测硬件 PROCHOT 信号或芯片临界温度，执行紧急降温降能。
/// </summary>
public class SafetyWatchdogNode : IDecisionNode
{
    public string NodeName => "SafetyWatchdog";

    public DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath)
    {
        auditPath.Add(NodeName);

        double criticalSafetyTemp = profile.Hardware.TjMax - 3.0; // 离 TjMax 仅差 3°C 时强制熔断

        if (snapshot.IsProchotActive || snapshot.CurrentTempC >= criticalSafetyTemp)
        {
            double safePL1 = Math.Max(profile.Hardware.MinPL1, snapshot.CurrentPL1W - gains.EmergencyStepDownWatts);
            var action = new ControllerAction
            {
                TargetPL1W = safePL1,
                TargetPL2W = safePL1 * profile.Pl2Ratio,
                TargetTauS = profile.TauSeconds,
                TargetEpp = Math.Max(profile.Epp, 128),
                TargetFanPercent = 100.0, // 强制全速散热
                Direction = -1,
                ForceImmediateApply = true
            };

            var receipt = new DecisionReceipt
            {
                TriggeredRuleName = NodeName,
                CausalReason = $"[硬件安全熔断] 检测到 PROCHOT 告警({snapshot.IsProchotActive}) 或 温度 ({snapshot.CurrentTempC:F1}°C) 逼近 TjMax 临界红线，执行紧急下调功耗至 {safePL1:F1}W 并拉满风扇",
                ActiveLimit = BindingLimit.ThermalLimit,
                Action = action,
                EvaluatedPath = auditPath.ToArray()
            };

            return new DecisionResult(IsMatch: true, Receipt: receipt);
        }

        return new DecisionResult(IsMatch: false);
    }
}
