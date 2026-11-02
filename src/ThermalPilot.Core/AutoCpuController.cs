namespace ThermalPilot.Core;

public class AutoCpuDecision
{
    public bool Valid { get; set; }
    public double Pl1W { get; set; }
    public double Pl2W { get; set; }
    public double TauS { get; set; }
    public int Epp { get; set; }
    public double Reff { get; set; }
    public double PredictedC { get; set; }
    public double PredictedSteadyC { get; set; }
    public bool RConfident { get; set; }
    public string RQuality { get; set; } = "Low";
    public double PowerCapW { get; set; }
    public string Reason { get; set; } = string.Empty;
    public BindingLimit Limit { get; set; }
    public int Direction { get; set; } // -1, 0, +1
}

public class PerfSample
{
    public double CoreClocksGhz { get; set; }
    public double MaxTurboGhz { get; set; }
    public bool Prochot { get; set; }
}

public class AutoCpuController
{
    public const double SafetyCeilingC = 95.0;

    private readonly ThermalResistance _r = new();
    private double _pl1 = 35.0;
    private DateTime _last = DateTime.MinValue;

    public ThermalResistance Resistance => _r;
    public bool HasR => _r.Solved;

    public AutoCpuDecision Update(
        double tempC,
        double powerW,
        double ceilingC,
        double ambientC,
        string profile,
        double minW = 10,
        double maxW = 65,
        PerfSample? perf = null,
        double loadPct = double.NaN,
        double dTdt = double.NaN)
    {
        double actualCeiling = Math.Min(ceilingC, SafetyCeilingC);
        double tau = TauForProfile(profile);
        int epp = EppForProfile(profile);
        double ratio = Pl2Ratio(profile);

        // Update thermal resistance estimator
        if (!double.IsNaN(loadPct))
        {
            _r.TryAdd(powerW, tempC, ambientC, loadPct, out _);
        }

        var dec = new AutoCpuDecision
        {
            Valid = true,
            Reff = _r.Solved ? _r.Value : 0.8,
            RConfident = _r.Solved,
            TauS = tau,
            Epp = epp
        };

        // Monolithic if-else logic
        if (perf?.Prochot == true || tempC >= actualCeiling)
        {
            _pl1 = Math.Max(minW, _pl1 - 6.0);
            dec.Limit = BindingLimit.ThermalLimit;
            dec.Direction = -1;
            dec.Reason = "Temperature reached ceiling, throttle power down";
        }
        else if (loadPct < 20.0 || powerW < 12.0)
        {
            dec.Limit = BindingLimit.LightLoad;
            dec.Direction = 0;
            dec.Reason = "Load is light, hold power";
        }
        else if (perf != null && perf.CoreClocksGhz >= perf.MaxTurboGhz * 0.98)
        {
            dec.Limit = BindingLimit.FrequencyCap;
            dec.Direction = 0;
            dec.Reason = "Max frequency achieved, no need to boost power";
        }
        else if (actualCeiling - tempC > 2.0 && _pl1 < maxW)
        {
            _pl1 = Math.Min(maxW, _pl1 + 3.0);
            dec.Limit = BindingLimit.PowerWall;
            dec.Direction = 1;
            dec.Reason = "Thermal headroom available, increase power";
        }
        else
        {
            dec.Limit = BindingLimit.Unknown;
            dec.Direction = 0;
            dec.Reason = "Equilibrium maintained";
        }

        dec.Pl1W = _pl1;
        dec.Pl2W = _pl1 * ratio;
        dec.PredictedSteadyC = tempC + ((dec.Pl1W - powerW) * dec.Reff);

        return dec;
    }

    public void Reset()
    {
        _pl1 = 35.0;
        _r.Reset();
    }

    public void ResetResistance() => _r.Reset();

    public static double TargetCForProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "silent" => 85.0,
        "performance" => 92.0,
        _ => 88.0
    };

    public static double FanMaxForProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "silent" => 55.0,
        "performance" => 100.0,
        _ => 75.0
    };

    public static int EppForProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "silent" => 200,
        "performance" => 0,
        _ => 128
    };

    public static double Pl2Ratio(string profile) => profile.ToLowerInvariant() switch
    {
        "silent" => 1.15,
        "performance" => 1.35,
        _ => 1.25
    };

    public static double TauForProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "silent" => 8.0,
        "performance" => 32.0,
        _ => 16.0
    };
}
