using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core;

public enum ParamTier
{
    Global = 0,
    Advanced = 1,
    Preview = 2
}

public enum ParamSeverity
{
    Block = 0,
    Warn = 1,
    Info = 2
}

public enum BindingLimit
{
    Unknown,
    Pl1Range,
    Pl2Range,
    Pl2LessPl1,
    TempCeiling,
    ThermalLimit,
    EppRange,
    FanSpeed,
    AmbientRange,
    TargetC,
    PowerWall,
    FrequencyCap,
    LightLoad
}

public class ParamSpec
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public ParamTier Tier { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double[]? Allowed { get; set; }
    public string Why { get; set; } = string.Empty;
}

public class ParamViolation
{
    public string Key { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ParamSeverity Severity { get; set; }
    public BindingLimit Limit { get; set; }
}

/// <summary>
/// 参数约束校验器（升级版）
/// 基于四象限参数分类学（HardwareLimits 与 UserProfile），提供物理防呆与业务约束校验。
/// </summary>
public static class ParamConstraints
{
    public static List<ParamViolation> Validate(double pl1, double pl2, double tau, int epp, double ceiling, HardwareLimits? hw = null)
    {
        hw ??= HardwareLimits.DefaultIntelMobile;
        var violations = new List<ParamViolation>();

        // 1. 物理硬约束 (Invariants)
        if (pl2 < pl1)
        {
            violations.Add(new ParamViolation
            {
                Key = "PL2",
                Message = $"PL2 ({pl2:F1}W) 不能小于持续功耗限制 PL1 ({pl1:F1}W)",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.Pl2LessPl1
            });
        }

        if (pl1 < hw.MinPL1 || pl1 > hw.MaxPL1)
        {
            violations.Add(new ParamViolation
            {
                Key = "PL1",
                Message = $"PL1 超出硬件允许范围 [{hw.MinPL1}W, {hw.MaxPL1}W]",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.Pl1Range
            });
        }

        if (pl2 < hw.MinPL2 || pl2 > hw.MaxPL2)
        {
            violations.Add(new ParamViolation
            {
                Key = "PL2",
                Message = $"PL2 超出硬件允许范围 [{hw.MinPL2}W, {hw.MaxPL2}W]",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.Pl2Range
            });
        }

        if (!hw.IsTauLegal(tau))
        {
            violations.Add(new ParamViolation
            {
                Key = "Tau",
                Message = $"时间常数 Tau ({tau}s) 不是合法的硬件 MSR 编码步长",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.Unknown
            });
        }

        // 2. 安全与体验警告约束
        if (ceiling > hw.MaxSafeCeiling)
        {
            violations.Add(new ParamViolation
            {
                Key = "Ceiling",
                Message = $"目标温度天花板 ({ceiling:F1}°C) 超出最高安全建议值 ({hw.MaxSafeCeiling:F1}°C)，有频繁触发硬件热保护风险",
                Severity = ParamSeverity.Warn,
                Limit = BindingLimit.TempCeiling
            });
        }

        if (epp < 0 || epp > 255)
        {
            violations.Add(new ParamViolation
            {
                Key = "EPP",
                Message = "EPP 能量性能偏好必须在 [0, 255] 之间",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.EppRange
            });
        }

        return violations;
    }
}
