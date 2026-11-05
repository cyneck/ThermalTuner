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

    // ---------------- 三层参数模型（避免复杂化，只用两块数据 + 只读固件基线） ----------------
    // L1 固件固化值  : MsrGuard 启动时备份的 0x610/0x774/0x1AD/0x150 原始值，只读基准，不可持久化。
    // L2 智能标定推荐: Settings.Calibration，只含通用安全参数，标定只更新这里，绝不覆盖 L3。
    // L3 用户编辑值  : Settings.Manual，高级参数页手动微调并成功写入后才保存，开机重放。
    // 固件默认值不可持久化修改；软件不运行 / 未授权时 CPU 按 L1 的固件默认散热逻辑运转。

    /// <summary>开机后是否自动把已保存的微调参数写入 MSR（需管理员 + 写入授权）。</summary>
    public bool ReplayOnStartup { get; set; } = true;

    /// <summary>L2：实测标定得到的热模型与推荐参数（只含通用安全参数）。</summary>
    public CalibrationSnapshot? Calibration { get; set; }
    /// <summary>L3：高级参数页手动微调并成功写入的参数（含个性化可选微调）。</summary>
    public ManualTuning? Manual { get; set; }

    /// <summary>
    /// 0x150 电压偏移的实机自检结果：null = 未探测，true/false = --voltage-probe 实测结论。
    /// 型号预筛只作提示，UI 是否开放该个性化项以本实测结果为准。
    /// </summary>
    public bool? VoltageWritable { get; set; }

    /// <summary>是否已完成过首次安装启动的环境自检（决定是否需要弹依赖提示）。</summary>
    public bool EnvironmentChecked { get; set; }
}

/// <summary>
/// L2 智能标定推荐快照。标定只用通用安全参数；电压偏移等个性化项不进入标定与应用标定流程。
/// VoltageContextMv 记录标定时机器的电压状态——降压会改变热模型，因此推荐值可能随个性化微调变化，
/// 重新标定只更新本快照，不触碰 L3 用户编辑值。
/// </summary>
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
    /// <summary>标定推荐 PL1（仅通用参数；0 = 未产生推荐值）。</summary>
    public double RecommendedPl1W { get; set; }
    /// <summary>标定推荐 EPP（仅通用参数；-1 = 未产生推荐值）。</summary>
    public int RecommendedEpp { get; set; } = -1;
    /// <summary>标定时机器的电压偏移上下文（mV，0 = 未施加降压）。</summary>
    public int VoltageContextMv { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>L3 用户编辑值。-1 = 该项本次未编辑、不写入、不重放。</summary>
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
    /// <summary>0x774 Min Perf，-1 = 保持原值。</summary>
    public int MinPerf { get; set; } = -1;
    /// <summary>0x774 Max Perf，-1 = 保持原值；不得超过出厂上限。</summary>
    public int MaxPerf { get; set; } = -1;
    /// <summary>0x774 Activity Window，-1 = 保持原值。</summary>
    public int ActivityWindow { get; set; } = -1;
    /// <summary>0x150 Core/Cache 电压偏移 mV；只允许 [-150, 0]，0 = 不施加（回零）。</summary>
    public int VoltageOffsetMv { get; set; }
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
