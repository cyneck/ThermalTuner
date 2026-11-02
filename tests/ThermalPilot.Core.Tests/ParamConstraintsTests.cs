using ThermalPilot.Core;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 机理约束：哪些值物理上不能出现，哪些只是"不推荐"。
///
/// 判据来自硬件机理而不是产品偏好：
///   · TJmax 100℃ → 任何上限卡 95（留 5℃ 给保护）
///   · τ 在 MSR 0x610 是 log2 编码 → 只能取 2 的幂
///   · EPP 是 8 位 → 0..255
///   · PL2 &lt; PL1 会让 0x610 的 pl2 窗口失去意义
/// </summary>
public sealed class ParamConstraintsTests
{
    /// <summary>
    /// 基准配置必须开启 CPU 控制通道：Validate 里的 CPU 参数只在
    /// AutoCpuMode / EnableCpuTuning 打开时才校验（关掉时这些值属于"不改"）。
    /// </summary>
    private static ThermalConfig Good() => new()
    {
        ProfileName = "均衡",
        AutoCpuMode = true,
        AutoTargetC = 88,
        AmbientC = 24,
        FanMaxPct = 75,
        FanMinPct = 20,
        CriticalTempC = 95,
        CpuPl1W = 30,
        CpuPl2W = 38,
        CpuTauS = 16,
        CpuEpp = 128,
        CpuTurboRatio = 34,
        UndervoltMv = -50
    };

    private static bool Blocked(ThermalConfig c, string key, double r = 1.2)
        => ParamConstraints.Validate(c, r).Any(v => v.Key == key && v.Severity == ParamSeverity.Block);

    private static bool Warned(ThermalConfig c, string key, int maxTurbo = 0)
        => ParamConstraints.Validate(c, 1.2, maxTurbo).Any(v => v.Key == key && v.Severity == ParamSeverity.Warn);

    [Fact]
    public void 合法配置不应有任何阻断()
    {
        var v = ParamConstraints.Validate(Good(), 1.2);
        Assert.False(ParamConstraints.HasBlocking(v), string.Join("；", v));
    }

    [Theory]
    [InlineData(96)]    // 超过安全余量
    [InlineData(120)]
    [InlineData(0)]
    public void 温度上限越界必须阻断(double ceiling)
    {
        var c = Good();
        c.AutoTargetC = ceiling;
        Assert.True(Blocked(c, "Tceiling"));
    }

    [Fact]
    public void 安全线不允许超过_TJmax_余量()
    {
        var c = Good();
        c.CriticalTempC = 99;
        Assert.True(Blocked(c, "Critical"));
    }

    [Theory]
    [InlineData(3)]     // 不是 2 的幂：硬件会落到 2 或 4，只能警告（不能因为"表示不精确"就拒绝应用）
    [InlineData(7)]
    public void τ_非_2_的幂应警告(int tau)
    {
        var c = Good();
        c.CpuTauS = tau;
        Assert.True(Warned(c, "Tau"), $"τ={tau}s 应提示「无法精确表示」");
    }

    [Fact]
    public void τ_为_0_表示不改_不应报错()
    {
        var c = Good();
        c.CpuTauS = 0;   // 0 = 不改（哨兵值）
        var v = ParamConstraints.Validate(c, 1.2);
        Assert.DoesNotContain(v, x => x.Key == "Tau" && x.Severity == ParamSeverity.Block);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public void 合法的_τ_应通过(int tau)
    {
        var c = Good();
        c.CpuTauS = tau;
        Assert.False(Blocked(c, "Tau"), $"τ={tau}s 被误判为非法");
        Assert.False(Warned(c, "Tau"), $"τ={tau}s 不该有警告");
    }

    [Theory]
    [InlineData(256)]
    [InlineData(-5)]    // 只有 -1 表示「不改」，其它负数是填错
    public void EPP_超出_8_位必须阻断(int epp)
    {
        var c = Good();
        c.CpuEpp = epp;
        Assert.True(Blocked(c, "Epp"));
    }

    [Fact]
    public void EPP_为负表示不改_不应报错()
    {
        var c = Good();
        c.CpuEpp = -1;   // -1 = 不改
        Assert.False(Blocked(c, "Epp"));
    }

    [Fact]
    public void 风扇上限不能低于下限()
    {
        var c = Good();
        c.FanMinPct = 70;
        c.FanMaxPct = 50;
        var v = ParamConstraints.Validate(c, 1.2);
        Assert.True(ParamConstraints.HasBlocking(v), "风扇上下限倒挂竟然通过");
    }

    [Fact]
    public void PL2_小于_PL1_必须阻断()
    {
        var c = Good();
        c.CpuPl1W = 40;
        c.CpuPl2W = 30;
        var v = ParamConstraints.Validate(c, 1.2);
        Assert.True(ParamConstraints.HasBlocking(v), "PL2 < PL1 竟然通过");
    }

    [Fact]
    public void 睿频上限超过芯片规格应警告()
    {
        var c = Good();
        c.CpuTurboRatio = 60;   // 芯片只有 38：硬件会静默截断，不报错也不生效
        Assert.True(Warned(c, "Turbo", maxTurbo: 38));
    }

    [Fact]
    public void 热阻不可信时高温预测只警告不阻断()
    {
        var c = Good();
        c.AutoTargetC = 88;
        c.CpuPl1W = 45;    // 按 R=1.2、室温 24 预测 78℃，不会撞线；这里主要验证 R 不可信不产生 Block
        c.CpuPl2W = 56;

        var v = ParamConstraints.Validate(c, 1.2, rConfident: false);
        Assert.False(ParamConstraints.HasBlocking(v),
            "R 不可信时不应该阻断应用，否则用户换完硅脂就没法调参了：" + string.Join("；", v));
    }

    [Fact]
    public void 每个规格都必须有可读的机理说明()
    {
        foreach (var s in ParamConstraints.Specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label), $"{s.Key} 缺少标签");
            Assert.False(string.IsNullOrWhiteSpace(s.Why), $"{s.Key} 缺少机理说明（用户看不到「为什么是这个范围」）");
        }
    }
}
