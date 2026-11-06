namespace ThermalTuner.Core.Msr;

/// <summary>
/// Intel OC Mailbox (MSR 0x150) Core/Cache 电压偏移编码。
/// [31:21] 为 11 位补码，单位 1/1024 V；[20] 附近为写使能位（平台有 1&lt;&lt;20 / 2&lt;&lt;20 两种编码）。
///
/// 产品硬约束：只允许降压 [-150, 0] mV，禁止加压；写入后必须回读校验；
/// 退出 / 恢复原值时强制回零；寄存器易失，重启即回到固件默认。
/// 注意：0x150 是命令/响应接口，直接读返回的是上次命令响应，不能据此断定「当前无偏移」。
/// </summary>
public static class FivrVoltage
{
    public const uint Msr = 0x150;
    public const int MinOffsetMv = -150;
    public const int MaxOffsetMv = 0;

    private static readonly ulong[] EnableBits = { 1UL << 20, 2UL << 20 };

    public static int EnableVariants => EnableBits.Length;

    public static int Clamp(int mv) => Math.Clamp(mv, MinOffsetMv, MaxOffsetMv);

    public static string? Validate(int mv) =>
        mv < MinOffsetMv || mv > MaxOffsetMv
            ? $"电压偏移必须在 [{MinOffsetMv}, {MaxOffsetMv}] mV（只允许降压，禁止加压）"
            : null;

    public static ulong Encode(int offsetMv, int enableVariant = 0)
    {
        long units = (long)Math.Round(Clamp(offsetMv) * 1024.0 / 1000.0);
        ulong field = (ulong)(units & 0x7FF) << 21;
        return field | EnableBits[Math.Clamp(enableVariant, 0, EnableBits.Length - 1)];
    }

    /// <summary>OC Mailbox 写后读回：高位匹配即视为命令被接受（老版本同款判据）。</summary>
    public static bool ResponseMatches(ulong sent, ulong readback) =>
        readback != 0 && (readback & 0xFFE00000UL) == (sent & 0xFFE00000UL);

    /// <summary>解析响应里的偏移值，仅作展示参考：读回的是命令响应而非实时电压。</summary>
    public static int? DecodeOffsetMv(ulong raw)
    {
        long v = (long)((raw >> 21) & 0x7FF);
        if ((v & 0x400) != 0) v -= 0x800;      // 11 位补码
        return (int)Math.Round(v * 1000.0 / 1024.0);
    }

    /// <summary>回零指令（退出 / 恢复时必须下发）。</summary>
    public static ulong Zero => 0UL;
}
