using System.IO;
using Microsoft.Win32;

namespace ThermalTuner.App.Services;

/// <summary>
/// 开机自启：写 HKCU\...\Run（不需要管理员，也不污染系统级配置）。
/// 卸载时由安装包清理该键值。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ThermalTuner";
    private const string LegacyValueName = "ThermalPilot";

    /// <summary>
    /// 开机启动命令。单文件发布下 Assembly.Location 为空，必须用 ProcessPath（IL3000）。
    /// 开发期通过 dotnet 启动时，退化为 dotnet + dll 的完整命令行。
    /// </summary>
    public static string ExecutablePath
    {
        get
        {
            string? process = System.Environment.ProcessPath;

            if (!string.IsNullOrEmpty(process) &&
                !process.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return $"\"{process}\"";

            string dll = Path.Combine(AppContext.BaseDirectory, "ThermalTuner.App.dll");
            return string.IsNullOrEmpty(process) ? $"\"{dll}\"" : $"\"{process}\" \"{dll}\"";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 产品更名后清理旧开机自启项：仅当旧值明确指向旧版 exe 时删除，
    /// 避免误删用户自己创建的同名项。程序启动时调用一次，静默执行。
    /// </summary>
    public static void CleanupLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (key.GetValue(LegacyValueName) is string old &&
                old.Contains("ThermalPilot.App.exe", StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 注册表不可写时忽略，不阻断启动
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, ExecutablePath);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
