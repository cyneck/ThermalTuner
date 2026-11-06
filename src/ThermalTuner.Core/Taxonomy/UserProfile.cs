namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// 用户意图配置文件（User Intent Profile）
/// 封装用户对性能、温度与噪音的权衡偏好，屏蔽底层寄存器细节。
/// </summary>
public record UserProfile
{
    public string Name { get; init; } = "balanced";
    public double TargetTemperatureCeiling { get; init; } = 88.0;
    public double FanMaxPercent { get; init; } = 75.0;
    public int Epp { get; init; } = 128;
    public double Pl2Ratio { get; init; } = 1.25;
    public double TauSeconds { get; init; } = 16.0;
    public HardwareLimits Hardware { get; init; } = HardwareLimits.DefaultIntelMobile;

    public static UserProfile Silent(HardwareLimits? hw = null) => new()
    {
        Name = "silent",
        TargetTemperatureCeiling = 85.0,
        FanMaxPercent = 55.0,
        Epp = 200,
        Pl2Ratio = 1.15,
        TauSeconds = 8.0,
        Hardware = hw ?? HardwareLimits.DefaultIntelMobile
    };

    public static UserProfile Balanced(HardwareLimits? hw = null) => new()
    {
        Name = "balanced",
        TargetTemperatureCeiling = 88.0,
        FanMaxPercent = 75.0,
        Epp = 128,
        Pl2Ratio = 1.25,
        TauSeconds = 16.0,
        Hardware = hw ?? HardwareLimits.DefaultIntelMobile
    };

    public static UserProfile Performance(HardwareLimits? hw = null) => new()
    {
        Name = "performance",
        TargetTemperatureCeiling = 92.0,
        FanMaxPercent = 100.0,
        Epp = 0,
        Pl2Ratio = 1.35,
        TauSeconds = 32.0,
        Hardware = hw ?? HardwareLimits.DefaultIntelMobile
    };

    public static UserProfile FromName(string profileName, HardwareLimits? hw = null) => profileName.ToLowerInvariant() switch
    {
        "silent" => Silent(hw),
        "performance" => Performance(hw),
        _ => Balanced(hw)
    };
}
