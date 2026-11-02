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

public static class ParamConstraints
{
    public static List<ParamViolation> Validate(double pl1, double pl2, double tau, int epp, double ceiling)
    {
        var violations = new List<ParamViolation>();
        if (pl2 < pl1)
        {
            violations.Add(new ParamViolation
            {
                Key = "PL2",
                Message = "PL2 cannot be less than PL1",
                Severity = ParamSeverity.Block,
                Limit = BindingLimit.Pl2LessPl1
            });
        }
        if (ceiling > 95.0)
        {
            violations.Add(new ParamViolation
            {
                Key = "Ceiling",
                Message = "Temperature ceiling exceeds safe limit",
                Severity = ParamSeverity.Warn,
                Limit = BindingLimit.TempCeiling
            });
        }
        return violations;
    }
}
