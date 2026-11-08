using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 阶梯式下发调度器（v0.5.0）回归测试：验证 1W 量化、连续 3 拍迟滞、升档冷却 20s、
/// 高温降档立即放行、事件通道二次确认、高温只降不升硬闸与每分钟熔断。
/// 所有用例使用注入时间，不依赖真实时钟，也不访问硬件。
/// </summary>
internal sealed class WriteSchedulerTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 1, 0, 0, DateTimeKind.Utc);

    private static WriteRequest Req(WriteChannel ch, double pl1, double seconds,
        double tempC = 60, bool gates = true, double hardCeiling = 95) =>
        new(ch, pl1, tempC, T0.AddSeconds(seconds), gates, hardCeiling);

    private static WriteScheduler New(double cooldown = 20, int maxWrites = 4) =>
        new(15, 65, cooldown, 3, maxWrites);

    public void RunAllTests()
    {
        Check.Suite("WriteScheduler");
        QuantizeAndSameStepSkip();
        FirstWriteEstablishesBaseline();
        AutoRampUpNeedsThreeTicks();
        RampUpCooldownAndForce();
        StepDownIgnoresCooldown();
        ThermalStepDownImmediate();
        HardCeilingCannotBeOverridden();
        GatesClosedIsHardBlocked();
        FuseStopsAfterPerMinuteLimit();
        EventChannelWritesSameStep();
        SeedResetAndConfig();
    }

    private static void QuantizeAndSameStepSkip()
    {
        var sc = New();
        sc.SeedAppliedStep(40.4, T0);
        Check.Equal(40, sc.LastAppliedStep!.Value, "种子值量化到 40W 台阶");

        var d = sc.Evaluate(Req(WriteChannel.AutoTick, 40.49, 1));
        Check.True(d.Verdict == WriteVerdict.Skip, "自动通道：40.49W 与 40W 同台阶，不下发");
        Check.Equal(40, d.StepPl1W, "量化后台阶为 40W");

        var d2 = sc.Evaluate(Req(WriteChannel.AutoTick, 40.6, 2));
        Check.Equal(41, d2.StepPl1W, "40.6W 量化到 41W 台阶");
        Check.True(d2.IsRampUp, "跨到 41W 台阶算升档");
        Check.True(d2.Verdict == WriteVerdict.Skip, "升档首拍仍被迟滞挡住");
    }

    private static void FirstWriteEstablishesBaseline()
    {
        var sc = New();
        Check.True(sc.LastAppliedStep is null, "前置条件：会话尚未下发过");
        var d = sc.Evaluate(Req(WriteChannel.AutoTick, 42, 1));
        Check.True(d.Verdict == WriteVerdict.Write, "首次自动通道必须写一次建立台阶基准");
        Check.Contains(d.Reason, "首次", "原因说明为首次建立基准");

        sc.Commit(d.StepPl1W, T0.AddSeconds(1));
        Check.Equal(42, sc.LastAppliedStep!.Value, "Commit 后台阶基准为 42W");
        Check.True(sc.Evaluate(Req(WriteChannel.AutoTick, 42, 2)).Verdict == WriteVerdict.Skip,
            "基准建立后同台阶不再重复下发");
    }

    private static void AutoRampUpNeedsThreeTicks()
    {
        var sc = New();
        sc.SeedAppliedStep(40, T0);

        for (int i = 1; i <= 2; i++)
        {
            var d = sc.Evaluate(Req(WriteChannel.AutoTick, 45, i));
            Check.True(d.Verdict == WriteVerdict.Skip, $"第 {i} 拍升档应被迟滞挡住");
            Check.Contains(d.Reason, "迟滞", "原因说明包含迟滞进度");
        }

        var third = sc.Evaluate(Req(WriteChannel.AutoTick, 45, 3));
        Check.True(third.Verdict == WriteVerdict.Write, "连续第 3 拍同台阶允许下发");
        Check.True(third.IsRampUp, "判定为升档");
        Check.Equal(45, third.StepPl1W, "写入 45W 台阶");

        // 目标换台阶后迟滞重新计数
        sc.Commit(45, T0.AddSeconds(3));
        var changed = sc.Evaluate(Req(WriteChannel.AutoTick, 46, 30));
        Check.True(changed.Verdict == WriteVerdict.Skip, "换成 46W 台阶需重新计数迟滞");
    }

    private static void RampUpCooldownAndForce()
    {
        var sc = New();
        sc.SeedAppliedStep(40, T0);
        sc.Commit(45, T0.AddSeconds(1));    // 一次升档写入，启动 20s 升档冷却

        double remaining = sc.CooldownRemaining(T0.AddSeconds(6));
        Check.Near(15, remaining, 0.01, "升档后 5 秒，冷却剩余 15 秒");

        // 自动通道先要满足连续 3 拍同台阶，第 3 拍才会命中升档冷却。
        for (int i = 6; i <= 7; i++)
            Check.Contains(sc.Evaluate(Req(WriteChannel.AutoTick, 50, i)).Reason, "迟滞",
                $"第 {i} 秒仍受迟滞约束");

        var auto = sc.Evaluate(Req(WriteChannel.AutoTick, 50, 8));
        Check.True(auto.Verdict == WriteVerdict.Skip, "自动通道在升档冷却内直接跳过");
        Check.Contains(auto.Reason, "冷却", "迟滞满足后原因说明包含升档冷却");

        var ev = sc.Evaluate(Req(WriteChannel.ProfileEvent, 50, 8));
        Check.True(ev.Verdict == WriteVerdict.NeedConfirmation,
            "事件通道跳过迟滞，但升档冷却内要求二次确认");
        Check.Near(13, ev.CooldownRemainingS, 0.01, "结论带上剩余冷却秒数（T0+1 起算，T0+8 剩 13s）");

        var forced = sc.ForceWrite(Req(WriteChannel.ProfileEvent, 50, 8));
        Check.True(forced.Verdict == WriteVerdict.Write, "用户二次确认后强制放行");
        Check.Contains(forced.Reason, "二次确认", "结论说明来自二次确认");

        var expired = sc.Evaluate(Req(WriteChannel.AutoTick, 50, 22));
        // 迟滞此前已被事件通道跳过，自动通道此时需重新计 3 拍；这里只验证冷却已过。
        Check.Near(0, sc.CooldownRemaining(T0.AddSeconds(22)), 0.01, "22 秒后升档冷却已过");
        Check.True(expired.Reason.Contains("迟滞") || expired.Verdict == WriteVerdict.Write,
            "冷却过期后只剩迟滞约束");
    }

    private static void StepDownIgnoresCooldown()
    {
        var sc = New();
        sc.SeedAppliedStep(50, T0);
        sc.Commit(50, T0.AddSeconds(1));
        // 人为制造一次升档冷却基准
        sc.Commit(55, T0.AddSeconds(2));
        Check.True(sc.CooldownRemaining(T0.AddSeconds(3)) > 0, "前置条件：处于升档冷却中");

        var d = sc.Evaluate(Req(WriteChannel.AutoTick, 35, 3, tempC: 70));
        Check.True(d.Verdict == WriteVerdict.Write, "降档不受升档冷却约束");
        Check.False(d.IsRampUp, "降档不是升档");
        Check.Equal(35, d.StepPl1W, "降档写入 35W 台阶");
    }

    private static void ThermalStepDownImmediate()
    {
        var sc = New();
        sc.SeedAppliedStep(50, T0);
        sc.Commit(55, T0.AddSeconds(1));   // 冷却中
        var d = sc.Evaluate(Req(WriteChannel.AutoTick, 30, 2, tempC: 96));
        Check.True(d.Verdict == WriteVerdict.Write, "高温降档立即下发");
        Check.True(d.IsThermalStepDown, "标记为高温降档");
        Check.Contains(d.Reason, "立即", "原因说明为立即放行");
    }

    private static void HardCeilingCannotBeOverridden()
    {
        var sc = New();
        sc.SeedAppliedStep(40, T0);
        var req = Req(WriteChannel.ProfileEvent, 60, 1, tempC: 95.4);
        var d = sc.Evaluate(req);
        Check.True(d.Verdict == WriteVerdict.HardBlocked, "≥ 安全上限时升档被硬闸拦下");
        Check.Contains(d.Reason, "只降不升", "原因说明只降不升");

        var forced = sc.ForceWrite(req);
        Check.True(forced.Verdict == WriteVerdict.HardBlocked, "二次确认不能解除高温硬闸");

        var down = sc.Evaluate(Req(WriteChannel.ProfileEvent, 30, 2, tempC: 95.4));
        Check.True(down.Verdict == WriteVerdict.Write, "同一温度下只允许降档");
    }

    private static void GatesClosedIsHardBlocked()
    {
        var sc = New();
        sc.SeedAppliedStep(40, T0);
        var d = sc.Evaluate(Req(WriteChannel.ProfileEvent, 45, 1, gates: false));
        Check.True(d.Verdict == WriteVerdict.HardBlocked, "写入闸门未开时不下发");
        Check.Contains(d.Reason, "闸门", "原因说明闸门未全开");

        var f = sc.ForceWrite(Req(WriteChannel.ProfileEvent, 45, 1, gates: false));
        Check.True(f.Verdict == WriteVerdict.HardBlocked, "二次确认无法绕过权限闸门");
    }

    private static void FuseStopsAfterPerMinuteLimit()
    {
        var sc = New(maxWrites: 4);
        sc.SeedAppliedStep(40, T0);
        for (int i = 1; i <= 4; i++)
        {
            var r = sc.Evaluate(Req(WriteChannel.ProfileEvent, 40, i));
            Check.True(r.Verdict == WriteVerdict.Write, $"第 {i} 次显式下发允许");
            sc.Commit(r.StepPl1W, T0.AddSeconds(i));
        }
        Check.Equal(4, sc.WritesInLastMinute, "窗口内累计 4 次写入");

        var blocked = sc.Evaluate(Req(WriteChannel.ProfileEvent, 40, 5));
        Check.True(blocked.Verdict == WriteVerdict.HardBlocked, "超过每分钟上限触发熔断");
        Check.True(sc.Tripped, "熔断标记已置位");
        Check.True(sc.ForceWrite(Req(WriteChannel.ProfileEvent, 30, 5)).Verdict == WriteVerdict.HardBlocked,
            "二次确认不能解除熔断");

        sc.Reset();
        Check.False(sc.Tripped, "Reset 清除熔断");
        Check.Equal(0, sc.WritesInLastMinute, "Reset 清空写入计数");
        Check.True(sc.Evaluate(Req(WriteChannel.ProfileEvent, 40, 6)).Verdict == WriteVerdict.Write,
            "重置后可重新下发");
    }

    private static void EventChannelWritesSameStep()
    {
        var sc = New();
        sc.SeedAppliedStep(40, T0);
        foreach (var ch in new[] { WriteChannel.ProfileEvent, WriteChannel.ManualSave, WriteChannel.StartupReplay })
        {
            var d = sc.Evaluate(Req(ch, 40, 1));
            Check.True(d.Verdict == WriteVerdict.Write, $"{ch} 同台阶也做一次显式下发");
            Check.Contains(d.Reason, "回读", "原因说明包含回读校验");
        }
    }

    private static void SeedResetAndConfig()
    {
        var tight = New(cooldown: 1);
        Check.Near(5, tight.CooldownSeconds, 0.01, "冷却下限钳到 5s");

        var loose = New(cooldown: 999);
        Check.Near(120, loose.CooldownSeconds, 0.01, "冷却上限钳到 120s");

        loose.CooldownSeconds = 20;
        Check.Near(20, loose.CooldownSeconds, 0.01, "设置页可调冷却秒数");

        loose.SeedAppliedStep(45, T0);
        Check.Equal(45, loose.LastAppliedStep!.Value, "重放/恢复原值后同步台阶");
        Check.Near(0, loose.CooldownRemaining(T0.AddSeconds(1)), 0.01,
            "Seed 不算升档，不启动升档冷却");

        loose.Commit(50, T0.AddSeconds(2), success: false);
        Check.Equal(45, loose.LastAppliedStep!.Value, "写入失败不推进台阶");
        Check.Near(0, loose.CooldownRemaining(T0.AddSeconds(3)), 0.01, "写入失败不启动冷却");
    }
}
