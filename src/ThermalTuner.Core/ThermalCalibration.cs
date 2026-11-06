namespace ThermalTuner.Core;

/// <summary>
/// 两点稳态标定：R_th = (T_hot − T_idle) / (P_hot − P_idle)。
/// 与在线最小二乘不同，这里取的是「两段稳态的差商」，
/// 只要两段都进入温度平台，结果就不受热容瞬态影响 ——
/// 这正是 20 秒单点压测做不到的事（上升沿斜率会被热容拉低，热阻被估小）。
/// 门槛沿用 ThermalResistance 的跨度常量，保证两条路径口径一致。
/// </summary>
public static class ThermalCalibration
{
    /// <summary>可用热阻下界：低于此值基本是「没有热阻」的伪数据。</summary>
    public const double MinPlausibleRth = 0.05;

    /// <summary>可用热阻上界：被动散热整机也很少超过 3 K/W。</summary>
    public const double MaxPlausibleRth = 3.0;

    /// <summary>
    /// 由轻载/满载两个稳态点求热阻。任一前置条件不满足即返回 false 并给出可读原因，
    /// 调用方应把原因原样展示给用户，而不是静默回退。
    /// </summary>
    public static bool TryTwoPoint(
        double idleC, double idleW, double hotC, double hotW,
        out double rth, out string reason)
    {
        rth = double.NaN;

        if (double.IsNaN(idleC) || double.IsNaN(idleW) ||
            double.IsNaN(hotC) || double.IsNaN(hotW))
        {
            reason = "存在无效采样（NaN），请检查温度/功耗读数是否可用";
            return false;
        }

        double dP = hotW - idleW;
        double dT = hotC - idleC;

        if (dP < ThermalResistance.MinSpanW)
        {
            reason = $"功率跨度不足：{idleW:F1}W → {hotW:F1}W（需 ≥ {ThermalResistance.MinSpanW:F0}W）";
            return false;
        }

        if (dT < ThermalResistance.MinRiseC)
        {
            reason = $"温升不足：{idleC:F1}°C → {hotC:F1}°C（需 ≥ {ThermalResistance.MinRiseC:F0}°C）";
            return false;
        }

        double r = dT / dP;
        if (r < MinPlausibleRth || r > MaxPlausibleRth)
        {
            reason = $"热阻 {r:F2} K/W 超出物理区间 [{MinPlausibleRth:F2}, {MaxPlausibleRth:F1}]";
            return false;
        }

        rth = r;
        reason = $"ΔT {dT:F1}°C ÷ ΔP {dP:F1}W = {r:F2} K/W";
        return true;
    }

    /// <summary>
    /// 唯一的稳态预测入口：T(P) = T_idle + max(0, P − P_idle)·R_th。
    /// 标定报告、界面预测卡片、决策树前馈必须共用此式，禁止各自用瞬态温度另算一套。
    /// idleTempC / idlePowerW 必须是同一时刻的轻载稳态样本，否则差商与外推口径不一致。
    /// </summary>
    public static double PredictSteady(double rth, double idleTempC, double idlePowerW, double targetPowerW)
        => idleTempC + Math.Max(0, targetPowerW - idlePowerW) * rth;

    /// <summary>反解：在天花板 ceilingC 约束下允许的最大稳态功耗；R_th 无效时返回 NaN。</summary>
    public static double MaxPowerForCeiling(double rth, double idleTempC, double idlePowerW, double ceilingC)
        => rth <= 0 || double.IsNaN(rth) || double.IsNaN(idleTempC) || double.IsNaN(idlePowerW)
            ? double.NaN
            : idlePowerW + Math.Max(0, ceilingC - idleTempC) / rth;
}
