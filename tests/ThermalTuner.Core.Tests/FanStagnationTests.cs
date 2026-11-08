using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// EC 原始值恒定检测回归测试：验证「连续 N 次真实采样不变」才判定疑似非实时，
/// 且读数变化 / 数据源中断 / 手动重置都能正确清零，避免把正常调速误标成假数。
/// </summary>
internal sealed class FanStagnationTests
{
    public void RunAllTests()
    {
        Check.Suite("FanStagnation");
        RepeatTriggersThreshold();
        ChangeResets();
        MissingRawResets();
        Tolerance();
        ThresholdGuardAndReset();
    }

    private static void RepeatTriggersThreshold()
    {
        var d = new FanStagnationDetector();
        Check.Equal(10, d.Threshold, "默认阈值为 10 次");

        d.Observe(80);
        Check.Equal(0, d.Consecutive, "首次有效读不计入「不变」次数");
        Check.False(d.IsStagnant, "首次有效读不得立即判定恒定");

        for (int i = 0; i < 9; i++) d.Observe(80);
        Check.Equal(9, d.Consecutive, "连续 10 次相同值产生 9 次「不变」");
        Check.False(d.IsStagnant, "未达阈值不得提示");

        d.Observe(80);
        Check.Equal(10, d.Consecutive, "再读一次达到阈值");
        Check.True(d.IsStagnant, "达到阈值应判定疑似非实时");
    }

    private static void ChangeResets()
    {
        var d = new FanStagnationDetector(3);
        for (int i = 0; i < 4; i++) d.Observe(80);
        Check.Equal(3, d.Consecutive, "4 次相同值产生 3 次「不变」");
        Check.True(d.IsStagnant, "阈值 3 时应触发");

        d.Observe(112);
        Check.Equal(0, d.Consecutive, "原始值变化立即清零");
        Check.False(d.IsStagnant, "变化后不得继续标注疑似非实时");

        d.Observe(112);
        d.Observe(112);
        Check.Equal(2, d.Consecutive, "变化后从新值重新计数");
        Check.False(d.IsStagnant, "重新计数未满 3 次");
        d.Observe(112);
        Check.True(d.IsStagnant, "新值连续不变也能触发");
    }

    private static void MissingRawResets()
    {
        var d = new FanStagnationDetector(3);
        for (int i = 0; i < 4; i++) d.Observe(80);
        Check.True(d.IsStagnant, "前置条件：已进入恒定态");

        d.Observe(double.NaN);
        Check.Equal(0, d.Consecutive, "读不到原始字节应重置而不是累加");
        Check.False(d.IsStagnant, "数据源中断后清除恒定标注");

        d.Observe(80);
        Check.Equal(0, d.Consecutive, "恢复后的首次有效读仍从 0 开始");
    }

    private static void Tolerance()
    {
        var d = new FanStagnationDetector(2);
        d.Observe(80);
        d.Observe(80.2);
        Check.Equal(1, d.Consecutive, "容差 0.5 内的抖动视为同一档位");

        d.Observe(80.9);
        Check.Equal(0, d.Consecutive, "超过容差的变化视为真实变化");

        var strict = new FanStagnationDetector(2);
        strict.Observe(80);
        strict.Observe(80.2, epsilon: 0.0);
        Check.Equal(0, strict.Consecutive, "epsilon=0 时任意变化都算变化");
    }

    private static void ThresholdGuardAndReset()
    {
        var d = new FanStagnationDetector(1);
        Check.Equal(2, d.Threshold, "阈值下限钳到 2，避免一拍就误报");

        for (int i = 0; i < 3; i++) d.Observe(58);
        Check.Equal(2, d.Consecutive, "最小阈值下需 2 次「不变」");
        Check.True(d.IsStagnant, "最小阈值下连续 3 次相同值触发");

        d.Reset();
        Check.Equal(0, d.Consecutive, "Reset 清零计数");
        Check.False(d.IsStagnant, "Reset 清除判定");
    }
}
