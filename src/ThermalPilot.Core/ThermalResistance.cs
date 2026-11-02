namespace ThermalPilot.Core;

public class ThermalResistance
{
    public const double MinKperW = 0.2;
    public const double MaxKperW = 3.0;
    public const double MinSpanW = 8.0;
    public const double MinRiseC = 4.0;

    public double Value { get; private set; } = double.NaN;
    public bool Solved => !double.IsNaN(Value);
    public double PowerSpan { get; private set; }
    public double TempSpan { get; private set; }
    public string LastReject { get; private set; } = string.Empty;
    public int Rejected { get; private set; }

    private readonly List<(double P, double T, double Amb, double Load)> _pts = new();

    public bool TryAdd(double powerW, double tempC, double ambientC, double loadPct, out string why)
    {
        if (loadPct < 30.0 || powerW < 10.0)
        {
            why = "Load/Power below threshold";
            LastReject = why;
            Rejected++;
            return false;
        }

        _pts.Add((powerW, tempC, ambientC, loadPct));
        if (_pts.Count > 60) _pts.RemoveAt(0);

        if (_pts.Count >= 10)
        {
            double minP = _pts.Min(p => p.P);
            double maxP = _pts.Max(p => p.P);
            double minT = _pts.Min(p => p.T);
            double maxT = _pts.Max(p => p.T);

            PowerSpan = maxP - minP;
            TempSpan = maxT - minT;

            if (PowerSpan >= MinSpanW && TempSpan >= MinRiseC)
            {
                // Simple regression slope deltaT / deltaP
                double slope = TempSpan / Math.Max(1.0, PowerSpan);
                if (slope >= MinKperW && slope <= MaxKperW)
                {
                    Value = slope;
                    why = "Fitted successfully";
                    return true;
                }
            }
        }

        why = "Insufficient span";
        LastReject = why;
        return false;
    }

    public void Reset()
    {
        _pts.Clear();
        Value = double.NaN;
        PowerSpan = 0;
        TempSpan = 0;
        Rejected = 0;
        LastReject = string.Empty;
    }
}
