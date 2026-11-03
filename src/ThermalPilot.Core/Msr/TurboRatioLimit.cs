using System.Globalization;

namespace ThermalPilot.Core.Msr;

/// <summary>
/// MSR 0x1AD MSR_TURBO_RATIO_LIMIT 的一次只读快照。
/// 每 8 bit 一档，依次是 1 / 2 / ... / 8 个活跃核心时的最大睿频倍率（×100MHz）；
/// 0 表示该档在本 CPU 上不存在（例如 4 核 8 线程的 5C~8C 档）。
/// </summary>
public sealed record TurboRatioState(
    ulong Raw,
    int[] Tiers,
    int OneCore,
    int AllCore,
    int MaxTier)
{
    public double OneCoreGhz => OneCore / 10.0;
    public double AllCoreGhz => AllCore / 10.0;
    public double MaxGhz => MaxTier / 10.0;
}

/// <summary>
/// 睿频倍率表编解码与变换。
///
/// 硬件事实（i7-7700HQ 实测 2026-11-03）：
///   0x1AD = 0x22232426 → 1C=38 / 2C=36 / 3C=35 / 4C=34；
///   FLEX_RATIO bit20 (OC Lock) 未置位，倍频表可写；
///   把各档拉平到 38 写入成功且回读完全匹配。
///
/// 安全边界：本产品只允许把各档设置在 [MinRatio, MaxTier]（出厂单核上限）之间，
/// 即 Intel 允许的「睿频表配置」范围，不做超出出厂规格的超频。
/// 出厂默认值掉电即恢复，软件侧的调整需要每次开机重放（见 AppSettings.Manual.TurboRatio）。
/// </summary>
public static class TurboRatioLimit
{
    public const uint MsrTurboRatioLimit = 0x1AD;
    public const uint MsrFlexRatio = 0x194;        // bit20 = Overclocking Lock
    public const uint MsrMiscEnable = 0x1A0;       // bit38 = Turbo Mode Disable
    public const uint MsrPlatformInfo = 0xCE;      // bits[15:8] = 最大非睿频倍率

    public const int TierCount = 8;
    public const int MinRatio = 8;                 // 0.8 GHz，低于此值没有实际意义

    /// <summary>逐档解码；0 表示该档不存在。</summary>
    public static int[] Tiers(ulong raw)
    {
        var t = new int[TierCount];
        for (int i = 0; i < TierCount; i++)
        {
            int r = (int)((raw >> (i * 8)) & 0xFF);
            t[i] = r is > 5 and < 80 ? r : 0;   // 过滤明显非法的档位值
        }
        return t;
    }

    public static TurboRatioState Decode(ulong raw)
    {
        int[] t = Tiers(raw);
        int one = t[0];
        int all = 0;
        int max = 0;
        for (int i = 0; i < TierCount; i++)
        {
            if (t[i] <= 0) continue;
            all = t[i];                          // 最后一个非零档 = 全核档
            if (t[i] > max) max = t[i];
        }
        if (all == 0) all = one;
        if (max == 0) max = one;
        return new TurboRatioState(raw, t, one, all, max);
    }

    /// <summary>把每个存在的档位压到 min(原值, maxRatio)；用于降频/降温。</summary>
    public static ulong Cap(ulong raw, int maxRatio)
    {
        ulong v = raw;
        for (int i = 0; i < TierCount; i++)
        {
            int shift = i * 8;
            int r = (int)((raw >> shift) & 0xFF);
            if (r == 0) continue;
            int nv = Math.Min(r, maxRatio);
            v = (v & ~(0xFFUL << shift)) | ((ulong)nv << shift);
        }
        return v;
    }

    /// <summary>
    /// 把每个存在的档位设为 ratio（可高于该档原值，但不得超过 MaxTier）。
    /// 这是「把全核睿频拉到单核上限」以释放性能的关键变换。
    /// </summary>
    public static ulong SetAll(ulong raw, int ratio)
    {
        ulong v = raw;
        for (int i = 0; i < TierCount; i++)
        {
            int shift = i * 8;
            int r = (int)((raw >> shift) & 0xFF);
            if (r == 0) continue;                // 不存在的档位保持 0，不凭空造档
            v = (v & ~(0xFFUL << shift)) | ((ulong)ratio << shift);
        }
        return v;
    }

    /// <summary>是否所有存在的档位都等于 ratio。</summary>
    public static bool AllTiersEqual(ulong raw, int ratio)
    {
        foreach (int r in Tiers(raw)) if (r != 0 && r != ratio) return false;
        return true;
    }

    public static string Describe(ulong raw) => Describe(Decode(raw));

    public static string Describe(TurboRatioState s)
    {
        string tiers = string.Join(" / ", s.Tiers.Select((r, i) =>
            $"{i + 1}C={(r == 0 ? "--" : r.ToString("D2", CultureInfo.InvariantCulture))}"));
        return $"0x{s.Raw:X16} · {tiers} · 单核 {s.OneCoreGhz:F1}GHz / 全核 {s.AllCoreGhz:F1}GHz / 出厂上限 {s.MaxGhz:F1}GHz";
    }

    /// <summary>简短档位串，给 UI「当前生效」列用。</summary>
    public static string ShortTiers(TurboRatioState s) =>
        string.Join("/", s.Tiers.Where(r => r > 0).Select(r => r.ToString(CultureInfo.InvariantCulture)));
}
