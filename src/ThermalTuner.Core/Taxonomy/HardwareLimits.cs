namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// 硬件物理边界与硬约束（Hardware Physical Limits）
/// 由检测到的硬件型号/平台（如 Intel CoffeeLake/KabyLake）确定，只读注入。
/// </summary>
public record HardwareLimits
{
    public double TjMax { get; init; } = 100.0;
    public double MinPL1 { get; init; } = 10.0;
    public double MaxPL1 { get; init; } = 65.0;
    public double MinPL2 { get; init; } = 15.0;
    public double MaxPL2 { get; init; } = 90.0;
    public double MaxSafeCeiling { get; init; } = 95.0;
    public IReadOnlySet<double> LegalTauSeconds { get; init; } = new HashSet<double> { 1, 2, 4, 8, 16, 28, 32, 64 };

    public static HardwareLimits DefaultIntelMobile => new()
    {
        TjMax = 100.0,
        MinPL1 = 10.0,
        MaxPL1 = 65.0,
        MinPL2 = 15.0,
        MaxPL2 = 90.0,
        MaxSafeCeiling = 95.0
    };

    public bool IsTauLegal(double tau) => LegalTauSeconds.Contains(tau);
}
