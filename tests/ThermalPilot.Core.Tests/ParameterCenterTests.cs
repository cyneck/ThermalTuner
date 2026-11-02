using ThermalPilot.Core;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// 参数中心：草稿 / 已应用 / 差异 / 提交 的三层语义。
///
/// 这一组用例的存在理由很具体：底部「应用（写入硬件）」按钮的可用性完全由
/// <see cref="ParameterCenter.HasPending"/> 决定（n>0 才 enable）。只要 Diff 与编辑脱节，
/// 按钮就会永久置灰 —— 面板上能改、改完没反应。所以这里把"任何一条编辑都必须产生待应用项"
/// 当成硬不变量来测。
/// </summary>
public sealed class ParameterCenterTests
{
    private static ThermalConfig Baseline() => new()
    {
        ProfileName = "均衡",
        ControlChannel = ControlChannels.Msr,
        AutoCpuMode = true,
        AutoTargetC = 88,
        AmbientC = 24,
        FanMaxPct = 75,
        FanMinPct = 20,
        CpuPl1W = 30,
        CpuPl2W = 38,
        CpuTauS = 16,
        CpuEpp = 128,
        CriticalTempC = 95
    };

    [Fact]
    public void 新建参数中心不应有待应用变更()
    {
        var pc = new ParameterCenter(Baseline());
        Assert.False(pc.HasPending);
        Assert.Equal(0, pc.PendingCount);
    }

