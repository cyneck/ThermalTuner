using ThermalTuner.Core.Msr;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// MSR 0x1AD（TURBO_RATIO_LIMIT）编解码功能测试。
/// 重点：八档逐档解码、0 档不臆造、封顶/拉平不污染保留档位、出厂值往返一致。
/// </summary>
public class TurboRatioLimitTests
{
    // i7-7700HQ 出厂实读：1C=38 / 2C=36 / 3C=35 / 4C=34
    private const ulong FactoryRaw = 0x22232426UL;

    public void RunAllTests()
    {
        Check.Suite("TurboRatioLimit");
        Test_DecodeFactoryValue();
        Test_DecodeFiltersIllegalTiers();
        Test_Cap_LowersEveryTier();
        Test_Cap_DoesNotRaiseLowerTiers();
        Test_SetAll_FlattensToSingleCoreMax();
        Test_SetAll_DoesNotInventMissingTiers();
        Test_SetAll_RoundTripThroughDecode();
        Test_AllTiersEqual_IgnoresMissingTiers();
        Test_Describe_ReportsGhz();
        Test_ShortTiers_SkipsMissing();
        Test_Constants_MatchHardwareSpec();
    }

    private void Test_DecodeFactoryValue()
    {
        var s = TurboRatioLimit.Decode(FactoryRaw);

        Check.Equal(FactoryRaw, s.Raw, "原始值应原样保留");
        Check.Equal(38, s.OneCore, "1C 档应为 38x");
        Check.Equal(34, s.AllCore, "全核档应为 34x");
        Check.Equal(38, s.MaxTier, "出厂单核上限应为 38x");
        Check.Near(3.8, s.OneCoreGhz, 1e-9, "单核频率应为 3.8GHz");
        Check.Near(3.4, s.AllCoreGhz, 1e-9, "全核频率应为 3.4GHz");
        Check.Equal(38, s.Tiers[0], "档位[0] = 1C");
        Check.Equal(36, s.Tiers[1], "档位[1] = 2C");
        Check.Equal(35, s.Tiers[2], "档位[2] = 3C");
        Check.Equal(34, s.Tiers[3], "档位[3] = 4C");
        Check.Equal(0, s.Tiers[4], "不存在的 5C 档应为 0");
    }

    private void Test_DecodeFiltersIllegalTiers()
    {
        // 1 / 2 / 300 / 0 这类值不是可信倍率：应被过滤为 0，不能参与 max/all 计算
        ulong raw = 1UL | (2UL << 8) | (0xFFUL << 16) | (0UL << 24);
        var s = TurboRatioLimit.Decode(raw);

        Check.Equal(0, s.Tiers[0], "1x 低于下限应过滤");
        Check.Equal(0, s.Tiers[1], "2x 低于下限应过滤");
        Check.Equal(0, s.Tiers[2], "255x 超出上限应过滤");
        Check.Equal(0, s.OneCore, "过滤后单核档为 0");
        Check.Equal(0, s.AllCore, "过滤后全核档回退为 0");
    }

    private void Test_Cap_LowersEveryTier()
    {
        ulong capped = TurboRatioLimit.Cap(FactoryRaw, 30);
        var s = TurboRatioLimit.Decode(capped);

        Check.Equal(30, s.Tiers[0], "1C 应压到 30x");
        Check.Equal(30, s.Tiers[1], "2C 应压到 30x");
        Check.Equal(30, s.Tiers[2], "3C 应压到 30x");
        Check.Equal(30, s.Tiers[3], "4C 应压到 30x");
        Check.True(TurboRatioLimit.AllTiersEqual(capped, 30), "所有存在档位都应等于 30x");
        Check.Near(3.0, s.AllCoreGhz, 1e-9, "全核频率应为 3.0GHz");
    }

    private void Test_Cap_DoesNotRaiseLowerTiers()
    {
        // 封顶是 min(原值, cap)：cap 高于原值时不应把档位抬高
        ulong capped = TurboRatioLimit.Cap(FactoryRaw, 40);
        Check.Equal(FactoryRaw, capped, "cap=40 高于所有档位时应原样返回");
    }

    private void Test_SetAll_FlattensToSingleCoreMax()
    {
        ulong flat = TurboRatioLimit.SetAll(FactoryRaw, 38);
        var s = TurboRatioLimit.Decode(flat);

        Check.Equal(0x26262626UL, flat, "拉平 38x 的原始值应为 0x26262626");
        Check.True(TurboRatioLimit.AllTiersEqual(flat, 38), "所有存在档位都应等于 38x");
        Check.Near(3.8, s.AllCoreGhz, 1e-9, "全核频率应被拉到 3.8GHz");
    }

    private void Test_SetAll_DoesNotInventMissingTiers()
    {
        ulong flat = TurboRatioLimit.SetAll(FactoryRaw, 38);
        var s = TurboRatioLimit.Decode(flat);

        for (int i = 4; i < TurboRatioLimit.TierCount; i++)
            Check.Equal(0, s.Tiers[i], $"5C~8C 档（索引 {i}）本不存在，不得凭空造档");
    }

    private void Test_SetAll_RoundTripThroughDecode()
    {
        ulong flat = TurboRatioLimit.SetAll(TurboRatioLimit.Cap(FactoryRaw, 30), 38);
        var s = TurboRatioLimit.Decode(flat);

        Check.Equal(38, s.OneCore, "先封顶再拉平后单核应为 38x");
        Check.Equal(38, s.AllCore, "全核也应为 38x");
        Check.Equal(0x26262626UL, s.Raw, "往返后原始值应稳定");
    }

    private void Test_AllTiersEqual_IgnoresMissingTiers()
    {
        ulong flat = TurboRatioLimit.SetAll(FactoryRaw, 38);
        Check.True(TurboRatioLimit.AllTiersEqual(flat, 38), "拉平后所有存在档位都等于 38");
        Check.True(TurboRatioLimit.AllTiersEqual(0x00000026UL, 38), "只有 1C 档时也算全档等于 38");
        Check.False(TurboRatioLimit.AllTiersEqual(FactoryRaw, 38), "出厂 2C/3C/4C 不等于 38，应返回 false");
    }

    private void Test_Describe_ReportsGhz()
    {
        string text = TurboRatioLimit.Describe(FactoryRaw);
        Check.Contains(text, "单核 3.8GHz", "描述应含单核频率");
        Check.Contains(text, "全核 3.4GHz", "描述应含全核频率");
        Check.Contains(text, "1C=38", "描述应含逐档倍率");
    }

    private void Test_ShortTiers_SkipsMissing()
    {
        Check.Equal("38/36/35/34", TurboRatioLimit.ShortTiers(TurboRatioLimit.Decode(FactoryRaw)),
            "简短档位串应跳过 0 档");
    }

    private void Test_Constants_MatchHardwareSpec()
    {
        Check.Equal(0x1ADu, TurboRatioLimit.MsrTurboRatioLimit, "0x1AD 是 TURBO_RATIO_LIMIT");
        Check.Equal(0x194u, TurboRatioLimit.MsrFlexRatio, "0x194 是 FLEX_RATIO（OC Lock 位）");
        Check.Equal(8, TurboRatioLimit.TierCount, "0x1AD 共 8 档");
        Check.Equal(8, TurboRatioLimit.MinRatio, "产品下限为 8x（800MHz）");
    }
}
