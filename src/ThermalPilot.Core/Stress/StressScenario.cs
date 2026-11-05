namespace ThermalPilot.Core.Stress;

/// <summary>
/// 压测负载内核。不同执行单元在真实 CPU 上的功耗特征差异很大：
/// 只用一种标量循环会严重低估机器的可达功耗，因此压测必须覆盖多种内核。
/// </summary>
public enum StressKernel
{
    /// <summary>空载基线（不施加负载）。</summary>
    Idle,

    /// <summary>整数 ALU / 乘法 / 位运算，中低功耗。</summary>
    ScalarInt,

    /// <summary>标量双精度浮点（除法、开方），中功耗。</summary>
    ScalarFloat,

    /// <summary>AVX2/FMA 向量浮点乘加，通常是 x86 上功耗最高的内核。</summary>
    SimdFma,

    /// <summary>大块内存顺序读写，压内存带宽与缓存层次。</summary>
    MemoryStream,

    /// <summary>整数 / 浮点 / SIMD / 内存按拍轮换，模拟真实混合负载。</summary>
    Mixed,

    /// <summary>按占空比在满载与休眠间切换，制造瞬态与热冲击。</summary>
    Pulse
}

/// <summary>
/// 一个可复现的压测场景。场景本身不含任何机型功率常量，
/// 线程数与时长由调用方按逻辑核数、用户设定生成。
/// </summary>
public sealed record StressScenario(
    string Name,
    StressKernel Kernel,
    int Threads,
    int DurationSeconds,
    double DutyCycle = 1.0)
{
    public bool IsLoad => Kernel != StressKernel.Idle && Threads > 0;

    public string Describe =>
        $"{Name} · {Kernel} · {Threads} 线程 · {DurationSeconds}s" +
        (DutyCycle < 0.999 ? $" · 占空比 {DutyCycle * 100:F0}%" : "");

    public override string ToString() => Describe;
}

/// <summary>
/// 场景目录：按「逻辑核数 + 每段时长」生成从轻到重的标准阶梯，
/// 以及以实读 PL1 为锚的多档功耗墙阶梯。全部是通用公式，不写死任何机器的瓦数。
/// </summary>
public static class StressScenarioCatalog
{
    /// <summary>默认 PL1 比例档位：先基准、再逐级降墙、最后放宽。</summary>
    public static readonly double[] DefaultPl1Ratios =
        { 1.0, 0.85, 0.70, 0.55, 0.40, 1.15, 1.30 };

    /// <summary>
    /// 标准场景阶梯。线程数按逻辑核数的比例推导，覆盖：
    /// 空载 / 单核整数 / 四分之一核整数 / 半核浮点 / 全核浮点 /
    /// 半核 SIMD / 全核 SIMD / 全核内存 / 全核混合 / 全核脉冲 / 收尾冷却。
    /// </summary>
    public static IReadOnlyList<StressScenario> StandardLadder(int logicalCores, int loadSeconds)
    {
        int cores = Math.Max(1, logicalCores);
        int sec = Math.Clamp(loadSeconds, 3, 600);
        int rest = Math.Clamp(sec / 2, 5, 15);

        int Quarter() => Math.Max(1, cores / 4);
        int Half() => Math.Max(1, cores / 2);
        int NearFull() => Math.Max(1, cores - 2);
        int OneEighth() => Math.Max(1, cores / 8);

        return new[]
        {
            new StressScenario("IDLE",          StressKernel.Idle,         0,         rest),
            new StressScenario("INT_1C",        StressKernel.ScalarInt,    1,         sec),
            new StressScenario("INT_LIGHT",     StressKernel.ScalarInt,    OneEighth(), sec),
            new StressScenario("INT_QUARTER",   StressKernel.ScalarInt,    Quarter(), sec),
            new StressScenario("FLOAT_HALF",    StressKernel.ScalarFloat,  Half(),    sec),
            new StressScenario("FLOAT_FULL",    StressKernel.ScalarFloat,  cores,     sec),
            new StressScenario("SIMD_HALF",     StressKernel.SimdFma,      Half(),    sec),
            new StressScenario("SIMD_NEARFULL", StressKernel.SimdFma,      NearFull(), sec),
            new StressScenario("SIMD_FULL",     StressKernel.SimdFma,      cores,     sec),
            new StressScenario("MEM_FULL",      StressKernel.MemoryStream, cores,     sec),
            new StressScenario("MIXED_FULL",    StressKernel.Mixed,        cores,     sec),
            new StressScenario("PULSE_FULL",    StressKernel.Pulse,        cores,     sec, 0.70),
            new StressScenario("COOL",          StressKernel.Idle,         0,         rest)
        };
    }

    /// <summary>
    /// 以实读的默认 PL1 为锚生成多档写入值，钳制在硬件合法区间内并去重。
    /// defaultPl1W 必须来自 0x610 实读，不能来自机型查表或硬编码常量。
    /// </summary>
    public static IReadOnlyList<double> Pl1Ladder(
        double defaultPl1W, double minPl1W, double maxPl1W, IEnumerable<double>? ratios = null)
    {
        if (double.IsNaN(defaultPl1W) || defaultPl1W <= 0) return Array.Empty<double>();

        double lo = Math.Max(1.0, minPl1W);
        double hi = Math.Max(lo + 1.0, maxPl1W);
        var result = new List<double>();

        foreach (double ratio in ratios ?? DefaultPl1Ratios)
        {
            double v = Math.Round(Math.Clamp(defaultPl1W * ratio, lo, hi), 1);
            if (v > 0 && !result.Any(x => Math.Abs(x - v) < 0.5))
                result.Add(v);
        }
        return result;
    }

    /// <summary>所有可用内核，供 UI 下拉与命令行参数校验复用。</summary>
    public static IReadOnlyList<StressKernel> Kernels { get; } =
        Enum.GetValues<StressKernel>();

    public static bool TryParseKernel(string? text, out StressKernel kernel)
        => Enum.TryParse(text, ignoreCase: true, out kernel)
           && Enum.IsDefined(kernel);
}
