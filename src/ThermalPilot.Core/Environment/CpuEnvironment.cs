namespace ThermalPilot.Core.Environment;

/// <summary>
/// CPU 厂商枚举。
/// </summary>
public enum CpuVendor
{
    Unknown,
    Intel,
    Amd,
    Arm,
    Other
}

/// <summary>
/// 产品形态分类（决定功耗墙量级）。
/// </summary>
public enum CpuFormFactor
{
    Unknown,
    MobileUltraLowPower, // U / Y 系列
    MobilePerformance,   // H / HK / HX 系列
    MobileMainstream,    // P 系列
    Desktop,
    Server
}

/// <summary>
/// 探测到的运行环境画像（只读）。
/// 由 HardwareProbe 在启动时采集一次，供 HardwareLimitResolver 推导物理约束。
/// </summary>
public sealed record CpuEnvironment
{
    /// <summary>CPU 品牌字符串原文，如 "Intel(R) Core(TM) Ultra 7 255H"。</summary>
    public string BrandString { get; init; } = string.Empty;

    public CpuVendor Vendor { get; init; } = CpuVendor.Unknown;

    /// <summary>
    /// Intel 代数（6 = Skylake, 7 = Kaby Lake, 12 = Alder Lake ...）。
    /// Core Ultra 系列用 100+ 编码：100 = Meteor Lake, 200 = Arrow Lake / Lunar Lake。
    /// 无法识别时为 0。
    /// </summary>
    public int Generation { get; init; }

    /// <summary>产品线后缀（H / U / P / HX / K 等），大写；未知为空。</summary>
    public string Suffix { get; init; } = string.Empty;

    public CpuFormFactor FormFactor { get; init; } = CpuFormFactor.Unknown;

    /// <summary>是否为 P/E 核混合架构（12 代+ / Core Ultra）。</summary>
    public bool IsHybridArchitecture { get; init; }

    public int LogicalCores { get; init; }
    public int PhysicalCores { get; init; }

    /// <summary>操作系统平台标识（Windows / Linux / macOS / Unknown）。</summary>
    public string OsPlatform { get; init; } = "Unknown";

    /// <summary>是否处于虚拟机/容器等虚拟化环境（功耗寄存器可能不可写）。</summary>
    public bool IsVirtualized { get; init; }

    /// <summary>探测过程备注/降级原因，用于诊断输出。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public override string ToString() =>
        $"[{Vendor}] {BrandString} | Gen={Generation} Suffix={Suffix} Form={FormFactor} " +
        $"Hybrid={IsHybridArchitecture} Cores={PhysicalCores}P/{LogicalCores}L OS={OsPlatform} VM={IsVirtualized}";
}
