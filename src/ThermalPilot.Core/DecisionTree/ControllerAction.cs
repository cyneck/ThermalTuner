namespace ThermalPilot.Core.DecisionTree;

/// <summary>
/// 决策执行动作（Controller Action Payload）
/// 封装经过决策树仲裁后的确切下发量及调节方向。
/// </summary>
public record ControllerAction
{
    public double TargetPL1W { get; init; }
    public double TargetPL2W { get; init; }
    public double TargetTauS { get; init; }
    public int TargetEpp { get; init; }
    public double TargetFanPercent { get; init; }
    public int Direction { get; init; } // -1 = 降能, 0 = 保持, +1 = 提速
    public bool ForceImmediateApply { get; init; }
}
