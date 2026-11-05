using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.Environment;

/// <summary>
/// 硬件约束推导器（Hardware Limit Resolver）
/// 将探测到的 CpuEnvironment 映射为决策树可用的 HardwareLimits。
///
/// 兼容策略（广泛兼容、宁保守勿激进）：
///   1. TjMax：识别到明确平台用平台值；未知平台一律保守按 100°C（个别老 Atom 会略低，但安全熔断只紧不松）。
///   2. PL1/PL2 上限：按产品形态 + 代数给出量级；未知形态回退到原 Intel Mobile 默认（65W/90W）。
///   3. 虚拟化环境：传感器/功耗寄存器可能失真，压缩功耗上限并保留告警。
///   4. 任何解析异常 → 回退 DefaultIntelMobile，保证程序始终可运行。
/// </summary>
public static class HardwareLimitResolver
{
    /// <summary>默认保守参数：与历史行为一致的 Intel Mobile 基线。</summary>
    public static HardwareLimits ConservativeFallback =>
        HardwareLimits.DefaultIntelMobile;

    public static HardwareLimits Resolve(CpuEnvironment env)
    {
        try
        {
            var limits = ResolveCore(env);

            // 虚拟化环境额外收紧：功耗遥测不可信，按平台自身上限的比例压制最大可写 PL。
            // 比例式收紧是通用规则；旧版写死 45W/65W 是从某台 i7-7700HQ 抄来的机型值，不适用于其它机器。
            if (env.IsVirtualized)
            {
                limits = limits with
                {
                    MaxPL1 = Math.Max(limits.MinPL1 + 5.0, limits.MaxPL1 * 0.75),
                    MaxPL2 = Math.Max(limits.MinPL2 + 5.0, limits.MaxPL2 * 0.75)
                };
            }

            // 物理不变式钳制：保证 Min < Max 且 PL2 空间 > PL1 空间
            return Clamp(limits);
        }
        catch
        {
            return ConservativeFallback;
        }
    }

    /// <summary>便捷入口：探测 + 推导一步到位。</summary>
    public static HardwareLimits ResolveForCurrentMachine() =>
        Resolve(HardwareProbe.Probe());

    // ---------------- 核心映射表 ----------------

    private static HardwareLimits ResolveCore(CpuEnvironment env)
    {
        double tjMax = ResolveTjMax(env);
        (double minPl1, double maxPl1, double minPl2, double maxPl2) = ResolvePowerEnvelope(env);

        return new HardwareLimits
        {
            TjMax = tjMax,
            MinPL1 = minPl1,
            MaxPL1 = maxPl1,
            MinPL2 = minPl2,
            MaxPL2 = maxPl2,
            // 安全天花板始终比 TjMax 低 5°C，给控制器留出熔断缓冲
            MaxSafeCeiling = tjMax - 5.0
        };
    }

    private static double ResolveTjMax(CpuEnvironment env)
    {
        switch (env.Vendor)
        {
            case CpuVendor.Intel:
                // Core Ultra（Meteor/Arrow/Lunar Lake）移动标压：TjMax 普遍 100~110，按 100 保守
                if (env.Generation >= 100)
                    return 100.0;
                // 12~14 代 HX 桌面级移动芯片存在 110°C 规格
                if (env.Generation >= 12 && env.Suffix is "HX" or "HXK")
                    return 110.0;
                // 6~11 代及普通移动/桌面：100°C
                return 100.0;

            case CpuVendor.Amd:
                // Ryzen 7000/9000 桌面 X 系 95°C，移动 HS/HX 常见 100°C；按后缀区分
                if (env.Suffix is "HX" or "HS" or "H")
                    return 100.0;
                return 95.0;

            case CpuVendor.Arm:
                // Apple Silicon / 骁龙 X：结温规格较高但不参与 x86 RAPL 体系，给保守 100
                return 100.0;

            default:
                return 100.0;
        }
    }

    private static (double MinPl1, double MaxPl1, double MinPl2, double MaxPl2) ResolvePowerEnvelope(CpuEnvironment env)
    {
        // 未知厂商/未知形态：回退历史默认（与7代 Intel Mobile 行为对齐）
        if (env.Vendor == CpuVendor.Unknown || env.FormFactor == CpuFormFactor.Unknown && env.Generation == 0)
            return (10.0, 65.0, 15.0, 90.0);

        switch (env.FormFactor)
        {
            case CpuFormFactor.MobileUltraLowPower:
                // U/Y 系列：cTDP 9~28W，允许有限上调空间
                return (7.0, 35.0, 10.0, 50.0);

            case CpuFormFactor.MobileMainstream:
                // P 系列：28W 基准
                return (10.0, 55.0, 15.0, 80.0);

            case CpuFormFactor.MobilePerformance:
                // H/HK 系列：45W 基准；HX / Core Ultra 标压可到 55~65W+ 基准、PL2 100W+
                if (env.Suffix is "HX" or "HXK")
                    return (15.0, 125.0, 20.0, 157.0);
                if (env.Generation >= 100) // Core Ultra H（Arrow Lake-H 等）PL2 显著高于老平台
                    return (15.0, 90.0, 20.0, 115.0);
                return (15.0, 65.0, 20.0, 90.0);

            case CpuFormFactor.Desktop:
                // 不锁倍频 K 系 125W 基准；普通桌面 65W
                if (env.Suffix is "K" or "KF" or "KS" or "X" or "XE")
                    return (25.0, 125.0, 35.0, 253.0);
                return (25.0, 65.0, 35.0, 125.0);

            case CpuFormFactor.Server:
                return (50.0, 205.0, 60.0, 300.0);

            default:
                // Intel + 有代数但后缀没识别出来：按代数给移动标压量级
                if (env.Vendor == CpuVendor.Intel && env.Generation > 0)
                    return (10.0, 65.0, 15.0, 90.0);
                return (10.0, 65.0, 15.0, 90.0);
        }
    }

    private static HardwareLimits Clamp(HardwareLimits limits)
    {
        double minPl1 = Math.Max(4.0, limits.MinPL1);
        double maxPl1 = Math.Max(minPl1 + 5.0, limits.MaxPL1);
        double minPl2 = Math.Max(minPl1, limits.MinPL2);
        double maxPl2 = Math.Max(maxPl1 + 5.0, limits.MaxPL2);

        return limits with
        {
            MinPL1 = minPl1,
            MaxPL1 = maxPl1,
            MinPL2 = minPl2,
            MaxPL2 = maxPl2
        };
    }
}
