using System;
using System.Collections.Generic;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>
/// 在线热阻估计（K/W）—— 只回答一个问题：**再给 1 W，稳态温度会升多少**。
///
/// 为什么不能用 <c>R = (T − T_amb) / P</c>：
///   这个式子在**满载**时才近似成立。轻载时 T 里有一大块固定偏置（die 与封装传感器的
///   温差、uncore/核显的耗散、机箱内回流），P 又很小，除出来会严重偏大。
///   本机实测（相变片已换）：
///       空闲  P=6.1W  T=40.8℃  → (40.8−24)/6.1 = 2.76 K/W   ← 假的
///       满载  P=41W   T=77.8℃  → (77.8−24)/41  = 1.31 K/W   ← 真的
///   旧版把这个数当有效热阻，还在每一拍更新（EMA 0.9），一分钟就被空闲样本带跑，
///   于是"R≈2.67 / 预测 238℃"这种数字就出现了，而机器实际 40W 只有 78℃。
///
/// 正确做法：**用功率变化引起的温升来拟合斜率**，截距（室温+偏置）不参与。
///     T = a + R·P   →   R = ΔT/ΔP   （最小二乘，窗口内多点）
/// 这样即使 a 有 4℃ 的固定偏置，R 依然准。
///
/// 采样门（任何一条不满足就不吃这个样本）：
///   · 负载 ≥ 35%、包功率 ≥ 12W —— 否则固定偏置占比过高；
///   · |dT/dt| ≤ 1.5℃/s —— 还在过渡过程里，T 不代表这个 P 的稳态；
///   · T 高于室温 8℃ 以上 —— 避免冷机；
///   · 功率跨度 ≥ 4W 才解斜率，跨度不够时保留上一次的值（并说明"在等功率变化"）。
/// </summary>
public sealed class ThermalResistance
{
    /// <summary>
    /// 物理边界。下限取 0.45：这已经是液金+均热板的超薄本水平，再低说明样本没激励；
    /// 上限 2.2 覆盖"硅脂干涸/压装不实"的断链机（本机换片前 ~1.8）。
    /// </summary>
    public const double MinKperW = 0.45;
    public const double MaxKperW = 2.2;

    private readonly List<(double P, double T)> _pts = new();

    public int Capacity { get; init; } = 60;
    public double Value { get; private set; } = double.NaN;
    public int Samples => _pts.Count;
    public double PowerSpan { get; private set; }
    public double TempSpan { get; private set; }
    /// <summary>
    /// 样本数 ≥6 且功率跨度 ≥6W 才叫"可信"。
    /// 判据的重心是**功率跨度**而不是点数：斜率只需要"功率确实变过"，
    /// 同功率下采 100 个点对 ΔT/ΔP 毫无帮助（这也是为什么 20 分钟恒载标定解不出好 R）。
    /// </summary>
    public bool Confident => Solved && Samples >= 6 && PowerSpan >= 6.0;

    /// <summary>是否真的解过斜率（false = 只有绝对式粗估，用于区分「可信」与「粗估」）。</summary>
    public bool Solved { get; private set; }

    /// <summary>最近一次被拒绝的原因（给 UI 解释"为什么还没有热阻"）。</summary>
    public string LastReject { get; private set; } = "";
    public int Rejected { get; private set; }

    public string Quality
    {
        get
        {
            if (double.IsNaN(Value)) return Samples == 0 ? "未估计" : "样本不足";
            string core = $"{Value:F2} K/W";
            if (!Solved) return core + "（粗估：未观察到功率变化）";
            if (!Confident) return core + $"（{Samples} 点 · ΔP {PowerSpan:F0}W，仅供参考）";
            return core + $"（{Samples} 点 · ΔP {PowerSpan:F0}W）";
        }
    }

