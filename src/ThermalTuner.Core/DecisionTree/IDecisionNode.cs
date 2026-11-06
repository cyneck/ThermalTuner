using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.DecisionTree;

public record DecisionResult(
    bool IsMatch,
    DecisionReceipt? Receipt = null
);

/// <summary>
/// 决策树节点接口（IDecisionNode）
/// 遵循单一职责，每个节点仅负责特定的物理边界或控制逻辑判定。
/// </summary>
public interface IDecisionNode
{
    string NodeName { get; }
    
    DecisionResult Evaluate(
        ThermalSnapshot snapshot,
        UserProfile profile,
        TuningGains gains,
        List<string> auditPath);
}