    [Theory]
    [InlineData("pl1")]      // 高级页 CPU 精细微调
    [InlineData("pl2")]
    [InlineData("tceiling")] // 策略页
    [InlineData("fanmax")]
    public void 任一参数编辑都必须产生待应用项(string which)
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c =>
        {
            switch (which)
            {
                case "pl1": c.CpuPl1W = 32; break;
                case "pl2": c.CpuPl2W = 40; break;
                case "tceiling": c.AutoTargetC = 86; break;
                case "fanmax": c.FanMaxPct = 70; break;
            }
        });

        // 这条挂了 = 底部「应用（写入硬件）」会一直灰着，用户改什么都没法提交
        Assert.True(pc.HasPending, $"改了 {which} 却没有待应用变更");
        Assert.Equal(1, pc.PendingCount);
    }

    [Fact]
    public void 改回原值后待应用应归零()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.AutoTargetC = 86);
        Assert.Equal(1, pc.PendingCount);

        pc.Edit(c => c.AutoTargetC = 88);
        Assert.False(pc.HasPending);   // Diff 基于值，不是"点过没点过"
    }

    [Fact]
    public void 应用成功后草稿与已应用同步()
    {
        var pc = new ParameterCenter(Baseline()) { REstimate = 1.2 };
        pc.Edit(c => c.CpuPl1W = 32);

        var r = pc.Apply();

        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.AppliedCount);
        Assert.Equal(32, pc.Committed.CpuPl1W);
        Assert.False(pc.HasPending);
        Assert.Equal(pc.Committed.CpuPl1W, pc.Draft.CpuPl1W);
    }

    [Fact]
    public void 阻断级违规必须拒绝提交且不污染已应用值()
    {
        var pc = new ParameterCenter(Baseline()) { REstimate = 1.2 };
        pc.Edit(c => c.AutoTargetC = 99);   // 超过 TJmax 留 5℃ 余量

        var r = pc.Apply();

        Assert.False(r.Ok);
        Assert.NotEmpty(r.Violations);
        Assert.Equal(88, pc.Committed.AutoTargetC);   // 原值不动
        Assert.True(pc.HasPending);                    // 草稿还在，用户可以改回来
    }

    [Fact]
    public void 放弃草稿必须回到已应用值()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.AmbientC = 35);
        Assert.True(pc.HasPending);

        pc.Discard();

        Assert.Equal(24, pc.Draft.AmbientC);
        Assert.False(pc.HasPending);
    }

    [Fact]
    public void 强制提交应清空草稿差异()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.CpuPl1W = 35);

        var next = Baseline();
        next.CpuPl1W = 20;
        pc.ForceCommit(next);

        Assert.Equal(20, pc.Committed.CpuPl1W);
        Assert.False(pc.HasPending);
    }

    [Fact]
    public void 多项编辑的差异清单应完整()
    {
        var pc = new ParameterCenter(Baseline()) { REstimate = 1.2 };
        pc.Edit(c => { c.AutoTargetC = 86; c.FanMaxPct = 70; });

        var d = pc.Diff();

        Assert.Equal(2, d.Count);
        Assert.Contains(d, x => x.Key == "Tceiling" && x.From == "88℃" && x.To == "86℃");
        Assert.Contains(d, x => x.Key == "FanMax");
    }

    [Fact]
    public void 应用事件应携带提交后的快照()
    {
        var pc = new ParameterCenter(Baseline()) { REstimate = 1.2 };
        ThermalConfig? got = null;
        pc.Applied += c => got = c;

        pc.Edit(c => c.FanMaxPct = 65);
        var r = pc.Apply();

        Assert.True(r.Ok);
        Assert.NotNull(got);
        Assert.Equal(65, got!.FanMaxPct);
    }

    [Fact]
    public void 无变更时应用应返回零项而不是报错()
    {
        var pc = new ParameterCenter(Baseline()) { REstimate = 1.2 };
        var r = pc.Apply();

        Assert.True(r.Ok);
        Assert.Equal(0, r.AppliedCount);
        Assert.Contains("没有变更", r.Message);
    }

    [Fact]
    public void 草稿变更事件必须触发_否则界面不会刷新待应用条()
    {
        var pc = new ParameterCenter(Baseline());
        int hits = 0;
        pc.DraftChanged += () => hits++;

        pc.Edit(c => c.CpuEpp = 100);

        Assert.True(hits > 0);
    }

    // ---------------- 手调即让位 ----------------
    //
    // 智能自动每 3 秒重算 PL1/PL2/τ/EPP。手改这四项并应用后如果自动不关掉，
    // 3 秒后手调值就被顶掉 —— 用户看到"已应用"，实际根本没生效。

    [Fact]
    public void 手改_PL1_并应用时智能自动必须让位()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.CpuPl1W = 35);

        bool yielded = pc.YieldAutoForManualCpuEdit(manualCpuEdit: true);

        Assert.True(yielded);
        Assert.False(pc.Draft.AutoCpuMode);
        Assert.True(pc.Draft.EnableCpuTuning);   // 让位后手动值必须有通道写进硬件
    }

    [Theory]
    [InlineData("pl2")]
    [InlineData("tau")]
    [InlineData("epp")]
    public void 手改_PL2_τ_EPP_任意一项同样让位(string which)
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c =>
        {
            switch (which)
            {
                case "pl2": c.CpuPl2W = 45; break;
                case "tau": c.CpuTauS = 32; break;
                case "epp": c.CpuEpp = 64; break;
            }
        });

        Assert.True(pc.YieldAutoForManualCpuEdit(manualCpuEdit: true));
        Assert.False(pc.Draft.AutoCpuMode);
    }

    [Fact]
    public void 预设套用改到这四项不算手调_不让位()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.CpuPl2W = 42);   // 取向预设会改 PL2/τ/EPP，但那不是用户手调

        Assert.False(pc.YieldAutoForManualCpuEdit(manualCpuEdit: false));
        Assert.True(pc.Draft.AutoCpuMode);
    }

    [Fact]
    public void 手滑改回去_值与已应用一致时不让位()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.CpuPl1W = 35);
        pc.Edit(c => c.CpuPl1W = 30);   // 又改回原值，最终没有差异

        Assert.False(pc.YieldAutoForManualCpuEdit(manualCpuEdit: true));
        Assert.True(pc.Draft.AutoCpuMode);
    }

    [Fact]
    public void 智能自动本来关着就没有让位一说()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => { c.AutoCpuMode = false; c.EnableCpuTuning = true; });
        pc.Edit(c => c.CpuEpp = 64);

        Assert.False(pc.YieldAutoForManualCpuEdit(manualCpuEdit: true));
        Assert.False(pc.Draft.AutoCpuMode);
    }

    [Fact]
    public void 让位也要进待应用清单_用户看得见自动被关()
    {
        var pc = new ParameterCenter(Baseline());
        pc.Edit(c => c.CpuTauS = 32);
        pc.YieldAutoForManualCpuEdit(manualCpuEdit: true);

        var keys = pc.Diff().Select(x => x.Key).ToList();
        Assert.Contains("Tau", keys);
        Assert.Contains("Auto", keys);    // 智能自动 开→关
        Assert.Contains("Tune", keys);    // 手工调控 关→开
    }
}
