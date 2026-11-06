using ThermalTuner.Core;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 两段式稳态标定与热阻模型单元测试。
/// 重点验证：差商公式、跨度/温升门槛、物理区间钳制、标定值不被在线拟合推翻。
/// </summary>
public class ThermalCalibrationTests
{
    public void RunAllTests()
    {
        Test_TwoPoint_BasicSlope();
        Test_TwoPoint_RejectsNarrowSpan();
        Test_TwoPoint_RejectsSmallRise();
        Test_TwoPoint_RejectsOutOfRange();
        Test_ApplyCalibration_OverridesOnlineFit();
        Test_ApplyCalibration_SurvivesOnlineUpdates();
        Test_PredictSteady_MatchesTwoPointModel();
        Test_MaxPowerForCeiling_InvertsPrediction();
        Test_CalibratedRth_CapsDecisionPl1();
        Test_Uncalibrated_HasNoThermalCap();
        Console.WriteLine("All ThermalCalibration unit tests passed successfully!");
    }

    public void Test_TwoPoint_BasicSlope()
    {
        // 45°C/12W → 78°C/55W：ΔT=33, ΔP=43 => 0.767 K/W
        bool ok = ThermalCalibration.TryTwoPoint(45, 12, 78, 55, out double rth, out _);
        Assert(ok, "两点法应成功");
        Assert(Math.Abs(rth - 33.0 / 43.0) < 1e-6, $"热阻应为 0.767，实得 {rth:F3}");
    }

    public void Test_TwoPoint_RejectsNarrowSpan()
    {
        // 满载只比空载高 8W，达不到 12W 跨度
        bool ok = ThermalCalibration.TryTwoPoint(45, 20, 60, 28, out double rth, out string reason);
        Assert(!ok, "功率跨度不足时应拒绝");
        Assert(double.IsNaN(rth), "拒绝时热阻必须为 NaN");
        Assert(reason.Contains("跨度"), "原因应指出功率跨度：" + reason);
    }

    public void Test_TwoPoint_RejectsSmallRise()
    {
        bool ok = ThermalCalibration.TryTwoPoint(45, 10, 46, 40, out _, out string reason);
        Assert(!ok, "温升不足时应拒绝");
        Assert(reason.Contains("温升"), "原因应指出温升：" + reason);
    }

    public void Test_TwoPoint_RejectsOutOfRange()
    {
        // ΔT=60 / ΔP=15 => 4.0 K/W，超出物理区间
        bool ok = ThermalCalibration.TryTwoPoint(30, 10, 90, 25, out _, out string reason);
        Assert(!ok, "超出物理区间的热阻应拒绝");
        Assert(reason.Contains("区间"), "原因应指出物理区间：" + reason);
    }

    public void Test_ApplyCalibration_OverridesOnlineFit()
    {
        var r = new ThermalResistance();
        r.ApplyCalibration(0.86);
        Assert(r.Solved, "标定后应视为已求解");
        Assert(r.Calibrated, "标定标志应置位");
        Assert(Math.Abs(r.Value - 0.86) < 1e-9, "标定值应直接生效");
    }

    public void Test_ApplyCalibration_SurvivesOnlineUpdates()
    {
        var r = new ThermalResistance();
        r.ApplyCalibration(0.86);

        // 灌入一段斜率 0.30 的在线样本：标定值只允许被微调，不允许被整体拖走
        for (int i = 0; i < 20; i++)
        {
            double p = 20 + i * 2.0;
            double t = 45 + (p - 20) * 0.30;
            r.TryAdd(p, t, 25, 90, out _);
        }

        Assert(r.Calibrated, "在线拟合不应清掉标定标志");
        Assert(r.Value > 0.7, $"标定值应基本保持（实得 {r.Value:F2}）");
    }

    public void Test_PredictSteady_MatchesTwoPointModel()
    {
        // 基线 53.2°C@13.5W，Rth 0.98 → 45W 下应为 84.1°C
        double t = ThermalCalibration.PredictSteady(0.98, 53.2, 13.5, 45.0);
        Assert(Math.Abs(t - (53.2 + (45.0 - 13.5) * 0.98)) < 1e-9, "统一预测入口必须等于两点稳态式");
        // 目标功耗低于基线时不外推出负温升
        Assert(Math.Abs(ThermalCalibration.PredictSteady(0.98, 53.2, 13.5, 10.0) - 53.2) < 1e-9,
            "目标功耗低于基线应返回基线温度");
    }

    public void Test_MaxPowerForCeiling_InvertsPrediction()
    {
        double cap = ThermalCalibration.MaxPowerForCeiling(1.0, 50.0, 10.0, 88.0);
        Assert(Math.Abs(cap - 48.0) < 1e-9, $"反解上限应为 48W，实得 {cap:F2}");
        Assert(Math.Abs(ThermalCalibration.PredictSteady(1.0, 50.0, 10.0, cap) - 88.0) < 1e-9,
            "在反解上限处预测稳态应恰好等于天花板");
        Assert(double.IsNaN(ThermalCalibration.MaxPowerForCeiling(0, 50, 10, 88)), "无效热阻应返回 NaN");
    }

    public void Test_CalibratedRth_CapsDecisionPl1()
    {
        // 标定后每拍都要用稳态模型钳制决策上限，而不是只把 Rth 显示在卡片上
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        c.ApplyCalibratedRth(2.0);
        c.SetThermalBaseline(50.0, 10.0);   // 天花板 88 → cap = 10 + 38/2 = 29W

        var dec = c.Update(tempC: 60, powerW: 30, ceilingC: 88, ambientC: 25,
            profile: "performance", loadPct: 90, dTdt: 0.2,
            perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8 });

        Assert(!double.IsNaN(dec.ThermalCapW), "标定后应产出前馈上限");
        Assert(Math.Abs(dec.ThermalCapW - 29.0) < 1e-6, $"上限应为 29W，实得 {dec.ThermalCapW:F2}");
        Assert(dec.Pl1W <= dec.ThermalCapW + 1e-6,
            $"决策 PL1 {dec.Pl1W:F1}W 不得超过标定上限 {dec.ThermalCapW:F1}W");
    }

    public void Test_Uncalibrated_HasNoThermalCap()
    {
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        var dec = c.Update(tempC: 60, powerW: 30, ceilingC: 88, ambientC: 25,
            profile: "balanced", loadPct: 90, dTdt: 0.2);
        Assert(double.IsNaN(dec.ThermalCapW), "未标定时不得用不可信模型钳制决策");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("ASSERT FAILED: " + message);
    }
}
