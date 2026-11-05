using ThermalPilot.Core;
using ThermalPilot.Core.Environment;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 平台探测推导、用户档位、参数防呆、控制器边界钳制的功能测试。
/// 覆盖"换任意 CPU 仅需重新探测"这条承诺：不同厂商/代数/形态必须推导出合理且自洽的约束。
/// </summary>
public class PlatformProfileTests
{
    public void RunAllTests()
    {
        Check.Suite("PlatformProfile");
        Test_Resolver_IntelKabyLakeMobile();
        Test_Resolver_ArrowLakeH();
        Test_Resolver_HxSeries();
        Test_Resolver_UltraLowPower();
        Test_Resolver_DesktopUnlocked();
        Test_Resolver_AmdDesktop();
        Test_Resolver_VirtualizedIsTighter();
        Test_Resolver_UnknownFallsBack();
        Test_Resolver_InvariantsHold();
        Test_Profiles_AreOrdered();
        Test_Profile_FromNameFallbacks();
        Test_Constraints_BlocksAndWarns();
        Test_Controller_ClampsToHardwareBounds();
        Test_Controller_ClampsDownToMinimum();
        Test_Controller_ReceiptIsExposed();
    }

    private static CpuEnvironment Env(
        CpuVendor vendor, int gen, string suffix, CpuFormFactor form,
        bool vm = false, bool hybrid = false) => new()
    {
        BrandString = "test",
        Vendor = vendor,
        Generation = gen,
        Suffix = suffix,
        FormFactor = form,
        IsVirtualized = vm,
        IsHybridArchitecture = hybrid,
        PhysicalCores = 4,
        LogicalCores = 8,
        OsPlatform = "Windows"
    };

