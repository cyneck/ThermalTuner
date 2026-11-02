namespace ThermalPilot.Core.DecisionTree;

/// <summary>
/// 显式因果决策凭证（Decision Receipt）
/// 彻底白盒化决策过程，记录“由哪条规则决定、为什么这样决定、经历哪些路径”。
/// </summary>
public record DecisionReceipt
{
    public required string TriggeredRuleName { get; init; }
    public required string CausalReason { get; init; }
    public required BindingLimit ActiveLimit { get; init; }
    public required ControllerAction Action { get; init; }
    public required IReadOnlyList<string> EvaluatedPath { get; init; }
    public DateTime DecidedAt { get; init; } = DateTime.UtcNow;
}
