using System.Globalization;
using System.Text.RegularExpressions;

namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// 笔记本 EC 风扇速度编码换算（NBFC 生效配置语义）。
///
/// 配置里的 MinSpeedValue / MaxSpeedValue 分别对应 0% / 100%，
/// 常见机型是反向编码（如 Min=255 停转、Max=58 满速，值越小转得越快），
/// 线性公式对正反方向都成立，结果一律钳在 [0,100]。
///
/// 只做换算，不访问任何硬件；写入 EC 属于控制层，不在此处。
/// </summary>
public static class FanPercentCodec
{
    public static double FromRaw(double raw, double min, double max)
    {
        if (double.IsNaN(raw) || double.IsNaN(min) || double.IsNaN(max) ||
            Math.Abs(max - min) < 0.001)
            return double.NaN;
        return Math.Clamp((raw - min) / (max - min) * 100.0, 0.0, 100.0);
    }

    /// <summary>从 ec-probe 输出里取第一个十进制或 0x 十六进制数（寄存器原始值）。</summary>
    public static double ParseFirstNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        foreach (Match m in Regex.Matches(text, @"0[xX][0-9A-Fa-f]+|\d+(\.\d+)?"))
        {
            string s = m.Value;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hx))
                    return hx;
            }
            else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
        }
        return double.NaN;
    }
}
