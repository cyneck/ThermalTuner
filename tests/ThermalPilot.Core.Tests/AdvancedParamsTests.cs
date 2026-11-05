using ThermalPilot.Core.Msr;
using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.Tests;

/// <summary>方案 B 新增：0x774 全位域、0x150 电压、CPU 能力预筛的纯函数测试。</summary>
public sealed class AdvancedParamsTests
{
    // 本机 i7-7700HQ 实读值：Min 28 / Max 38 / Desired 0 / EPP 0 / AW 414 / PkgCtl 0
    private const ulong FactoryHwp = 0x0000019E0000261CUL;

    public void RunAllTests()
    {
        Check.Suite("AdvancedParams");

        // ---- 0x774 解码 ----
        var d = HwpRequest.Decode(FactoryHwp);
        Check.Equal(28, d.MinPerf, "出厂 Min Perf 解码");
        Check.Equal(38, d.MaxPerf, "出厂 Max Perf 解码");
        Check.Equal(0, d.DesiredPerf, "出厂 Desired Perf = 硬件自主");
        Check.Equal(0, d.Epp, "出厂 EPP = 性能优先");
        Check.Equal(414, d.ActivityWindow, "出厂 Activity Window");
        Check.False(d.PackageControl, "出厂 Package Control 未置位");

        // ---- 读-改-写：只改指定字段，Desired / PkgCtl 永不改写 ----
        ulong target = HwpRequest.Encode(FactoryHwp, 20, 34, 128, 100);
        var t = HwpRequest.Decode(target);
        Check.Equal(20, t.MinPerf, "Min Perf 已改写");
        Check.Equal(34, t.MaxPerf, "Max Perf 已改写");
        Check.Equal(128, t.Epp, "EPP 已改写");
        Check.Equal(100, t.ActivityWindow, "Activity Window 已改写");
        Check.Equal(0, t.DesiredPerf, "Desired Perf 必须保持原值");
        Check.False(t.PackageControl, "Package Control 必须保持原值");

        // ---- -1 = 保持原值，且高 22 位不受影响 ----
        ulong onlyEpp = HwpRequest.Encode(FactoryHwp, epp: 128);
        Check.Equal(28, HwpRequest.Decode(onlyEpp).MinPerf, "-1 时 Min Perf 保持");
        Check.Equal(414, HwpRequest.Decode(onlyEpp).ActivityWindow, "-1 时 AW 保持");
        Check.Equal(128, HwpRequest.Decode(onlyEpp).Epp, "只改 EPP");
        Check.True((onlyEpp >> 42) == (FactoryHwp >> 42), "bit42 以上保持不变");

        // ---- 范围约束 ----
        Check.True(HwpRequest.Validate(20, 34, 128, 100, 38) is null, "合法组合通过");
        Check.Contains(HwpRequest.Validate(40, 34, 128, 100, 38) ?? "", "不能大于", "Min > Max 被拒绝");
        Check.Contains(HwpRequest.Validate(20, 44, 128, 100, 38) ?? "", "出厂上限", "Max > 出厂上限被拒绝（不做超频）");
        Check.Contains(HwpRequest.Validate(20, 34, 256, 100, 38) ?? "", "EPP", "EPP 越界被拒绝");
        Check.Contains(HwpRequest.Validate(20, 34, 128, 1024, 38) ?? "", "Activity", "AW 越界被拒绝");

        // ---- 0x150 电压 ----
        Check.True(FivrVoltage.Validate(-150) is null, "-150mV 合法");
        Check.True(FivrVoltage.Validate(0) is null, "0mV（回零）合法");
        Check.Contains(FivrVoltage.Validate(1) ?? "", "禁止加压", "加压被拒绝");
        Check.Contains(FivrVoltage.Validate(-151) ?? "", "必须", "超过 -150mV 被拒绝");
        Check.Equal(-150, FivrVoltage.Clamp(-999), "Clamp 下限");
        Check.Equal(0, FivrVoltage.Clamp(999), "Clamp 上限");
        Check.Equal(0UL, FivrVoltage.Zero, "回零指令为 0");

        ulong cmd = FivrVoltage.Encode(-100);
        int? decoded = FivrVoltage.DecodeOffsetMv(cmd);
        Check.True(decoded is not null && Math.Abs(decoded.Value - (-100)) <= 2,
            $"电压编码往返 -100mV（实际 {decoded}）");
        Check.True(FivrVoltage.ResponseMatches(cmd, cmd | 0x1F), "OC Mailbox 高位匹配判据");
        Check.False(FivrVoltage.ResponseMatches(cmd, 0), "全 0 响应不算成功");
        Check.Equal(2, FivrVoltage.EnableVariants, "两种写使能编码都尝试");

        // ---- CPU 能力预筛（型号只是提示，不编造能力） ----
        var kaby = CpuCapability.Probe("Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz");
        Check.Equal("Intel", kaby.Vendor, "厂商 = Intel");
        Check.Equal(7, kaby.Generation, "7700HQ = 第 7 代");
        Check.True(kaby.IsMobile, "HQ = 移动版");
        Check.False(kaby.IsUnlocked, "非 K 不解锁倍频");
        Check.True(kaby.VoltageOffsetPlausible, "7 代 OC Mailbox 预筛为可能可用");
        Check.False(kaby.TurboRatioMayBeEffective, "非 K 睿频档位不承诺有效");

        var ice = CpuCapability.Probe("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz");
        Check.Equal(10, ice.Generation, "10750H = 第 10 代");
        Check.False(ice.VoltageOffsetPlausible, "10 代预筛为电压大概率不可用");
        Check.Contains(ice.VoltageHint, "Plundervolt", "给出 Plundervolt 微码提示");

        var amd = CpuCapability.Probe("AMD Ryzen 7 5800H with Radeon Graphics");
        Check.Equal("AMD", amd.Vendor, "厂商 = AMD");
        Check.False(amd.VoltageOffsetPlausible, "AMD 不适用 0x150");
        Check.Contains(amd.VoltageHint, "Curve Optimizer", "AMD 提示改用 BIOS 方案");

        var unknown = CpuCapability.Probe("未知 CPU");
        Check.Equal(0, unknown.Generation, "未知型号代际为 0");
        Check.Contains(unknown.VoltageHint, "实测", "未知型号要求实测而非硬编码禁止");
        Check.Contains(kaby.MatrixText, "预筛", "能力矩阵明确标注为预筛");
    }
}
