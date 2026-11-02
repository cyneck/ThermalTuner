using System.Runtime.InteropServices;

namespace ThermalPilot.Core;

/// <summary>
/// 物理内存占用（总容量 / 已用 / 百分比）。
///
/// 为什么用 GlobalMemoryStatusEx 而不是监控库：
///   · 它是 Windows 自己在"任务管理器 → 性能 → 内存"里用的同一组数据（ullTotalPhys - ullAvailPhys）；
///   · 监控库的 "Memory Used" 有几种口径（已提交 / 工作集 / 物理），彼此差得很多，
///     与任务管理器/OMEN Hub 对不上时很难解释。
/// 这里只用物理内存口径：已用 = 总量 − 可用，与任务管理器一致。
/// </summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;      // 已用百分比（Windows 自己算好的，0–100）
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>读一次物理内存状态。失败返回全 0（界面显示 --，不编数）。</summary>
    public static (double totalMb, double usedMb, double pct) Read()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (0, 0, 0);

            double totalMb = m.ullTotalPhys / 1024.0 / 1024.0;
            double availMb = m.ullAvailPhys / 1024.0 / 1024.0;
            double usedMb = Math.Max(0, totalMb - availMb);
            // dwMemoryLoad 是 Windows 给的百分比，但它是取整后的；用算出来的更细，两者通常一致
            double pct = totalMb > 0 ? usedMb / totalMb * 100.0 : 0;
            return (Math.Round(totalMb, 0), Math.Round(usedMb, 0), Math.Round(pct, 0));
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>自检：总量必须合理（>1GB 且 <4TB），已用 ≤ 总量，百分比在 0–100。</summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();
        var (total, used, pct) = Read();
        if (total <= 1024 || total > 4 * 1024 * 1024)
            errors.Add($"物理内存总量异常：{total:F0} MB");
        if (used > total)
            errors.Add($"已用 {used:F0} MB 超过总量 {total:F0} MB");
        if (pct < 0 || pct > 100)
            errors.Add($"占用率越界：{pct:F1}%");
        // 交叉校验：pct 与 used/total 必须自洽（±1%）
        if (total > 0 && Math.Abs(pct - used / total * 100.0) > 1.0)
            errors.Add($"占用率与已用/总量不自洽：{pct:F1}% vs {used / total * 100:F1}%");
        return errors;
    }
}