    private void Test_Resolver_IntelKabyLakeMobile()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 7, "HQ", CpuFormFactor.MobilePerformance));

        Check.Near(100, hw.TjMax, 1e-9, "7 代移动 TjMax 应为 100°C");
        Check.Near(95, hw.MaxSafeCeiling, 1e-9, "安全天花板应为 TjMax-5");
        Check.Near(15, hw.MinPL1, 1e-9, "H 系列 PL1 下界 15W");
        Check.Near(65, hw.MaxPL1, 1e-9, "7 代 H 系列 PL1 上界 65W");
        Check.Near(90, hw.MaxPL2, 1e-9, "7 代 H 系列 PL2 上界 90W");
    }

    private void Test_Resolver_ArrowLakeH()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 200, "H", CpuFormFactor.MobilePerformance, hybrid: true));

        Check.Near(90, hw.MaxPL1, 1e-9, "Core Ultra H 的 PL1 上界应放宽到 90W");
        Check.Near(115, hw.MaxPL2, 1e-9, "Core Ultra H 的 PL2 上界应放宽到 115W");
        Check.Near(100, hw.TjMax, 1e-9, "Core Ultra 移动 TjMax 按 100°C 保守取值");
    }

    private void Test_Resolver_HxSeries()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 13, "HX", CpuFormFactor.MobilePerformance, hybrid: true));

        Check.Near(110, hw.TjMax, 1e-9, "12 代+ HX 规格为 110°C");
        Check.Near(105, hw.MaxSafeCeiling, 1e-9, "HX 安全天花板应为 105°C");
        Check.Near(125, hw.MaxPL1, 1e-9, "HX PL1 上界 125W");
        Check.Near(157, hw.MaxPL2, 1e-9, "HX PL2 上界 157W");
    }

    private void Test_Resolver_UltraLowPower()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 11, "U", CpuFormFactor.MobileUltraLowPower));

        Check.Near(7, hw.MinPL1, 1e-9, "U 系列 PL1 下界 7W");
        Check.Near(35, hw.MaxPL1, 1e-9, "U 系列 PL1 上界 35W");
        Check.Near(50, hw.MaxPL2, 1e-9, "U 系列 PL2 上界 50W");
    }

    private void Test_Resolver_DesktopUnlocked()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 13, "K", CpuFormFactor.Desktop));

        Check.Near(25, hw.MinPL1, 1e-9, "桌面 K 系 PL1 下界 25W");
        Check.Near(125, hw.MaxPL1, 1e-9, "桌面 K 系 PL1 上界 125W");
        Check.Near(253, hw.MaxPL2, 1e-9, "桌面 K 系 PL2 上界 253W");
    }

    private void Test_Resolver_AmdDesktop()
    {
        var hw = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Amd, 0, "", CpuFormFactor.Desktop));

        Check.Near(95, hw.TjMax, 1e-9, "AMD 桌面非 H 后缀 TjMax 按 95°C");
        Check.Near(90, hw.MaxSafeCeiling, 1e-9, "AMD 桌面安全天花板 90°C");
    }

    private void Test_Resolver_VirtualizedIsTighter()
    {
        var bare = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 7, "HQ", CpuFormFactor.MobilePerformance));
        var vm = HardwareLimitResolver.Resolve(
            Env(CpuVendor.Intel, 7, "HQ", CpuFormFactor.MobilePerformance, vm: true));

        // 虚拟化收紧是通用比例规则，不能绑定某台机器的 45W / 65W
        Check.True(vm.MaxPL1 < bare.MaxPL1,
            $"虚拟化环境 PL1 上界必须比裸机更紧（裸机 {bare.MaxPL1}，虚拟化 {vm.MaxPL1}）");
        Check.True(vm.MaxPL2 < bare.MaxPL2,
            $"虚拟化环境 PL2 上界必须比裸机更紧（裸机 {bare.MaxPL2}，虚拟化 {vm.MaxPL2}）");
        Check.True(vm.MaxPL1 <= bare.MaxPL1, "虚拟化约束不得比裸机更宽");
        Check.True(vm.MinPL1 < vm.MaxPL1 && vm.MaxPL1 < vm.MaxPL2,
            "收紧后仍须保持 MinPL1 < MaxPL1 < MaxPL2 物理不变式");
    }

    private void Test_Resolver_UnknownFallsBack()
    {
        var hw = HardwareLimitResolver.Resolve(Env(CpuVendor.Unknown, 0, "", CpuFormFactor.Unknown));

        Check.Near(65, hw.MaxPL1, 1e-9, "未知平台回退 65W PL1 基线");
        Check.Near(90, hw.MaxPL2, 1e-9, "未知平台回退 90W PL2 基线");
        Check.Near(100, hw.TjMax, 1e-9, "未知平台 TjMax 保守取 100°C");
    }

    private void Test_Resolver_InvariantsHold()
    {
        var cases = new[]
        {
            Env(CpuVendor.Intel, 7, "HQ", CpuFormFactor.MobilePerformance),
            Env(CpuVendor.Intel, 200, "H", CpuFormFactor.MobilePerformance, hybrid: true),
            Env(CpuVendor.Intel, 13, "HX", CpuFormFactor.MobilePerformance),
            Env(CpuVendor.Intel, 11, "U", CpuFormFactor.MobileUltraLowPower),
            Env(CpuVendor.Intel, 13, "K", CpuFormFactor.Desktop),
            Env(CpuVendor.Amd, 0, "H", CpuFormFactor.MobilePerformance),
            Env(CpuVendor.Arm, 0, "", CpuFormFactor.MobileUltraLowPower),
            Env(CpuVendor.Unknown, 0, "", CpuFormFactor.Unknown),
            Env(CpuVendor.Intel, 0, "", CpuFormFactor.Unknown)
        };

        foreach (var env in cases)
        {
            var hw = HardwareLimitResolver.Resolve(env);
            string tag = env.Vendor + "/" + env.FormFactor;
            Check.True(hw.MinPL1 >= 4.0, $"{tag}: MinPL1 不应低于 4W");
            Check.True(hw.MaxPL1 >= hw.MinPL1 + 5.0, $"{tag}: MaxPL1 应比 MinPL1 至少高 5W");
            Check.True(hw.MaxPL2 >= hw.MaxPL1 + 5.0, $"{tag}: MaxPL2 应比 MaxPL1 至少高 5W");
            Check.Near(hw.TjMax - 5.0, hw.MaxSafeCeiling, 1e-9, $"{tag}: 安全天花板应为 TjMax-5");
        }
    }

    private void Test_Profiles_AreOrdered()
    {
        var silent = UserProfile.Silent();
        var balanced = UserProfile.Balanced();
        var perf = UserProfile.Performance();

        Check.True(silent.TargetTemperatureCeiling < balanced.TargetTemperatureCeiling, "静音天花板应低于平衡");
        Check.True(balanced.TargetTemperatureCeiling < perf.TargetTemperatureCeiling, "平衡天花板应低于性能");
        Check.True(silent.FanMaxPercent < balanced.FanMaxPercent, "静音风扇上限应低于平衡");
        Check.True(balanced.FanMaxPercent < perf.FanMaxPercent, "平衡风扇上限应低于性能");
        Check.True(silent.Epp > balanced.Epp, "静音 EPP 应更偏节能（数值更大）");
        Check.True(balanced.Epp > perf.Epp, "性能 EPP 应更偏性能（数值更小）");
        Check.True(silent.Pl2Ratio < balanced.Pl2Ratio, "静音 PL2 倍率应更保守");
        Check.True(balanced.Pl2Ratio < perf.Pl2Ratio, "性能 PL2 倍率应更激进");
        Check.True(silent.TauSeconds < balanced.TauSeconds, "静音 Tau 应更短");
        Check.True(balanced.TauSeconds < perf.TauSeconds, "性能 Tau 应更长");
    }

    private void Test_Profile_FromNameFallbacks()
    {
        Check.Equal("silent", UserProfile.FromName("silent").Name, "silent 应命中静音档");
        Check.Equal("performance", UserProfile.FromName("PERFORMANCE").Name, "档位名应大小写不敏感");
        Check.Equal("balanced", UserProfile.FromName("unknown").Name, "未知档位应回退 balanced");
        Check.Equal("balanced", UserProfile.FromName("").Name, "空档位应回退 balanced");
        Check.Near(92, AutoCpuController.TargetCForProfile("performance"), 1e-9, "性能档天花板 92°C");
        Check.Near(85, AutoCpuController.TargetCForProfile("silent"), 1e-9, "静音档天花板 85°C");
        Check.Equal(0, AutoCpuController.EppForProfile("performance"), "性能档 EPP=0");
    }

    private void Test_Constraints_BlocksAndWarns()
    {
        var hw = HardwareLimits.DefaultIntelMobile;

        var badPl1 = ParamConstraints.Validate(70, 80, 16, 128, 88, hw);
        Check.True(badPl1.Any(v => v.Limit == BindingLimit.Pl1Range && v.Severity == ParamSeverity.Block),
            "PL1 超范围应 Block");

        var badPl2 = ParamConstraints.Validate(45, 95, 16, 128, 88, hw);
        Check.True(badPl2.Any(v => v.Limit == BindingLimit.Pl2Range && v.Severity == ParamSeverity.Block),
            "PL2 超范围应 Block");

        var badEpp = ParamConstraints.Validate(45, 56, 16, 300, 88, hw);
        Check.True(badEpp.Any(v => v.Limit == BindingLimit.EppRange && v.Severity == ParamSeverity.Block),
            "EPP 超范围应 Block");

        var hotCeiling = ParamConstraints.Validate(45, 56, 16, 128, 99, hw);
        Check.True(hotCeiling.Any(v => v.Limit == BindingLimit.TempCeiling && v.Severity == ParamSeverity.Warn),
            "天花板超安全值应 Warn 而非 Block");

        var legalBigTau = ParamConstraints.Validate(45, 56, 28672, 128, 88, hw);
        Check.True(legalBigTau.Any(v => v.Key == "Tau" && v.Severity == ParamSeverity.Warn),
            "28672s 是合法硬件编码，应 Warn 而不是 Block");
        Check.False(legalBigTau.Any(v => v.Key == "Tau" && v.Severity == ParamSeverity.Block),
            "合法编码不得被 Block");

        var illegalTau = ParamConstraints.Validate(45, 56, 9, 128, 88, hw);
        Check.True(illegalTau.Any(v => v.Key == "Tau" && v.Severity == ParamSeverity.Block),
            "非法 Tau 编码应 Block");

        var clean = ParamConstraints.Validate(45, 56.25, 16, 128, 88, hw);
        Check.Equal(0, clean.Count, "合法参数不应产生任何告警");
    }

    private void Test_Controller_ClampsToHardwareBounds()
    {
        var limits = HardwareLimits.DefaultIntelMobile with
        {
            MinPL1 = 15,
            MaxPL1 = 50,
            MaxPL2 = 70,
            MaxSafeCeiling = 95
        };
        var c = new AutoCpuController(limits);

        for (int i = 0; i < 20; i++)
        {
            c.Update(tempC: 60, powerW: 40, ceilingC: 92, ambientC: 25,
                profile: "performance", loadPct: 95, dTdt: 0.2,
                perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8 });
        }

        var dec = c.Update(tempC: 60, powerW: 40, ceilingC: 92, ambientC: 25,
            profile: "performance", loadPct: 95, dTdt: 0.2,
            perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8 });

        Check.True(dec.Pl1W <= limits.MaxPL1 + 1e-9,
            $"决策 PL1 {dec.Pl1W:F1}W 不得超过平台上限 {limits.MaxPL1}W");
        Check.True(dec.Pl1W >= limits.MinPL1 - 1e-9,
            $"决策 PL1 {dec.Pl1W:F1}W 不得低于平台下限 {limits.MinPL1}W");
        Check.True(dec.Pl2W <= limits.MaxPL2 + 1e-9,
            $"决策 PL2 {dec.Pl2W:F1}W 不得超过平台上限 {limits.MaxPL2}W");
    }

    private void Test_Controller_ClampsDownToMinimum()
    {
        var limits = HardwareLimits.DefaultIntelMobile with { MinPL1 = 15, MaxPL1 = 50 };
        var c = new AutoCpuController(limits);

        // PROCHOT 连续触发 20 拍：每次 -15W，必须停在下限而不是继续往下掉
        for (int i = 0; i < 20; i++)
        {
            c.Update(tempC: 99, powerW: 45, ceilingC: 92, ambientC: 25,
                profile: "performance", loadPct: 99, dTdt: 1.0,
                perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8, Prochot = true });
        }

        var dec = c.Update(tempC: 99, powerW: 45, ceilingC: 92, ambientC: 25,
            profile: "performance", loadPct: 99, dTdt: 1.0,
            perf: new PerfSample { CoreClocksGhz = 3.0, MaxTurboGhz = 3.8, Prochot = true });

        Check.True(dec.Pl1W >= limits.MinPL1 - 1e-9,
            $"紧急降功耗不得跌破平台下限 {limits.MinPL1}W（实际 {dec.Pl1W:F1}W）");
        Check.Equal((int)BindingLimit.ThermalLimit, (int)dec.Limit, "PROCHOT 下受限类型应为 ThermalLimit");
    }

    private void Test_Controller_ReceiptIsExposed()
    {
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        c.Update(tempC: 55, powerW: 12, ceilingC: 88, ambientC: 25,
            profile: "balanced", loadPct: 10, dTdt: 0.0,
            firmwarePl1W: 45.0);

        var receipt = c.LatestReceipt;
        Check.True(receipt is not null, "每拍都应产出决策凭证");
        if (receipt is null) return;

        Check.Equal("LightLoadFilter", receipt.TriggeredRuleName, "10% 负载应命中轻载过滤");
        Check.True(receipt.EvaluatedPath.Count >= 1, "凭证应记录求值路径");
        Check.True(receipt.CausalReason.Length > 0, "凭证应带可读因果依据");
        Check.Near(45.0, receipt.Action.TargetPL1W, 1e-9, "轻载应回到固件 45W");
    }
}
