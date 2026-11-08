using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 热事件埋点（v0.5.0 P1 研究基础设施）回归测试。验证触顶 → 降档记录 → 回落 →
/// 60s 恢复观察 → 二次触顶计数的完整生命周期，以及数据中断 / 会话结束的收尾。
/// </summary>
internal sealed class ThermalEventTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc);

    public void RunAllTests()
    {
        Check.Suite("ThermalEvent");
        NoEventBelowCeiling();
        FullLifecycle();
        SecondaryTouchCounted();
        FlushIncompleteEvent();
        TemperatureLossFinalizes();
        TimeoutFinalizes();
    }

    private static void NoEventBelowCeiling()
    {
        var r = new ThermalEventRecorder();
        for (int i = 0; i < 10; i++)
            Check.True(r.Observe(70 + i, 88, 45, T0.AddSeconds(i)) is null,
                $"第 {i} 拍温度低于天花板不应产生事件");
        Check.False(r.IsActive, "始终保持空闲");
        Check.True(r.Flush(T0.AddSeconds(20)) is null, "空闲时 Flush 不产生空记录");
    }

    private static void FullLifecycle()
    {
        var r = new ThermalEventRecorder();
        Check.True(r.Observe(89, 88, 45, T0) is null, "触顶首拍只是开始事件");
        Check.True(r.IsActive, "触顶后进入活跃态");
        Check.Contains(r.PhaseText, "触顶", "阶段文案为触顶中");

        // 升到峰值 91°C，同时决策 PL1 从 45W 降到 36W
        Check.True(r.Observe(91, 88, 36, T0.AddSeconds(1)) is null, "峰值拍仍在事件中");
        Check.True(r.Observe(90, 88, 36, T0.AddSeconds(2)) is null, "未回落不进入恢复");

        // 回落到 85°C（< 88-2），进入恢复观察
        Check.True(r.Observe(85, 88, 36, T0.AddSeconds(3)) is null, "回落拍进入恢复观察");
        Check.Contains(r.PhaseText, "恢复", "阶段文案为恢复观察");

        var done = r.Observe(70, 88, 36, T0.AddSeconds(63));
        Check.True(done is not null, "恢复观察满 60s 后输出一条记录");
        var e = done!;
        Check.Near(91, e.PeakC, 0.01, "峰值温度 91°C");
        Check.Near(88, e.CeilingC, 0.01, "天花板 88°C");
        Check.Near(3, e.OvershootC, 0.01, "超温幅度 3°C");
        Check.Equal(1, e.StepDownCount, "记录 1 次降档");
        Check.Near(9, e.StepDownWatts, 0.01, "降档总瓦数 9W");
        Check.Equal(0, e.SecondaryTouches60s, "恢复期内未二次触顶");
        Check.Near(60, e.CooldownSeconds, 0.1, "恢复观察时长 60s");
        Check.Contains(e.EndReason, "恢复观察完成", "结束原因正确");
        Check.False(double.IsNaN(e.FallRateP50), "回落速率 P50 有值");
        Check.True(e.FallRateP95 >= e.FallRateP50, "P95 不小于 P50");
        Check.False(r.IsActive, "事件结束后回到空闲");
    }

    private static void SecondaryTouchCounted()
    {
        var r = new ThermalEventRecorder();
        r.Observe(89, 88, 45, T0);
        r.Observe(85, 88, 40, T0.AddSeconds(1));       // 进入恢复
        r.Observe(88.5, 88, 40, T0.AddSeconds(5));     // 二次触顶
        r.Observe(84, 88, 40, T0.AddSeconds(6));
        r.Observe(89, 88, 40, T0.AddSeconds(20));      // 又一次
        r.Observe(83, 88, 40, T0.AddSeconds(21));

        var e = r.Observe(70, 88, 40, T0.AddSeconds(61));
        Check.True(e is not null, "恢复观察结束输出记录");
        Check.Equal(2, e!.SecondaryTouches60s, "60s 内二次触顶计 2 次（关键标签）");
    }

    private static void FlushIncompleteEvent()
    {
        var r = new ThermalEventRecorder();
        r.Observe(90, 88, 45, T0);
        r.Observe(92, 88, 38, T0.AddSeconds(2));
        var e = r.Flush(T0.AddSeconds(5));
        Check.True(e is not null, "会话结束时收尾未完结事件");
        Check.Contains(e!.EndReason, "会话结束", "结束原因为会话结束");
        Check.Near(92, e.PeakC, 0.01, "未完结事件也保留峰值");
        Check.Near(7, e.StepDownWatts, 0.01, "未完结事件也保留降档瓦数");
        Check.False(r.IsActive, "Flush 后回到空闲");
    }

    private static void TemperatureLossFinalizes()
    {
        var r = new ThermalEventRecorder();
        r.Observe(90, 88, 45, T0);
        var e = r.Observe(double.NaN, 88, 45, T0.AddSeconds(1));
        Check.True(e is not null, "温度读数中断时收尾事件");
        Check.Contains(e!.EndReason, "中断", "结束原因为数据中断");
        Check.True(double.IsNaN(e.FallRateP50), "没有足够回落样本时 P50 为 NaN（不伪造）");
        Check.False(r.IsActive, "中断后回到空闲");
    }

    private static void TimeoutFinalizes()
    {
        var r = new ThermalEventRecorder();
        r.Observe(90, 88, 45, T0);
        var mid = r.Observe(90, 88, 45, T0.AddSeconds(300));
        Check.True(mid is null, "未到 10 分钟超时阈值不收尾");
        var e = r.Observe(90, 88, 45, T0.AddSeconds(601));
        Check.True(e is not null, "持续触顶 10 分钟后强制收尾");
        Check.Contains(e!.EndReason, "超时", "结束原因为事件超时");
    }
}
