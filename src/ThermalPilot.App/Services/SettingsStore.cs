using System.IO;
using System.Text.Json;

namespace ThermalPilot.App.Services;

/// <summary>用户设置（持久化到 %LOCALAPPDATA%\ThermalPilot\settings.json）。</summary>
public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 1000;
    public string Profile { get; set; } = "balanced";
    public double CeilingC { get; set; } = 88.0;
    /// <summary>D-01：向导确认后默认自动接管；未完成向导时保持只读。</summary>
    public bool AutoTakeover { get; set; }
    public bool RestoreOnExit { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>双开关之一：允许真实写入 MSR。默认关闭，缺失时永远走演练。</summary>
    public bool AllowRealWrite { get; set; }
    /// <summary>关闭窗口时最小化到托盘而非退出。</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>开机自启（HKCU Run）。</summary>
    public bool RunAtStartup { get; set; }
    public bool WizardDone { get; set; }
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalPilot");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDirectory => Dir;
    public static string SettingsPath => FilePath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时用默认值，不阻断启动
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
        }
        catch
        {
            // 只读目录等场景下静默失败，不影响运行
        }
    }
}
