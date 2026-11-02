using ThermalPilot.Core;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 温度抗毛刺滤波。用例直接取自真机采样里出现过的形态（lab-data/lab-deep1）：
/// 单点离群 47→85→52、交替毛刺 47→68→47→85、真实阶跃 41→96→97、掉载 96→58→66。
///
/// 这类问题只能靠"物理可能性"判断：温度涨 X℃ 必须有 X/R 瓦的功率跟着，没有就是读数问题。
/// </summary>
public sealed class TempFilterTests
{
    [Fact]
    public void 首帧应直接可用()
    {
        var f = new TempFilter();
        Assert.Equal(47, f.Push(47), 3);
    }

    [Fact]
    public void 单点毛刺必须被吃掉()
    {
        var f = new TempFilter();
        f.Push(47);
        double spike = f.Push(85);     // 真机出现过：功率几乎没变
        double after = f.Push(52);

        Assert.True(spike < 80, $"单点毛刺没滤掉，输出 {spike}");
        Assert.InRange(after, 50, 54);
    }

    [Fact]
    public void 交替毛刺必须被吃掉()
    {
        // 47 → 68 → 47 → 85 → 52：三点中值会漏（窗口 68,47,85 中值仍是 68），五点才吃得掉
        var f = new TempFilter();
        foreach (var v in new[] { 47.0, 68, 47, 85, 52 }) f.Push(v);

        Assert.True(f.Value < 60, $"交替毛刺没滤掉，末值 {f.Value}");
        Assert.InRange(f.Value, 50, 54);
    }

    [Fact]
    public void 真实升温阶跃必须跟上()
    {
        var f = new TempFilter();
        foreach (var v in new[] { 41.0, 41, 41, 96, 97 }) f.Push(v);
        double after = f.Push(98);

        Assert.True(after >= 96, $"真实升温被削弱到 {after}℃");
    }

    [Fact]
    public void 真实掉载降温必须跟上()
    {
        var f = new TempFilter();
        foreach (var v in new[] { 96.0, 96, 96 }) f.Push(v);
        foreach (var v in new[] { 58.0, 66, 52, 50 }) f.Push(v);

        Assert.True(f.Value < 80, $"真实降温被延迟过头，仍是 {f.Value}℃");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(200)]
    public void 无效读数不得污染窗口(double bad)
    {
        var f = new TempFilter();
        f.Push(47); f.Push(47); f.Push(47);
        f.Push(bad);

        Assert.Equal(47, f.Value, 3);
    }

    [Fact]
    public void 稳态值不得被改变()
    {
        var f = new TempFilter();
        for (int i = 0; i < 12; i++) f.Push(66);
        Assert.Equal(66, f.Value, 3);
    }

    [Theory]
    [InlineData(4, 5)]   // 偶数窗口自动 +1
    [InlineData(1, 3)]   // 过小窗口兜底为 3
    [InlineData(7, 7)]
    public void 窗口长度必须归一化为奇数(int request, int expect)
    {
        Assert.Equal(expect, new TempFilter(request).Window);
    }

    [Fact]
    public void 复位后应回到初始状态()
    {
        var f = new TempFilter();
        f.Push(47); f.Push(85); f.Push(52);
        f.Reset();

        Assert.True(double.IsNaN(f.Value));
        Assert.Equal(0, f.RejectedCount);
    }

    [Fact]
    public void 内建自检必须全部通过()
    {
        var errs = TempFilter.SelfTest();
        Assert.True(errs.Count == 0, string.Join("；", errs));
    }
}
