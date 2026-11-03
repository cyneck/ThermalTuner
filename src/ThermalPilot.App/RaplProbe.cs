using System.IO;
using System.Text;
using ThermalPilot.App.Services;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Environment;

namespace ThermalPilot.App;

/// <summary>
/// RAPL 功耗计量自检（纯只读，不写任何 MSR）：
/// 打开 MSR 通道 → 读 0x606 能量单位 → 连续采 5 秒 0x611 能量计数器 → 折算实时功耗。
/// 用法：ThermalPilot.App.exe --rapl-probe --out rapl.txt
/// </summary>
internal static class RaplProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        var env = HardwareProbe.Probe();

        sb.AppendLine("== CPU ==");
        sb.AppendLine($"型号        : {env.BrandString}");
        sb.AppendLine($"厂商        : {env.Vendor}");
        sb.AppendLine($"虚拟化      : {env.IsVirtualized}");

        sb.AppendLine();
        sb.AppendLine("== MSR 通道 ==");
        using var channel = MsrChannel.Open();
        sb.AppendLine($"状态        : {channel.Status} / {MsrChannel.StatusText(channel.Status)}");
        sb.AppendLine($"详情        : {channel.Detail}");

        sb.AppendLine();
        sb.AppendLine("== RAPL ==");
        var meter = new RaplPowerMeter(channel, env.Vendor);
        sb.AppendLine($"可用        : {meter.Available}");
        sb.AppendLine($"详情        : {meter.Detail}");

        int ok = 0;
        if (meter.Available)
        {
            sb.AppendLine();
            sb.AppendLine("时间(ms)    实时功耗(W)");
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(500);
                if (meter.TrySample(out double w))
                {
                    sb.AppendLine($"{i * 500,8}    {w,8:F2}");
                    ok++;
                }
                else
                {
                    sb.AppendLine($"{i * 500,8}    {'-',8}");
                }
            }
            sb.AppendLine();
            sb.AppendLine($"有效样本    : {ok}/12");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠退出码 */ }

        return meter.Available && ok > 0 ? 0 : 3;
    }
}
