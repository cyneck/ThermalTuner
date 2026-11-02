namespace ThermalPilot.Core.Taxonomy;

public enum ThermalDynamicRegime
{
    ColdOrIdle,
    TransientHeating,
    QuasiSteadyState,
    Overheating
}

/// <summary>
/// 在线估计模型状态（Identified System State）
/// 运行时在线拟合得出的物理特征量。
/// </summary>
public record IdentifiedThermalState
{
    public double EstimatedRth { get; init; } = double.NaN;
    public bool IsSolved => !double.IsNaN(EstimatedRth);
    public bool IsConfident { get; init; }
    public string QualityDescription { get; init; } = "Uncalibrated";
    public double PredictedSteadyTempC { get; init; }
    public double PowerCapWatts { get; init; }
    public ThermalDynamicRegime Regime { get; init; } = ThermalDynamicRegime.ColdOrIdle;
}
