using System.Text.RegularExpressions;

namespace ThermalPilot.Core.Taxonomy;

/// <summary>三层参数来源：固件固化 / 智能标定推荐 / 用户编辑值。用于 UI 标注，不参与写入逻辑。</summary>
public enum ParamLayer
{
    Firmware = 0,      // 出厂默认，只读基准，不可持久化
    Calibrated = 1,    // 软件实测标定推荐（只含通用安全参数）
    UserEdited = 2     // 用户在高级参数页手动编辑并成功写入
}

/// <summary>参数分组：通用项可智能标定推荐；个性化项只能由用户手动微调。</summary>
public enum ParamGroup
{
    Universal = 0,     // PL1 / PL2 / Tau / EPP / Min-Max Perf / Activity Window
    Personalized = 1   // 电压偏移 / 睿频倍率（能力因 CPU 而异）
}

/// <summary>
/// CPU 能力预筛：根据型号给出「哪些个性化参数可能可用」的提示与约束。
///
/// ⚠️ 这是**提示性预筛，不是能力承诺**：Plundervolt 修复微码、OEM BIOS 锁、HVCI、
/// Windows 电源计划都可能让写入失效。最终必须以 --voltage-probe / --hwp-probe
/// 的「写入 → 回读 → 恢复」实测为准。通用参数不依赖型号判断。
/// </summary>
public sealed record CpuCapability(
    string CpuName,
    string Vendor,
    int Generation,
    bool IsMobile,
    bool IsUnlocked,
    bool HwpExpected,
    bool VoltageOffsetPlausible,
    bool TurboRatioMayBeEffective,
    string VoltageHint,
    string TurboHint)
{
    private static readonly Regex IntelModel = new(@"i[3579]\s*-\s*(\d{4,5})", RegexOptions.IgnoreCase);

    public static CpuCapability Probe(string? cpuName)
    {
        string name = string.IsNullOrWhiteSpace(cpuName) ? "未知" : cpuName.Trim();
        bool intel = name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        bool amd = name.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        string vendor = intel ? "Intel" : amd ? "AMD" : "Unknown";

        int gen = 0;
        var m = IntelModel.Match(name);
        if (m.Success)
        {
            string digits = m.Groups[1].Value;
            gen = digits.Length >= 5
                ? int.Parse(digits[..2])
                : int.Parse(digits[..1]);
        }

        bool mobile = Regex.IsMatch(name, @"(HQ|HK|H|U|Y|P)\b", RegexOptions.IgnoreCase)
                      || name.Contains("Mobile", StringComparison.OrdinalIgnoreCase);
        bool unlocked = Regex.IsMatch(name, @"(KF|HK|K)\b", RegexOptions.IgnoreCase);
        bool hwp = intel && gen >= 6;

        // 10 代及以后 + 已打 Plundervolt 修复微码的平台，OC Mailbox 通常忽略写入。
        bool voltagePlausible = intel && gen is >= 2 and <= 9;
        string voltageHint = !intel
            ? amd
                ? "非 Intel 平台：0x150 OC Mailbox 不适用，电压偏移不可用（AMD 请用 BIOS Curve Optimizer）"
                : "无法识别 CPU 厂商 / 型号：电压偏移能力未知，必须跑 --voltage-probe 实测，不按型号硬编码禁止"
            : gen >= 10
                ? $"第 {gen} 代 Intel：Plundervolt 修复微码通常已禁用 OC Mailbox 写入，电压偏移大概率不可用"
                : voltagePlausible
                    ? $"第 {gen} 代 Intel：原则上有 OC Mailbox，但 OEM BIOS 锁 / HVCI 可能拦截，必须跑 --voltage-probe 实测"
                    : "无法从型号判断代际：电压偏移能力未知，必须实测";

        bool turboMayWork = intel && unlocked;
        string turboHint = turboMayWork
            ? "解锁倍频（K/HK）：规格内档位配置可能有效"
            : "非 K / 移动 U：倍频表可写入但 PCU/HWP 常不采纳（本机 i7-7700HQ 实测 38x 零收益），不作为性能卖点";

        return new CpuCapability(
            name, vendor, gen, mobile, unlocked, hwp,
            voltagePlausible, turboMayWork, voltageHint, turboHint);
    }

    public string Summary =>
        $"{Vendor}{(Generation > 0 ? $" 第 {Generation} 代" : string.Empty)}" +
        $"{(IsMobile ? " · 移动版" : string.Empty)}{(IsUnlocked ? " · 解锁倍频" : string.Empty)}" +
        $" · HWP {(HwpExpected ? "预期可用" : "预期不可用")}";

    /// <summary>能力矩阵文本，供高级参数页 / 自检报告复用。</summary>
    public string MatrixText =>
        $"CPU 型号      : {CpuName}\n" +
        $"能力预筛      : {Summary}\n" +
        $"HWP (0x774)   : {(HwpExpected ? "预期可用（通用参数可智能标定推荐）" : "预期不可用，通用参数中的 HWP 项需实测")}\n" +
        $"电压 (0x150)  : {(VoltageOffsetPlausible ? "可能可用（需实测）" : "大概率不可用")} — {VoltageHint}\n" +
        $"睿频 (0x1AD)  : {TurboHint}\n" +
        "说明          : 以上仅为型号预筛，最终能力一律以写入自检实测为准。";
}
