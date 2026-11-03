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

    // ---------------- 软件侧优化参数（跨重启保留） ----------------
    // 固件 0x610 里的值是出厂默认，掉电即恢复，无法持久化；
    // 能持久化的是「软件想让 CPU 跑成什么样」：实测标定结果 + 手动微调值，
    // 每次开机由软件重放写入寄存器，软件不运行时 CPU 按固件默认散热逻辑运转。

    /// <summary>开机后是否自动把已保存的微调参数写入 MSR（需管理员 + 写入授权）。</summary>
    public bool ReplayOnStartup { get; set; } = true;

    /// <summary>实测标定得到的本机稳态热模型（不是固件值，可超过固件默认）。</summary>
    public CalibrationSnapshot? Calibration { get; set; }

    /// <summary>高级参数页手动微调并成功写入的参数。</summary>
    public ManualTuning? Manual { get; set; }
}

/// <summary>实测两段式标定结果，开机后加载进热模型，不必每次重新标定。</summary>
public sealed class CalibrationSnapshot
{
    public double Rth { get; set; }
    public double IdleC { get; set; }
    public double IdleW { get; set; }
    public double HotC { get; set; }
    public double HotW { get; set; }
    public double BasisW { get; set; }
    public double PredictedC { get; set; }
    public string Recommended { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }
}

/// <summary>手动微调值（待应用 → 应用后写入寄存器并保存）。</summary>
public sealed class ManualTuning
{
    public double Pl1W { get; set; }
    public double Pl2W { get; set; }
    public double TauS { get; set; }
    public int Epp { get; set; }
    /// <summary>是否连时间窗口一起改写（默认 false，避免动 OEM 特殊编码）。</summary>
    public bool WriteTimeWindow { get; set; }
    /// <summary>睿频倍率上限（×100MHz，0 = 不调节 0x1AD）。上限为出厂单核最大倍率。</summary>
    public int TurboRatio { get; set; }
    public DateTime SavedAt { get; set; }
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