    /// <summary>喂一个样本。返回 true 表示这次更新了估计值。</summary>
    public bool TryAdd(double powerW, double tempC, double loadPct, double ambientC, double dTdt,
                       out string why)
    {
        why = "";
        // NaN 视为"调用方没有这个信息"（离线自检/手动调用），不做拦截
        if (!double.IsNaN(powerW))
        {
            if (powerW < 12) { Reject(why = $"功率 {powerW:F1}W < 12W，固定偏置占比过高"); return false; }
            if (powerW > 250) { Reject(why = $"功率 {powerW:F0}W 超出量程"); return false; }
        }
        if (!double.IsNaN(loadPct) && loadPct < 35)
        {
            Reject(why = $"负载 {loadPct:F0}% < 35%");
            return false;
        }
        if (!double.IsNaN(ambientC) && !double.IsNaN(tempC) && tempC < ambientC + 8)
        {
            Reject(why = $"温度只比室温高 {tempC - ambientC:F1}℃");
            return false;
        }
        if (!double.IsNaN(dTdt) && Math.Abs(dTdt) > 0.8)
        {
            Reject(why = $"温度还在变（{dTdt:+0.0;-0.0}℃/s），不是稳态");
            return false;
        }

        _pts.Add((powerW, tempC));
        while (_pts.Count > Capacity) _pts.RemoveAt(0);

        double minP = _pts.Min(p => p.P), maxP = _pts.Max(p => p.P);
        double minT = _pts.Min(p => p.T), maxT = _pts.Max(p => p.T);
        PowerSpan = maxP - minP;
        TempSpan = maxT - minT;

        // 解斜率需要**激励**：功率要真的变过、温度也要跟着动。
        // 只要求 ΔP 不要求 ΔT 的话，恒载状态下温度缓慢漂移（78→80℃）会被解成 0.2 K/W 这种假值，
        // 而 0.2 意味着"再加 50W 只升 10℃"，正好把功率放到危险区。
        const double MinSpanW = 8.0;      // 至少 8W 的功率变化
        const double MinRiseC = 4.0;      // 对应的温升至少 4℃（分辨得出来）
        if (_pts.Count < 4 || PowerSpan < MinSpanW || TempSpan < MinRiseC)
        {
            why = PowerSpan < MinSpanW
                ? $"功率跨度仅 {PowerSpan:F1}W（需 ≥{MinSpanW:F0}W），等负载变化后再解斜率"
                : $"温升仅 {TempSpan:F1}℃（需 ≥{MinRiseC:F0}℃），无法与噪声区分";
            Reject(why);

            // 兜底：机器长期恒载（比如只玩游戏、功率一直是 40W）时永远等不到激励。
            // 退一步用「固定偏置」的绝对式：R ≈ (T − 室温 − 偏置) / P。
            // 偏置取 5℃（本机实测 die/封装/uncore 合计 3.6~5℃），只在 P≥25W、负载≥55% 时用，
            // 并标记为"粗估"—— 一旦出现功率变化，回归值会覆盖它。
            // 粗估要持续跟踪，不能"第一次算完就冻住"：趋势是冷机→热机，第一次（最冷）会低估 20%+。
            if (powerW >= 25 && (double.IsNaN(loadPct) || loadPct >= 55))
            {
                double cand = Clamp((tempC - ((double.IsNaN(ambientC) ? 24 : ambientC) + 5.0)) / powerW);
                if (double.IsNaN(Value) || (cand >= Value * 0.8 && cand <= Value * 1.25))
                {
                    Value = cand;
                    why = "粗估（未观察到功率变化，偏置按 5℃）";
                    return true;
                }
            }
            return false;
        }

        // 最小二乘：T = a + R·P
        double mx = _pts.Average(p => p.P), my = _pts.Average(p => p.T);
        double sxx = _pts.Sum(p => (p.P - mx) * (p.P - mx));
        double sxy = _pts.Sum(p => (p.P - mx) * (p.T - my));
        if (sxx < 1e-6) { why = "功率没有变化，无法解斜率"; return false; }

        double slope = sxy / sxx;
        if (slope <= 0) { why = $"斜率为负（{slope:F2}），样本受负载迁移污染"; return false; }
        if (slope < MinKperW || slope > MaxKperW)
        {
            why = $"斜率 {slope:F2} K/W 超出物理范围 {MinKperW}~{MaxKperW}";
            return false;
        }

        // 斜率需要与"温度确实跟着功率走"一致：温度跨度要能覆盖 ΔP 引起的温升
        if (TempSpan < slope * PowerSpan * 0.5)
        {
            why = $"ΔT {TempSpan:F1}℃ 与 ΔP×R={slope * PowerSpan:F1}℃ 不一致，样本受时间漂移污染";
            Reject(why);
            return false;
        }

        // 单次变化限幅 ±20%：冷却阶段的第一批样本（功率掉了、散热器还热着）会把斜率解小，
        // 那种点会让控制器误以为"还能再加 50W"，必须挡住。
        if (!double.IsNaN(Value) && (slope < Value * 0.8 || slope > Value * 1.25))
        {
            why = $"斜率跳变 {Value:F2}→{slope:F2} K/W（单次限幅 ±20%）";
            Reject(why);
            return false;
        }

        Value = slope;
        Solved = true;
        why = "";
        return true;
    }

