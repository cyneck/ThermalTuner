using System.Diagnostics;
using ThermalTuner.Core.Stress;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 场景化压测与「实读默认功率」基准的功能测试。
/// 重点守住两条通用产品约束：不写死任何机型瓦数、压测场景覆盖足够多的执行单元。
/// </summary>
public sealed class StressScenarioTests
{
    public void RunAllTests()
    {
        Check.Suite("StressScenario");
        Test_LadderCoversAllKernels();
        Test_LadderScalesWithCores();
        Test_Pl1LadderAnchoredOnMeasuredDefault();
        Test_Pl1LadderIsGeneric();
        Test_KernelParsing();
        Test_CancelledWorkerExitsImmediately();
        Test_MeasuredPl1Baseline();
    }

    private void Test_LadderCoversAllKernels()
    {
        var ladder = StressScenarioCatalog.StandardLadder(8, 12);
        Check.True(ladder.Count >= 12, $"标准阶梯应至少 12 个场景（实际 {ladder.Count}）");
        Check.True(ladder[0].Kernel == StressKernel.Idle, "首场景必须是空载基线");
        Check.True(ladder[^1].Kernel == StressKernel.Idle, "末场景必须是冷却段");

        var kernels = ladder.Select(s => s.Kernel).ToHashSet();
        foreach (var k in Enum.GetValues<StressKernel>())
            Check.True(kernels.Contains(k), $"标准阶梯应覆盖 {k} 内核");

        Check.True(ladder.Any(s => s.Kernel == StressKernel.SimdFma && s.Threads == 8),
            "应包含全核 SIMD/FMA 最重场景");
        Check.True(ladder.Any(s => s.Kernel == StressKernel.Pulse && s.DutyCycle < 1.0),
            "应包含带占空比的脉冲场景");
        Check.True(ladder.All(s => s.DurationSeconds > 0), "每个场景时长必须为正");
    }

    private void Test_LadderScalesWithCores()
    {
        var small = StressScenarioCatalog.StandardLadder(2, 10);
        var large = StressScenarioCatalog.StandardLadder(32, 10);
        Check.True(small.All(s => s.Threads <= 2), "2 核机器场景线程数不得超过 2");
        Check.True(large.All(s => s.Threads <= 32), "32 核机器场景线程数不得超过 32");
        Check.True(large.Max(s => s.Threads) > small.Max(s => s.Threads),
            "场景线程规模必须随逻辑核数放大，不能写死");
        Check.True(small.All(s => s.Threads >= 1 || s.Kernel == StressKernel.Idle),
            "负载场景至少 1 个线程");
    }

    private void Test_Pl1LadderAnchoredOnMeasuredDefault()
    {
        var steps = StressScenarioCatalog.Pl1Ladder(45.0, 10.0, 65.0);
        Check.True(steps.Count >= 5, $"以实读 45W 为锚应产生多档（实际 {steps.Count}）");
        Check.Near(45.0, steps[0], 0.01, "首档必须等于实读默认功率本身");
        Check.True(steps.Any(v => v < 45.0), "应包含降墙档");
        Check.True(steps.Any(v => v > 45.0), "应包含放宽档");
        Check.True(steps.All(v => v >= 10.0 && v <= 65.0), "所有档位必须钳制在硬件合法区间");
        Check.True(steps.Distinct().Count() == steps.Count, "档位必须去重");
        Check.True(StressScenarioCatalog.Pl1Ladder(double.NaN, 10, 65).Count == 0,
            "实读基准不可用时不得凭空造档位");
    }

    private void Test_Pl1LadderIsGeneric()
    {
        var laptop = StressScenarioCatalog.Pl1Ladder(28.0, 7.0, 55.0);
        var desktop = StressScenarioCatalog.Pl1Ladder(125.0, 25.0, 205.0);
        Check.Near(28.0, laptop[0], 0.01, "低功率机器首档为其自身实读值");
        Check.Near(125.0, desktop[0], 0.01, "高功率机器首档为其自身实读值");
        Check.True(desktop.Max() > laptop.Max() * 2,
            "同一套比例规则在不同机器上必须给出不同档位（不含机型常量）");
        Check.True(laptop.All(v => v >= 7.0 && v <= 55.0), "低功率机器档位受其自身区间钳制");
        Check.True(desktop.All(v => v >= 25.0 && v <= 205.0), "高功率机器档位受其自身区间钳制");
    }

    private void Test_KernelParsing()
    {
        Check.True(StressScenarioCatalog.TryParseKernel("simdfma", out var a) &&
                   a == StressKernel.SimdFma, "内核名应支持大小写不敏感解析");
        Check.True(StressScenarioCatalog.TryParseKernel("MemoryStream", out var b) &&
                   b == StressKernel.MemoryStream, "应能解析内存带宽内核");
        Check.False(StressScenarioCatalog.TryParseKernel("NotAKernel", out _), "非法内核名必须拒绝");
        Check.False(StressScenarioCatalog.TryParseKernel(null, out _), "空内核名必须拒绝");
    }

    private void Test_CancelledWorkerExitsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        StressWorker.Run(StressKernel.Idle, 1.0, cts.Token);
        StressWorker.Run(StressKernel.ScalarInt, 1.0, cts.Token);
        StressWorker.Run(StressKernel.ScalarFloat, 1.0, cts.Token);
        StressWorker.Run(StressKernel.SimdFma, 1.0, cts.Token);
        StressWorker.Run(StressKernel.MemoryStream, 1.0, cts.Token);
        StressWorker.Run(StressKernel.Mixed, 1.0, cts.Token);
        StressWorker.Run(StressKernel.Pulse, 0.7, cts.Token);
        sw.Stop();
        Check.True(sw.ElapsedMilliseconds < 1000,
            $"已取消的 token 应让所有内核立即退出（实际 {sw.ElapsedMilliseconds} ms）");
    }

    private void Test_MeasuredPl1Baseline()
    {
        var c = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        Check.False(c.HasMeasuredPl1, "未注入实读值时必须标记为「未测量」");
        Check.IsNaN(c.MeasuredDefaultPl1W, "未注入时默认功率为 NaN，不得回退到机型常量");

        c.SetCurrentPl1W(45.0);
        Check.True(c.HasMeasuredPl1, "注入 0x610 实读值后应标记为已测量");
        Check.Near(45.0, c.MeasuredDefaultPl1W, 1e-9, "默认功率应等于注入的实读值");

        c.SetCurrentPl1W(double.NaN);
        Check.Near(45.0, c.MeasuredDefaultPl1W, 1e-9, "非法读数不得污染已建立的实读基准");

        c.Reset();
        Check.Near(45.0, c.MeasuredDefaultPl1W, 1e-9,
            "Reset 只重置热模型，不得丢掉实读默认功率基准");

        var other = new AutoCpuController(HardwareLimits.DefaultIntelMobile);
        other.SetCurrentPl1W(125.0);
        Check.Near(125.0, other.MeasuredDefaultPl1W, 1e-9,
            "另一台机器的默认功率应取其自身实读值");
    }
}
