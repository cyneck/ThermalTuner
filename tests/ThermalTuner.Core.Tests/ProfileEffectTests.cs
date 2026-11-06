using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 档位（UserProfile）到底有没有进入决策树：三档 A/B 对照 + 天花板覆盖关系。
/// 背景：用户质疑「挡位选择是否有效」，这里把「算法层有效」做成可复现断言；
/// 至于「是否写入寄存器」，由 App 层 TryAutoApply 的闸门决定，不在此套件范围。
/// </summary>
internal sealed class ProfileEffectTests
{
    public void RunAllTests()
    {
        Check.Suite("ProfileEffect");
        StaticProfileTable();
        ProfileParametersReachDecision();
        CeilingDrivesThermalSuppression();
    }

    private static AutoCpuDecision Tick(string profile, double ceiling,
        double tempC = 50, double powerW = 12, double loadPct = 8)
    {
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        c.SetCurrentPl1W(45.0);   // 注入实读固件 PL1，避免仿真占位值影响断言
        return c.Update(tempC: tempC, powerW: powerW, ceilingC: ceiling, ambientC: 25,
            profile: profile, loadPct: loadPct, dTdt: 0.0, firmwarePl1W: 45.0);
    }

    private static void StaticProfileTable()
    {
        Check.Near(85.0, AutoCpuController.TargetCForProfile("silent"), 0.01, "静音档天花板 85°C");
        Check.Near(88.0, AutoCpuController.TargetCForProfile("balanced"), 0.01, "均衡档天花板 88°C");
        Check.Near(92.0, AutoCpuController.TargetCForProfile("performance"), 0.01, "性能档天花板 92°C");
        Check.Equal(200, AutoCpuController.EppForProfile("silent"), "静音档 EPP 200");
        Check.Equal(128, AutoCpuController.EppForProfile("balanced"), "均衡档 EPP 128");
        Check.Equal(0, AutoCpuController.EppForProfile("performance"), "性能档 EPP 0");
        Check.Near(1.15, AutoCpuController.Pl2Ratio("silent"), 0.001, "静音档 PL2 系数 1.15");
        Check.Near(1.35, AutoCpuController.Pl2Ratio("performance"), 0.001, "性能档 PL2 系数 1.35");
        Check.Near(55.0, AutoCpuController.FanMaxForProfile("silent"), 0.01, "静音档风扇演算上限 55%");
        Check.Near(100.0, AutoCpuController.FanMaxForProfile("performance"), 0.01, "性能档风扇演算上限 100%");
    }

    private static void ProfileParametersReachDecision()
    {
        // 同一物理状态、同一天花板，只换档位：EPP / Tau / PL2 必须不同
        var silent = Tick("silent", 88);
        var balanced = Tick("balanced", 88);
        var performance = Tick("performance", 88);

        Check.Equal(200, silent.Epp, "静音档 EPP 进入决策输出");
        Check.Equal(128, balanced.Epp, "均衡档 EPP 进入决策输出");
        Check.Equal(0, performance.Epp, "性能档 EPP 进入决策输出");

        Check.Near(8.0, silent.TauS, 0.01, "静音档 Tau 8s 进入决策输出");
        Check.Near(16.0, balanced.TauS, 0.01, "均衡档 Tau 16s 进入决策输出");
        Check.Near(32.0, performance.TauS, 0.01, "性能档 Tau 32s 进入决策输出");

        // 轻载分支：holdPl1 锚定实读固件 45W，PL2 = 45 × 档位系数
        Check.Near(51.75, silent.Pl2W, 0.05, "静音档 PL2 = 45W × 1.15");
        Check.Near(56.25, balanced.Pl2W, 0.05, "均衡档 PL2 = 45W × 1.25");
        Check.Near(60.75, performance.Pl2W, 0.05, "性能档 PL2 = 45W × 1.35");
        Check.True(silent.Pl2W < balanced.Pl2W && balanced.Pl2W < performance.Pl2W,
            "PL2 应随档位激进程度单调递增");

        // 轻载时 PL1 一律锚定固件实值：档位不该在空闲时抬高功耗墙
        Check.Near(45.0, silent.Pl1W, 0.01, "轻载 PL1 锚定固件 45W（静音）");
        Check.Near(45.0, performance.Pl1W, 0.01, "轻载 PL1 锚定固件 45W（性能）");
    }

    private static void CeilingDrivesThermalSuppression()
    {
        // 关键耦合：AutoCpuController.Update 用调用方的 ceilingC 覆盖档位自带天花板
        const double hot = 90, power = 45, load = 95;

        var tight = Tick("balanced", 85, hot, power, load);
        var loose = Tick("balanced", 92, hot, power, load);
        Check.Contains(tight.Reason, "温度上限抑制", "85°C 天花板下 90°C 应触发触顶降功耗");
        Check.False(loose.Reason.Contains("温度上限抑制"),
            "92°C 天花板下 90°C 不应触顶");

        // 静音档自带 85°C，但传入 92°C 时不得再用 85°C 判断
        var silentLoose = Tick("silent", 92, hot, power, load);
        Check.False(silentLoose.Reason.Contains("温度上限抑制"),
            "调用方 ceilingC 必须覆盖档位自带天花板（否则自定义天花板/标定推荐失效）");
        Check.True(tight.Pl1W <= loose.Pl1W,
            "天花板更低时决策 PL1 不应更高");
    }
}
