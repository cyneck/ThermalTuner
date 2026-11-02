using System.IO;
using Microsoft.Win32;

namespace ThermalPilot.App.Services;

/// <summary>
/// 开机自启：写 HKCU\...\Run（不需要管理员，也不污染系统级配置）。
/// 卸载时由安装包清理该键值。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ThermalPilot";

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

            string dll = Path.Combine(AppContext.BaseDirectory, "ThermalPilot.App.dll");
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
