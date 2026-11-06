namespace ThermalTuner.Core.Msr;

/// <summary>MSR_HWP_REQUEST (0x774) 实读解码结果。</summary>
public readonly record struct HwpRequestState(
    int MinPerf,
    int MaxPerf,
    int DesiredPerf,
    int Epp,
    int ActivityWindow,
    bool PackageControl,
    ulong Raw)
{
    public string Describe =>
        $"Min {MinPerf} / Max {MaxPerf} / Desired {DesiredPerf} / EPP {Epp} / AW {ActivityWindow}";
}

/// <summary>
/// 0x774 编解码与约束。位域见 Intel SDM Vol.4：
///   [7:0] Min Perf · [15:8] Max Perf · [23:16] Desired Perf · [31:24] EPP
///   [41:32] Activity Window · [42] Package Control
///
/// 产品约定：Desired Perf 恒保持固件值（0 = 硬件自主选频），Package Control 不改写，
/// 避免把 HWP 自主调频写死，属于「不开放的危险/反效果项」。
/// </summary>
public static class HwpRequest
{
    public const uint Msr = 0x774;
    public const int FieldMax = 255;
    public const int ActivityWindowMax = 0x3FF;

    public static HwpRequestState Decode(ulong raw) => new(
        MinPerf: (int)(raw & 0xFF),
        MaxPerf: (int)((raw >> 8) & 0xFF),
        DesiredPerf: (int)((raw >> 16) & 0xFF),
        Epp: (int)((raw >> 24) & 0xFF),
        ActivityWindow: (int)((raw >> 32) & ActivityWindowMax),
        PackageControl: ((raw >> 42) & 1) != 0,
        Raw: raw);

    /// <summary>读-改-写：参数为 -1 表示保持原值，绝不整字覆盖。</summary>
    public static ulong Encode(ulong original,
        int minPerf = -1, int maxPerf = -1, int epp = -1, int activityWindow = -1)
    {
        ulong v = original;
        if (minPerf >= 0)
            v = (v & ~0xFFUL) | (ulong)(byte)minPerf;
        if (maxPerf >= 0)
            v = (v & ~(0xFFUL << 8)) | ((ulong)(byte)maxPerf << 8);
        if (epp >= 0)
            v = (v & ~(0xFFUL << 24)) | ((ulong)(byte)epp << 24);
        if (activityWindow >= 0)
            v = (v & ~(0x3FFUL << 32)) | ((ulong)(activityWindow & ActivityWindowMax) << 32);
        return v;
    }

    /// <summary>
    /// 范围约束。firmwareMaxPerf 来自 0x774 出厂实读（本机 38），
    /// Max Perf 不得超过它——这是「规格内调参，不做超频」的硬边界。
    /// </summary>
    public static string? Validate(int minPerf, int maxPerf, int epp,
        int activityWindow, int firmwareMaxPerf)
    {
        if (minPerf is < 0 or > FieldMax || maxPerf is < 0 or > FieldMax)
            return $"Min / Max Perf 必须在 [0, {FieldMax}]";
        if (minPerf > maxPerf)
            return $"Min Perf ({minPerf}) 不能大于 Max Perf ({maxPerf})";
        if (firmwareMaxPerf > 0 && maxPerf > firmwareMaxPerf)
            return $"Max Perf 不得超过出厂上限 {firmwareMaxPerf}（不做超频）";
        if (epp is < 0 or > FieldMax)
            return $"EPP 必须在 [0, {FieldMax}]";
        if (activityWindow is < 0 or > ActivityWindowMax)
            return $"Activity Window 必须在 [0, {ActivityWindowMax}]";
        return null;
    }
}
