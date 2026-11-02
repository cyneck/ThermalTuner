using System.IO;
using System.Reflection;

namespace ThermalPilot.App.Services.Msr;

/// <summary>
/// 从 LibreHardwareMonitor 程序集里取出内置的 Pawn 模块（IntelMSR 等）。
///
/// 为什么不自己打包 blob：
///  PawnIO 只加载厂商签名的模块，我们无法自行签发；LHM 随包携带的是已签名版本。
///  运行时从已引用的 LHM 程序集读取，既避免再分发签名件，也跟随依赖版本自动更新。
/// 合规：LHM 为 MPL-2.0，本项目仅在其程序集内读取资源，未修改其文件。
/// </summary>
internal static class LhmPawnModule
{
    public static string[] AvailableModuleNames()
    {
        try
        {
            var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            return asm.GetManifestResourceNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>取 Intel 平台 MSR 模块；找不到返回 null。</summary>
    public static byte[]? TryReadIntelMsr()
    {
        var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
        var names = asm.GetManifestResourceNames();

        string? pick =
            names.FirstOrDefault(n => n.Contains("IntelMsr", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Contains("Msr", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

        if (pick is null) return null;

        using var stream = asm.GetManifestResourceStream(pick);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
