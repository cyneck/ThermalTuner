using ThermalTuner.Core;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 在线热阻拟合（最小二乘）功能测试。
/// 重点：宁可"没有模型"也不用错模型——样本不足、跨度过小、斜率越界、拟合优度差都必须拒绝；
/// 标定值只能在 ±15% 带内被在线数据微调，不能被瞬态数据整体拖走。
/// </summary>
public class ThermalResistanceTests
{
    public void RunAllTests()
    {
        Check.Suite("ThermalResistance");
        Test_RejectsLowLoad();
        Test_RejectsLowPower();
        Test_RejectsInsufficientSamples();
        Test_RejectsInsufficientSpan();
        Test_FitsKnownSlope();
        Test_RejectsOutOfRangeSlope();
        Test_RejectsPoorFit();
        Test_Calibration_ClampsOnlineDrift();
        Test_Calibration_IgnoresInvalidInput();
        Test_Reset_ClearsEverything();
        Test_RejectsLowLoadAndCountsRejection();
    }

    private static ThermalResistance Feed(double startP, double stepP, int count, Func<double, int, double> temp)
    {
        var r = new ThermalResistance();
        for (int i = 0; i < count; i++)
        {
            double p = startP + i * stepP;
            r.TryAdd(p, temp(p, i), 25.0, 60.0, out _);
        }
        return r;
    }

    private void Test_RejectsLowLoad()
    {
        var r = new ThermalResistance();
        bool ok = r.TryAdd(30, 55, 25, 20, out string why);   // 负载 20% < 30%
        Check.False(ok, "低负载样本不应进入拟合");
        Check.Contains(why, "Load", "拒绝原因应指出负载门槛");
        Check.Equal(1, r.Rejected, "拒绝计数应 +1");
    }

    private void Test_RejectsLowPower()
    {
        var r = new ThermalResistance();
        bool ok = r.TryAdd(8, 40, 25, 80, out string why);    // 功耗 8W < 10W
        Check.False(ok, "低功耗样本不应进入拟合");
        Check.Contains(why, "Load/Power", "拒绝原因应指出功耗门槛");
    }

    private void Test_RejectsInsufficientSamples()
    {
        var r = Feed(20, 2, 5, (p, _) => 40 + 0.9 * p);
        Check.False(r.Solved, "不足 10 个样本时不得给出热阻");
        Check.IsNaN(r.Value, "样本不足时热阻应为 NaN");
        Check.Contains(r.LastReject, "Insufficient samples", "应记录样本不足原因");
    }

    private void Test_RejectsInsufficientSpan()
    {
        // 10 个样本但功率只跨 9W（< 12W 门槛）
        var r = Feed(20, 1, 10, (p, _) => 40 + 0.9 * p);
        Check.False(r.Solved, "功率跨度不足时不得给出热阻");
        Check.True(r.PowerSpan < ThermalResistance.MinSpanW, $"实际跨度 {r.PowerSpan:F1}W 应小于门槛");
        Check.Contains(r.LastReject, "span", "应记录跨度不足原因");
    }

    private void Test_FitsKnownSlope()
    {
        // 12 个样本，功率 20→42W，温度严格按 0.9 K/W 上升
        var r = Feed(20, 2, 12, (p, _) => 40 + 0.9 * p);

        Check.True(r.Solved, "样本与跨度都满足时应拟合成功");
        Check.Near(0.9, r.Value, 0.02, "拟合斜率应接近 0.90 K/W");
        Check.True(r.RSquared > 0.99, $"无噪声数据 R² 应接近 1（实际 {r.RSquared:F3}）");
        Check.True(r.PowerSpan >= ThermalResistance.MinSpanW, "功率跨度应记录");
        Check.True(r.TempSpan >= ThermalResistance.MinRiseC, "温升跨度应记录");
    }

    private void Test_RejectsOutOfRangeSlope()
    {
        // 斜率 3.0 K/W 超出物理区间 [0.2, 2.0]
        var r = Feed(20, 2, 12, (p, _) => 30 + 3.0 * p);
        Check.False(r.Solved, "越界斜率不得被接受");
        Check.Contains(r.LastReject, "out of range", "应记录斜率越界原因");
    }

    private void Test_RejectsPoorFit()
    {
        // 真实斜率 1.0，但叠加 ±25°C 交替噪声：R² 远低于 0.5
        var r = Feed(20, 2, 13, (p, i) => 50 + 1.0 * p + (i % 2 == 0 ? 25 : -25));
        Check.False(r.Solved, "拟合优度不足时不得接受");
        Check.Contains(r.LastReject, "Poor fit", "应记录拟合优度原因");
    }

    private void Test_Calibration_ClampsOnlineDrift()
    {
        var r = new ThermalResistance();
        r.ApplyCalibration(1.0);

        // 灌入斜率 0.2 的"瞬态"数据：只允许在 ±15% 带内微调，不得拖到 0.2
        for (int i = 0; i < 20; i++)
        {
            double p = 20 + i * 2.0;
            r.TryAdd(p, 45 + 0.2 * p, 25, 90, out _);
        }

        Check.True(r.Calibrated, "在线拟合不得清除标定标志");
        Check.True(r.Value >= 0.85 - 1e-9 && r.Value <= 1.15 + 1e-9,
            $"标定值应被限制在 ±15% 带内（实际 {r.Value:F3}）");
        Check.Near(1.0, r.CalibratedValue, 1e-9, "标定原值应保留，供 UI 区分来源");
    }

    private void Test_Calibration_IgnoresInvalidInput()
    {
        var r = new ThermalResistance();
        r.ApplyCalibration(double.NaN);
        r.ApplyCalibration(0);
        r.ApplyCalibration(-1);
        Check.False(r.Calibrated, "非法标定值必须被忽略");
        Check.False(r.Solved, "非法标定值不得产生热阻");
    }

    private void Test_Reset_ClearsEverything()
    {
        var r = Feed(20, 2, 12, (p, _) => 40 + 0.9 * p);
        r.ApplyCalibration(1.1);
        Check.True(r.Solved, "重置前应有解");

        r.Reset();
        Check.False(r.Solved, "重置后应无解");
        Check.False(r.Calibrated, "重置后应清除标定标志");
        Check.IsNaN(r.Value, "重置后热阻应为 NaN");
        Check.Equal(0, r.Rejected, "重置后拒绝计数应归零");
    }

    private void Test_RejectsLowLoadAndCountsRejection()
    {
        var r = new ThermalResistance();
        r.TryAdd(5, 40, 25, 10, out _);
        r.TryAdd(6, 41, 25, 10, out _);
        Check.Equal(2, r.Rejected, "连续拒绝应累计");
        Check.False(r.Solved, "只有低载样本时不得有解");
    }
}
