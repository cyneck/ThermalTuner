using System.Globalization;

namespace ThermalPilot.Core.Msr;

/// <summary>
/// MSR 0x610（MSR_PKG_POWER_LIMIT）解码结果。
/// 字段定义见 Intel SDM Vol.4，PL1/PL2 单位为 1/8 W，时间窗口为 (1+Y/4)*2^X 秒。
/// </summary>
public readonly record struct RaplLimits(
    double Pl1W,
    double Pl2W,
    double TauS,
    bool Pl1Enabled,
    bool Pl2Clamp,
    bool Pl2Enabled,
    bool Locked,
    ulong Raw);

/// <summary>
/// 0x610 的编解码与安全写入约束。
/// 纯函数、无副作用，便于单测；实际写硬件由 App 层的 MsrGuard 负责。
/// </summary>
public static class RaplPowerLimit
{
    public const uint MsrPkgPowerLimit = 0x610;

    // 位域
    private const int Pl1Shift = 0, Pl1Mask = 0x7FFF;      // [14:0]
    private const int Pl1EnableBit = 15;
    private const int Pl1ClampBit = 16;
    private const int TauShift = 17, TauMask = 0x7F;       // [23:17]
    private const int Pl2Shift = 32, Pl2Mask = 0x7FFF;     // [46:32]
    private const int Pl2EnableBit = 47;
    private const int Pl2ClampBit = 48;
    private const int LockBit = 63;

    private const double WattScale = 8.0;   // 1/8 W
    private const double MaxEncodableW = 4095.875;

    public static RaplLimits Decode(ulong raw)
    {
        double pl1 = ((raw >> Pl1Shift) & Pl1Mask) / WattScale;
        double pl2 = ((raw >> Pl2Shift) & Pl2Mask) / WattScale;
        double tau = DecodeTau((int)((raw >> TauShift) & TauMask));

        return new RaplLimits(
            Pl1W: pl1,
            Pl2W: pl2,
            TauS: tau,
            Pl1Enabled: Bit(raw, Pl1EnableBit),
            Pl2Clamp: Bit(raw, Pl2ClampBit),
            Pl2Enabled: Bit(raw, Pl2EnableBit),
            Locked: Bit(raw, LockBit),
            Raw: raw);
    }

    /// <summary>
    /// 在原始值基础上只改写 PL1/PL2/时间窗口，保留 enable/clamp/lock 等其它位。
    /// 这是「读-改-写」的硬性要求：整字覆盖会误清 lock 位或写入保留位。
    /// </summary>
    public static ulong Encode(ulong original, double pl1W, double pl2W, double tauS)
    {
        int tauBits = EncodeTau(tauS);
        if (tauBits < 0)
            throw new ArgumentOutOfRangeException(nameof(tauS), tauS, "不是合法的 MSR 时间窗口编码");

        ulong pl1Bits = ToPowerBits(pl1W);
        ulong pl2Bits = ToPowerBits(pl2W);

        ulong v = original;
        v &= ~((ulong)Pl1Mask << Pl1Shift);
        v |= pl1Bits << Pl1Shift;

        v &= ~((ulong)TauMask << TauShift);
        v |= (ulong)tauBits << TauShift;

        v &= ~((ulong)Pl2Mask << Pl2Shift);
        v |= pl2Bits << Pl2Shift;

        return v;
    }

    /// <summary>时间窗口编码：bits 23:22 = Y，bits 21:17 = X，值 = (1 + Y/4) * 2^X。</summary>
    public static int EncodeTau(double tauS)
    {
        for (int x = 0; x <= 31; x++)
        {
            for (int y = 0; y <= 3; y++)
            {
                double value = (1.0 + y / 4.0) * (1L << x);
                if (Math.Abs(value - tauS) < 0.01)
                    return (y << 5) | x;
            }
        }
        return -1;
    }

    public static double DecodeTau(int bits)
    {
        int x = bits & 0x1F;
        int y = (bits >> 5) & 0x3;
        return (1.0 + y / 4.0) * (1L << x);
    }

    /// <summary>合法的硬件时间窗口集合（供 UI 校验，与 HardwareLimits.LegalTauSeconds 一致）。</summary>
    public static IReadOnlyList<double> LegalTauSeconds { get; } =
        new[] { 1d, 2d, 4d, 8d, 16d, 28d, 32d, 64d, 128d }.Where(t => EncodeTau(t) >= 0).ToArray();

    public static string Describe(ulong raw)
    {
        var d = Decode(raw);
        return string.Format(CultureInfo.InvariantCulture,
            "PL1 {0:F3}W{1} / PL2 {2:F3}W{3} / Tau {4:F0}s / lock={5}",
            d.Pl1W, d.Pl1Enabled ? "(en)" : "(dis)",
            d.Pl2W, d.Pl2Enabled ? "(en)" : "(dis)",
            d.TauS, d.Locked);
    }

    private static ulong ToPowerBits(double watts)
    {
        if (double.IsNaN(watts) || watts < 0) return 0;
        if (watts > MaxEncodableW) watts = MaxEncodableW;
        return (ulong)Math.Round(watts * WattScale, MidpointRounding.AwayFromZero);
    }

    private static bool Bit(ulong v, int index) => ((v >> index) & 1UL) == 1UL;
}
