using ThermalPilot.Core.Msr;

namespace ThermalPilot.Core.Tests;

/// <summary>
/// MSR 0x610 编解码测试：写硬件的正确性取决于位域，必须逐位验证。
/// </summary>
public class MsrEncodingTests
{
    public void RunAllTests()
    {
        Test_TauEncoding_MatchesIntelEncoding();
        Test_Tau_AllLegalValuesRoundTrip();
        Test_Power_RoundTrip();
        Test_Encode_PreservesLockAndEnableBits();
        Test_Encode_OnlyTouchesTargetFields();
        Test_Encode_RejectsIllegalTau();
        Test_Power_ClampsToFieldWidth();
        Test_Decode_ReadsKnownRawValue();
        Console.WriteLine("All MSR encoding tests passed successfully!");
    }

    private void Test_TauEncoding_MatchesIntelEncoding()
    {
        // Intel: value = (1 + Y/4) * 2^X, bits 23:17 = (Y << 5) | X
        AssertEq(0, RaplPowerLimit.EncodeTau(1), "1s -> X=0,Y=0");
        AssertEq(3, RaplPowerLimit.EncodeTau(8), "8s -> X=3,Y=0");
        AssertEq(4, RaplPowerLimit.EncodeTau(16), "16s -> X=4,Y=0");
        AssertEq(5, RaplPowerLimit.EncodeTau(32), "32s -> X=5,Y=0");
        AssertEq(6, RaplPowerLimit.EncodeTau(64), "64s -> X=6,Y=0");
        AssertEq((3 << 5) | 4, RaplPowerLimit.EncodeTau(28), "28s -> X=4,Y=3");
    }

    private void Test_Tau_AllLegalValuesRoundTrip()
    {
        foreach (double tau in RaplPowerLimit.LegalTauSeconds)
        {
            int bits = RaplPowerLimit.EncodeTau(tau);
            Assert(bits >= 0, $"tau {tau} 应可编码");
            double back = RaplPowerLimit.DecodeTau(bits);
            Assert(Math.Abs(back - tau) < 0.01, $"tau {tau} 往返得到 {back}");
        }
        Assert(RaplPowerLimit.LegalTauSeconds.Count >= 8, "合法窗口集合至少 8 个");
    }

    private void Test_Power_RoundTrip()
    {
        ulong raw = RaplPowerLimit.Encode(0, 35.0, 44.0, 16);
        var d = RaplPowerLimit.Decode(raw);
        Assert(Math.Abs(d.Pl1W - 35.0) < 0.001, $"PL1 往返 {d.Pl1W}");
        Assert(Math.Abs(d.Pl2W - 44.0) < 0.001, $"PL2 往返 {d.Pl2W}");
        Assert(Math.Abs(d.TauS - 16.0) < 0.001, $"Tau 往返 {d.TauS}");
    }

    private void Test_Encode_PreservesLockAndEnableBits()
    {
        // 模拟固件已设置：PL1 enable(15) + PL1 clamp(16) + PL2 enable(47) + clamp(48) + lock(63)
        ulong original = (1UL << 15) | (1UL << 16) | (1UL << 47) | (1UL << 48) | (1UL << 63);

        ulong encoded = RaplPowerLimit.Encode(original, 30.0, 40.0, 8);
        var d = RaplPowerLimit.Decode(encoded);

        Assert(d.Locked, "lock 位必须保留（否则会误解锁固件保护）");
        Assert(d.Pl1Enabled, "PL1 enable 位必须保留");
        Assert(d.Pl2Enabled, "PL2 enable 位必须保留");
        Assert(d.Pl2Clamp, "PL2 clamp 位必须保留");
        Assert(Math.Abs(d.Pl1W - 30.0) < 0.001, "PL1 已更新");
    }

    private void Test_Encode_OnlyTouchesTargetFields()
    {
        // 保留位（如 [16:15] 之外的高位）在改写后应原样保留
        ulong original = 0xA5A5_0000_0000_0000UL;
        ulong encoded = RaplPowerLimit.Encode(original, 20.0, 25.0, 4);

        ulong preservedMask = original & ~(
            ((ulong)0x7FFF << 0) | ((ulong)0x7F << 17) | ((ulong)0x7FFF << 32));
        ulong preservedAfter = encoded & ~(
            ((ulong)0x7FFF << 0) | ((ulong)0x7F << 17) | ((ulong)0x7FFF << 32));

        AssertEq(preservedMask, preservedAfter, "非目标位必须保持不变");
    }

    private void Test_Encode_RejectsIllegalTau()
    {
        bool threw = false;
        try { RaplPowerLimit.Encode(0, 30, 40, 9); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Assert(threw, "非法 Tau(9s) 必须拒绝编码，而不是静默取近似值");
    }

    private void Test_Power_ClampsToFieldWidth()
    {
        var d = RaplPowerLimit.Decode(RaplPowerLimit.Encode(0, 9999, 9999, 16));
        Assert(d.Pl1W <= 4095.875 + 0.001, "PL1 不得超出 15 位字段上限");
        Assert(d.Pl2W <= 4095.875 + 0.001, "PL2 不得超出 15 位字段上限");
    }

    private void Test_Decode_ReadsKnownRawValue()
    {
        // 手算：PL1=45W -> 360 (0x168)，PL2=60W -> 480 (0x1E0)，Tau=32s -> X=5
        ulong raw = 360UL | ((ulong)(5 & 0x7F) << 17) | (480UL << 32);
        var d = RaplPowerLimit.Decode(raw);

        Assert(Math.Abs(d.Pl1W - 45.0) < 0.001, $"PL1 应为 45W，实际 {d.Pl1W}");
        Assert(Math.Abs(d.Pl2W - 60.0) < 0.001, $"PL2 应为 60W，实际 {d.Pl2W}");
        Assert(Math.Abs(d.TauS - 32.0) < 0.001, $"Tau 应为 32s，实际 {d.TauS}");
        Assert(!d.Locked, "未设置 lock 位");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEq(ulong expected, ulong actual, string message)
    {
        if (expected != actual) throw new Exception($"{message}（期望 {expected}，实际 {actual}）");
    }

    private static void AssertEq(int expected, int actual, string message)
    {
        if (expected != actual) throw new Exception($"{message}（期望 {expected}，实际 {actual}）");
    }
}
