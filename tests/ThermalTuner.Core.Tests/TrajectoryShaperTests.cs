using ThermalTuner.Core.DecisionTree;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 轨迹整形层测试：中值去突刺 → 一阶低通 → 温度带滞回 → 升档带门控。
/// 全部离线，不依赖 MSR / 驱动。
/// </summary>
public class TrajectoryShaperTests
{
    public void RunAllTests()
    {
        Check.Suite("TrajectoryShaper");
        SpikeIsRejected();
        RealStepIsAccepted();
        LowPassConverges();
        BandHysteresisHolds();
        FallIsDirect();
        RiseGatedWithinBand();
        RiseReleasedOnBandCross();
        LowStressRiseNotRatchet();
        LowStressRiseStillLimited();
        ThermalBypassIgnoresShaping();
        OutputIsQuantizedToWatts();
        InvalidSettingsThrow();
        ResetReopensGate();
        NaNTemperatureKeepsFilter();
        NaNTargetHoldsCurrent();
        PresetsAreValid();
    }

    private static void SpikeIsRejected()
    {
        var s = new TrajectoryShaper();
        for (int i = 0; i < 3; i++) s.Step(60, 30, 30);
        var r = s.Step(78, 30, 30);

        Check.True(r.SpikeRejected, "单拍 60→78 应判为突刺");
        Check.Near(60, r.FilteredTempC, 0.01, "突刺不得改变滤波温度");
        Check.Contains(r.Reason, "突刺", "理由应说明突刺处理");
    }

    private static void RealStepIsAccepted()
    {
        var s = new TrajectoryShaper();
        for (int i = 0; i < 3; i++) s.Step(60, 30, 30);
        s.Step(78, 30, 30);                 // 第一拍被判突刺
        var r = s.Step(78, 30, 30);         // 中值转向后应被采信

        Check.False(r.SpikeRejected, "连续两拍同向 78 不应再判突刺");
        Check.True(r.FilteredTempC > 62, $"滤波温度应开始上行，实际 {r.FilteredTempC:F2}");
    }

    private static void LowPassConverges()
    {
        var s = new TrajectoryShaper();
        s.Step(60, 30, 30);
        ShapedTarget r = default;
        for (int i = 0; i < 12; i++) r = s.Step(70, 30, 30);

        Check.True(r.FilteredTempC > 68 && r.FilteredTempC < 70,
            $"低通应收敛到 70 附近但不越过，实际 {r.FilteredTempC:F2}");
    }

    private static void BandHysteresisHolds()
    {
        var s = new TrajectoryShaper();
        var first = s.Step(60, 30, 30);
        Check.Equal(12, first.TempBandIndex, "60 °C 应落在第 12 带（每带 5 °C）");

        ShapedTarget r = default;
        for (int i = 0; i < 6; i++) r = s.Step(65.5, 30, 30);
        Check.Equal(12, r.TempBandIndex, "65.5 °C 未越过 65+1 滞回，带索引不得上翻");

        for (int i = 0; i < 6; i++) r = s.Step(67, 30, 30);
        Check.Equal(13, r.TempBandIndex, "67 °C 越过 66 应升到第 13 带");

        for (int i = 0; i < 6; i++) r = s.Step(64.5, 30, 30);
        Check.Equal(13, r.TempBandIndex, "64.5 °C 未退到 64 以下，带索引不得下翻");
    }

    private static void FallIsDirect()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(60, 25, 45);

