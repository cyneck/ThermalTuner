using ThermalPilot.Core;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 控制通道互斥：同一时刻只能有一个通道拥有 CPU 频率的控制权。
///
/// 为什么必须测：三套控制器（电源计划 / MSR / 策略模型）同时为真时互相覆盖，
/// 现象是「温度升了、功率升了、频率反而降了」——这种 bug 在界面上看起来像传感器坏了。
/// </summary>
public sealed class ControlChannelsTests
{
    [Theory]
    [InlineData(ControlChannels.Msr)]
    [InlineData(ControlChannels.PowerCfg)]
    [InlineData(ControlChannels.Policy)]
    public void 归一化后只能有一个通道生效(string channel)
    {
        var c = new ThermalConfig
        {
            ControlChannel = channel,
            EnableFrequencyControl = true,   // 三个都先打开，看归一化能不能收拢
            EnableCpuTuning = true,
            AutoCpuMode = true,
            UsePolicyModel = true
        };

        ControlChannels.Normalize(c);

        int live = 0;
        if (c.EnableFrequencyControl) live++;
        if (c.EnableCpuTuning || c.AutoCpuMode) live++;
        if (c.UsePolicyModel) live++;

        Assert.Equal(1, live);
        Assert.Equal(channel, c.ControlChannel);
    }

    [Fact]
    public void 切到电源计划必须关掉_MSR_与策略()
    {
        var c = new ThermalConfig { EnableCpuTuning = true, AutoCpuMode = true, UsePolicyModel = true };
        ControlChannels.Normalize(c);
        c.ControlChannel = ControlChannels.PowerCfg;
        ControlChannels.Normalize(c);

        Assert.True(c.EnableFrequencyControl);
        Assert.False(c.EnableCpuTuning);
        Assert.False(c.AutoCpuMode);
        Assert.False(c.UsePolicyModel);
    }

    [Fact]
    public void 切到策略模型必须关掉另外两套()
    {
        var c = new ThermalConfig { ControlChannel = ControlChannels.Policy, EnableCpuTuning = true, EnableFrequencyControl = true };

        ControlChannels.Normalize(c);

        Assert.True(c.UsePolicyModel);
        Assert.False(c.EnableCpuTuning);
        Assert.False(c.EnableFrequencyControl);
        Assert.False(c.AutoCpuMode);
    }

    [Fact]
    public void MSR_通道下自动与手工二选一()
    {
        var c = new ThermalConfig
        {
            ControlChannel = ControlChannels.Msr,
            AutoCpuMode = true,
            EnableCpuTuning = true
        };

        ControlChannels.Normalize(c);

        Assert.False(c.EnableCpuTuning);   // 自动优先，手工关掉
        Assert.False(c.EnableFrequencyControl);
        Assert.False(c.UsePolicyModel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-channel")]
    public void 未知通道应兜底到_MSR(string? bad)
    {
        var c = new ThermalConfig { ControlChannel = bad ?? "" };
        ControlChannels.Normalize(c);
        Assert.Equal(ControlChannels.Msr, c.ControlChannel);
    }

    [Fact]
    public void 通道标签必须可读()
    {
        Assert.Equal("MSR 精细调控", ControlChannels.Label(ControlChannels.Msr));
        Assert.Equal("电源计划限频", ControlChannels.Label(ControlChannels.PowerCfg));
        Assert.Equal("实测策略模型", ControlChannels.Label(ControlChannels.Policy));
    }
}