    private static double Clamp(double v) => Math.Clamp(v, MinKperW, MaxKperW);

    private void Reject(string why)
    {
        LastReject = why;
        Rejected++;
    }

    public void Reset()
    {
        _pts.Clear();
        Value = double.NaN;
        PowerSpan = 0;
        TempSpan = 0;
        Solved = false;
        LastReject = "";
        Rejected = 0;
    }

    /// <summary>
    /// 离线拟合（`tplab fit --csv ...`）：把一段采样直接解成 (R, 截距, R²)。
    /// 和在线估计用同一套最小二乘，保证"事后分析"与"实时显示"口径一致。
    /// </summary>
    public static (double r, double a, double r2, int n, double pMin, double pMax, double tMin, double tMax)
        Fit(IEnumerable<(double P, double T)> pts)
    {
        var list = pts.Where(p => p.P > 5).ToList();
        int n = list.Count;
        if (n < 3) return (double.NaN, double.NaN, double.NaN, n, 0, 0, 0, 0);

        double mx = list.Average(p => p.P), my = list.Average(p => p.T);
        double sxx = list.Sum(p => (p.P - mx) * (p.P - mx));
        double sxy = list.Sum(p => (p.P - mx) * (p.T - my));
        if (sxx < 1e-9) return (double.NaN, double.NaN, double.NaN, n, 0, 0, 0, 0);

        double b = sxy / sxx, a = my - b * mx;
        double ssTot = list.Sum(p => (p.T - my) * (p.T - my));
        double ssRes = list.Sum(p => (p.T - (a + b * p.P)) * (p.T - (a + b * p.P)));
        double r2 = ssTot > 1e-9 ? 1 - ssRes / ssTot : double.NaN;
        return (b, a, r2, n, list.Min(p => p.P), list.Max(p => p.P), list.Min(p => p.T), list.Max(p => p.T));
    }

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 空闲样本必须被拒（这是旧版最大的错误来源）
        var est = new ThermalResistance();
        for (int i = 0; i < 20; i++) est.TryAdd(6.1, 40.8, 1.0, 24, 0.0, out _);
        if (!double.IsNaN(est.Value)) errors.Add("空闲/轻载样本竟然被吃进热阻估计");
        if (est.Samples != 0) errors.Add($"被拒样本仍进入窗口：{est.Samples}");

        // B) 带 +4℃ 固定偏置的负载数据：斜率必须解对（旧版单点除法会偏大 10%+）
        var e2 = new ThermalResistance();
        double[] ps = { 15, 20, 25, 30, 35, 40, 45 };
        foreach (var p in ps) e2.TryAdd(p, 28 + 1.20 * p, 95, 24, 0.0, out _);
        if (Math.Abs(e2.Value - 1.20) > 0.03)
            errors.Add($"斜率拟合错：{e2.Value:F3} vs 1.200");
        if (!e2.Confident) errors.Add("7 点、ΔP=30W 却被判为不可信");

        // C) 过渡过程（温度还在爬）样本要拒
        var e3 = new ThermalResistance();
        if (e3.TryAdd(40, 70, 95, 24, 3.2, out _)) errors.Add("温度 3.2℃/s 上升的过渡样本被采纳");

        // D) 非物理斜率要拒（例如负载迁移导致的负相关）
        var e4 = new ThermalResistance();
        if (e4.TryAdd(20, 80, 95, 24, 0, out _)) { }
        if (e4.TryAdd(40, 74, 95, 24, 0, out _)) { }   // 功率高反而更凉
        if (!double.IsNaN(e4.Value) && e4.Value < 0) errors.Add("负斜率被采纳");

        // E) 离线拟合与在线口径一致
        var fit = ThermalResistance.Fit(ps.Select(p => (p, 28 + 1.20 * p)));
        if (Math.Abs(fit.r - 1.20) > 0.03) errors.Add($"Fit 斜率错：{fit.r:F3}");
        if (Math.Abs(fit.a - 28) > 0.5) errors.Add($"Fit 截距错：{fit.a:F1}");
        if (fit.r2 < 0.999) errors.Add($"纯线性数据 R² 应≈1，实际 {fit.r2:F3}");

        return errors;
    }
}
