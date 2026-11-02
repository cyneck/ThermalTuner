namespace ThermalPilot.Core.Taxonomy;

/// <summary>
/// 控制回路算法增益（Tuning Control Gains）
/// 专家级调优参数，提供工程默认收敛参数，避免零散硬编码。
/// </summary>
public record TuningGains
{
    public double StepUpWatts { get; init; } = 3.0;
    public double StepDownWatts { get; init; } = 6.0;
    public double EmergencyStepDownWatts { get; init; } = 15.0;
    public double DeadbandDeltaT { get; init; } = 2.0; // 死区温差
    public double QuasiSteadyDtThreshold { get; init; } = 1.0; // |dT/dt| <= 1.0°C/s 视为准稳态
    public double MinLoadPercentage { get; init; } = 25.0;
    public double MinPackagePowerWatts { get; init; } = 12.0;

    public static TuningGains Default => new();
}
