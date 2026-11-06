namespace ThermalTuner.Core;

/// <summary>
/// 在线热阻估计（R_th = ΔT / ΔP）。
/// 最小二乘拟合功率-温度斜率，并要求：
///   · 功率跨度 ≥ 12W（跨度过小时斜率会被量化噪声放大）
///   · 拟合优度 R² ≥ 0.5（避免把负载抖动当成热响应）
///   · 斜率落在物理可用区间 [0.2, 2.0] K/W
/// 任一条件不满足即放弃本拍估计：宁可"没有模型"，也不用错模型做预测。
/// </summary>
public class ThermalResistance
{
    public const double MinKperW = 0.2;
    public const double MaxKperW = 2.0;
    public const double MinSpanW = 12.0;
    public const double MinRiseC = 4.0;
    public const double MinRSquared = 0.5;

    public double Value { get; private set; } = double.NaN;
    public bool Solved => !double.IsNaN(Value);

    /// <summary>当前值是否来自两段式稳态标定（比在线拟合更可信，权重更高）。</summary>
    public bool Calibrated { get; private set; }

    /// <summary>标定得到的值（未标定则为 NaN），供 UI 区分来源。</summary>
    public double CalibratedValue { get; private set; } = double.NaN;
    public double PowerSpan { get; private set; }
    public double TempSpan { get; private set; }
    public double RSquared { get; private set; }
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

        if (_pts.Count < 10)
        {
            why = "Insufficient samples";
            LastReject = why;
            return false;
        }

        double minP = _pts.Min(p => p.P);
        double maxP = _pts.Max(p => p.P);
        double minT = _pts.Min(p => p.T);
        double maxT = _pts.Max(p => p.T);

        PowerSpan = maxP - minP;
        TempSpan = maxT - minT;

        if (PowerSpan < MinSpanW || TempSpan < MinRiseC)
        {
            why = "Insufficient span";
            LastReject = why;
            return false;
        }

        // 最小二乘：T = a + R·P
        int n = _pts.Count;
        double meanP = _pts.Average(p => p.P);
        double meanT = _pts.Average(p => p.T);
        double sxx = 0, sxy = 0, sst = 0;
        foreach (var (p, t, _, _) in _pts)
        {
            double dp = p - meanP, dt = t - meanT;
            sxx += dp * dp;
            sxy += dp * dt;
            sst += dt * dt;
        }

        if (sxx <= 1e-6 || sst <= 1e-6)
        {
            why = "Degenerate data";
            LastReject = why;
            return false;
        }

        double slope = sxy / sxx;
        double intercept = meanT - slope * meanP;

        // 拟合优度
        double ssres = 0;
        foreach (var (p, t, _, _) in _pts)
        {
            double pred = intercept + slope * p;
            ssres += (t - pred) * (t - pred);
        }
        RSquared = 1.0 - ssres / sst;

        if (slope < MinKperW || slope > MaxKperW)
        {
            why = $"R out of range ({slope:F2} K/W)";
            LastReject = why;
            Rejected++;
            return false;
        }

        if (RSquared < MinRSquared)
        {
            why = $"Poor fit (R²={RSquared:F2})";
            LastReject = why;
            Rejected++;
            return false;
        }

        // 指数平滑，避免单窗口跳变；
        // 已标定时在线拟合只做「带内微调」：允许 ±15% 漂移（灰尘、风扇曲线变化），
        // 但不允许连续窗口把稳态标定整体拖回瞬态水平。
        if (Calibrated)
        {
            double refined = Value * 0.85 + slope * 0.15;
            double band = CalibratedValue * 0.15;
            Value = Math.Clamp(refined, CalibratedValue - band, CalibratedValue + band);
        }
        else
        {
            Value = Solved ? Value * 0.7 + slope * 0.3 : slope;
        }
        why = "Fitted successfully";
        return true;
    }

    /// <summary>
    /// 写入两段式稳态标定结果：清空在线拟合历史，直接采用实测值。
    /// 之后仍允许在线拟合微调（权重 0.15），但不会推翻标定。
    /// </summary>
    public void ApplyCalibration(double rthKW)
    {
        if (double.IsNaN(rthKW) || rthKW <= 0) return;

        _pts.Clear();
        PowerSpan = 0;
        TempSpan = 0;
        RSquared = 0;
        Rejected = 0;
        LastReject = "Calibrated";

        CalibratedValue = Math.Clamp(rthKW, ThermalCalibration.MinPlausibleRth, ThermalCalibration.MaxPlausibleRth);
        Value = CalibratedValue;
        Calibrated = true;
    }

    public void Reset()
    {
        _pts.Clear();
        Value = double.NaN;
        PowerSpan = 0;
        TempSpan = 0;
        RSquared = 0;
        Rejected = 0;
        LastReject = string.Empty;
        Calibrated = false;
        CalibratedValue = double.NaN;
    }
}