        Check.Near(25, r.Pl1W, 0.001, "降档不受温度带门控，立即到目标");
        Check.False(r.GateHeld, "降档不得被门控拦截");
        Check.Contains(r.Reason, "降档直通", "理由应说明降档直通");
    }

    private static void RiseGatedWithinBand()
    {
        var s = new TrajectoryShaper();
        var r1 = s.Step(60, 45, 30);

        // 会话首次升档：门控放行，但限幅 +6W
        Check.Near(36, r1.Pl1W, 0.001, "首次升档限幅 +6 W（30→36）");
        Check.Contains(r1.Reason, "带门控放行", "理由应说明门控放行");

        // 同带内继续升：门控保持，不再放行
        var r2 = s.Step(60, 45, r1.Pl1W);
        Check.Near(36, r2.Pl1W, 0.001, "同带内升档应被门控拦下");
        Check.True(r2.GateHeld, "结果应标记 GateHeld");
        Check.Contains(r2.Reason, "升档暂缓", "理由应说明升档暂缓");
    }

    private static void RiseReleasedOnBandCross()
    {
        var s = new TrajectoryShaper();
        double cur = 30;
        var r = s.Step(60, 45, cur);                 // 首次放行：36，记录第 12 带
        Check.Near(36, r.Pl1W, 0.001, "首次放行 36 W");
        cur = r.Pl1W;

        r = s.Step(68, 45, cur);                     // 同带，仍拦
        Check.Near(36, r.Pl1W, 0.001, "跨带前不得放行");

        while (r.TempBandIndex < 13)
        {
            r = s.Step(68, 45, cur);
            cur = r.Pl1W;
        }
        Check.Equal(13, r.TempBandIndex, "68 °C 应使滤波温度越过 66 进入第 13 带");
        Check.Near(42, cur, 0.001, "跨带那一拍应放行限幅 +6 W（36→42）");

        r = s.Step(68, 45, cur);                     // 第 13 带内继续拦
        Check.Near(42, r.Pl1W, 0.001, "第 13 带内第二次升档应被拦");
        cur = r.Pl1W;

        while (r.TempBandIndex < 14)
        {
            r = s.Step(72, 45, cur);
            cur = r.Pl1W;
        }
        Check.Equal(14, r.TempBandIndex, "72 °C 应进入第 14 带");
        Check.Near(45, cur, 0.001, "再次跨带后到达目标 45 W（min(45, 42+6)）");
    }

    /// <summary>
    /// 热余量充足时不得形成「只降不升」的棘轮：同带内也应放行升档。
    /// 场景：满载掉到 41 W 后转轻载，温度稳定在同带，目标回到固件 45 W。
    /// </summary>
    private static void LowStressRiseNotRatchet()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(58, 45, 36);          // 首次升档放行 → 42，并记录当前带
        Check.Near(42, r.Pl1W, 0.001, "首次升档应放行（限幅 +6 W）");

        r = s.Step(58, 45, r.Pl1W);          // 同带，被拦
        Check.Near(42, r.Pl1W, 0.001, "未放开时应保持（确认门控仍然生效）");
        Check.True(r.GateHeld, "同带升档应标记为被门控");

        r = s.Step(58, 45, 42, riseUnrestricted: true);
        Check.True(!r.GateHeld, "热余量充足时不应再标记被门控");
        Check.Near(45, r.Pl1W, 0.001, "热余量充足时同带也应放行至目标（不形成棘轮）");
    }

    /// <summary>放开带门控不等于取消限速：单拍仍受 MaxRiseWPerGate 约束。</summary>
    private static void LowStressRiseStillLimited()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(50, 45, 15, riseUnrestricted: true);
        Check.Near(21, r.Pl1W, 0.001, "免带门控仍限幅 +6 W（15→21）");
        Check.Contains(r.Reason, "热余量", "理由应说明免带门控的原因");
    }

    private static void ThermalBypassIgnoresShaping()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(95, 20, 45, thermalBypass: true);

        Check.Near(20, r.Pl1W, 0.001, "高温直通必须立即到目标，不受门控约束");
        Check.True(r.Bypassed, "结果应标记为直通");
        Check.Contains(r.Reason, "安全直通", "理由应说明安全直通");
    }

    private static void OutputIsQuantizedToWatts()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(60, 33.4, 30);
        Check.Near(Math.Ceiling(r.Pl1W), r.Pl1W, 0.0001, "输出应为 1 W 台阶");
    }

    private static void InvalidSettingsThrow()
    {
        Check.Throws<ArgumentException>(() => new TrajectoryShaper(new TrajectorySettings { EmaAlpha = 0 }),
            "alpha=0 应拒绝");
        Check.Throws<ArgumentException>(() => new TrajectoryShaper(new TrajectorySettings { MedianWindow = 4 }),
            "偶数中值窗口应拒绝");
        Check.Throws<ArgumentException>(() => new TrajectoryShaper(new TrajectorySettings { BandHysteresisC = 5 }),
            "滞回不得大于等于带宽");
        Check.Throws<ArgumentException>(() => new TrajectoryShaper(new TrajectorySettings { MaxRiseWPerGate = 0 }),
            "门控升幅必须为正");
    }

    private static void ResetReopensGate()
    {
        var s = new TrajectoryShaper();
        s.Step(60, 45, 30);                  // 放行 36，记录第 12 带
        var held = s.Step(60, 45, 36);
        Check.True(held.GateHeld, "同带第二次升档应被拦");

        s.Reset();
        var r = s.Step(60, 45, 36);
        Check.True(!r.GateHeld && r.Pl1W > 36, "Reset 后门控应重新打开（首次升档放行）");
        Check.Near(60, r.FilteredTempC, 0.01, "Reset 后滤波值应等于原始值");
    }

    private static void NaNTemperatureKeepsFilter()
    {
        var s = new TrajectoryShaper();
        s.Step(60, 30, 30);
        var r = s.Step(double.NaN, 30, 30);
        Check.Near(60, r.FilteredTempC, 0.01, "无温度读数时滤波值应保持不变");
    }

    private static void NaNTargetHoldsCurrent()
    {
        var s = new TrajectoryShaper();
        var r = s.Step(60, double.NaN, 32);
        Check.Near(32, r.Pl1W, 0.001, "无有效目标时应保持当前 PL1");
        Check.Contains(r.Reason, "无目标", "理由应说明无目标");
    }

    private static void PresetsAreValid()
    {
        Check.True(TrajectorySettings.Default.Validate() is null, "Default 预设应合法");
        Check.True(TrajectorySettings.Smooth.Validate() is null, "Smooth 预设应合法");
        Check.True(TrajectorySettings.Agile.Validate() is null, "Agile 预设应合法");
        Check.True(TrajectorySettings.Smooth.MaxRiseWPerGate < TrajectorySettings.Default.MaxRiseWPerGate,
            "Smooth 的门控升幅应比 Default 更保守");
        Check.True(TrajectorySettings.Agile.MaxRiseWPerGate > TrajectorySettings.Default.MaxRiseWPerGate,
            "Agile 的门控升幅应比 Default 更宽松");
        Check.Equal(5.0, TrajectorySettings.Smooth.BandC, "Smooth 使用 5 °C 温度带");
    }
}
