using System.IO;
using System.Text.Json;

namespace ThermalTuner.App.Services;

/// <summary>调优模式常量。持久化用字符串，避免枚举值变更导致旧配置反序列化失败。</summary>
public static class TuningModes
{
    public const string Intelligent = "intelligent";
    public const string Manual = "manual";

    public static string Normalize(string? value) =>
        string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase) ? Manual : Intelligent;
}

/// <summary>用户设置（持久化到 %LOCALAPPDATA%\ThermalTuner\settings.json）。</summary>
public sealed class AppSettings
{
    public int IntervalMs { get; set; } = 1000;
    public string Profile { get; set; } = "balanced";
    public double CeilingC { get; set; } = 88.0;

    // ---------------- v0.5.0 调优模式（单一真值源） ----------------
    /// <summary>
    /// 调优模式单一真值源：TuningModes.Intelligent / TuningModes.Manual。
    /// 空字符串 = 旧版配置，启动时按「是否存在 L3 手动值」迁移：
    /// 有值 → manual（保持旧行为，不擅自改成智能写）；无值 → intelligent。
    /// </summary>
    public string TuningMode { get; set; } = string.Empty;
    /// <summary>
    /// 升档冷却秒数（默认 20s，设置页可调，钳制 5~120s）。
    /// 只约束「升档」写入，让 RAPL 的 PL1 平均功率窗口按 Tau 收敛；降档与高温降档不受限。
    /// </summary>
    public double RampUpCooldownSeconds { get; set; } = 20.0;
    /// <summary>
    /// v0.5.0 起语义变更：不再是「是否允许开始写」，而是
    /// 「是否允许决策树在选中即下发之后，继续每拍动态微调」。
    /// 选中档位 / 手工保存这两条显式事件通道不受它约束。
    /// </summary>
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

    // ---------------- 内置 EC 只读映射（可选覆盖） ----------------
    // 0 = 使用 EcFanReader 里的本机校准默认值（寄存器 149 / 原始刻度 255~58）。
    // 这些是「EC 档位刻度」映射，不是瓦数；换机型须用 --ec-probe 或 NBFC 配置重新确认。
    /// <summary>风扇档位 EC 寄存器地址；0 = 用默认 149。</summary>
    public int FanEcRegister { get; set; }
    /// <summary>EC 原始值对应 0% 的刻度；0 = 用默认 255。</summary>
    public double FanEcMinRaw { get; set; }
    /// <summary>EC 原始值对应 100% 的刻度；0 = 用默认 58。</summary>
    public double FanEcMaxRaw { get; set; }
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
    private static readonly string Dir = ResolveDataDirectory();

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDirectory => Dir;
    public static string SettingsPath => FilePath;
    /// <summary>旧版数据目录（产品更名前）。只用于迁移，不再写入。</summary>
    public static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalPilot");

    /// <summary>
    /// 产品更名后的平滑迁移：旧版把配置写在 %LOCALAPPDATA%\ThermalPilot。
    /// 首次启动把旧目录内容**复制**到新目录（不覆盖、不删除旧目录），
    /// 保住实测标定值、手动微调、MSR 出厂基线备份、能力快照与审计日志。
    /// 用标记文件保证只迁一次；失败不阻断启动（最坏回到默认设置，固件默认值不受影响）。
    /// </summary>
    private static string ResolveDataDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string next = Path.Combine(local, "ThermalTuner");
        string legacy = Path.Combine(local, "ThermalPilot");
        string stamp = Path.Combine(next, "migrated-from-legacy.txt");
        try
        {
            if (!Directory.Exists(legacy) || File.Exists(stamp)) return next;

            Directory.CreateDirectory(next);
            foreach (string src in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(legacy, src);
                string dst = Path.Combine(next, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst)) File.Copy(src, dst);
            }
            File.WriteAllText(stamp,
                $"已从旧版数据目录复制配置：{legacy}{Environment.NewLine}" +
                $"迁移时间：{DateTime.Now:O}{Environment.NewLine}" +
                "旧目录保留未删除；确认新版正常后可手动清理。" + Environment.NewLine);
        }
        catch
        {
            // 迁移失败不阻断启动
        }
        return next;
    }

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
