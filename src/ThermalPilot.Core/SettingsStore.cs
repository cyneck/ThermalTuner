using System.Text;
using System.Text.Json;

namespace ThermalPilot.Core;

/// <summary>settings.json 落盘（%ProgramData%\ThermalPilot）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public sealed class AppSettings
    {
        public ThermalConfig Config { get; set; } = Presets.Create("均衡");
        /// <summary>
        /// 配置档：内置基线（模型给定、只读）+ 用户增量（高级参数的手改）。
        /// 与 Config 同时落盘 —— Config 是展开后的完整值，Profile 记住"哪几项是自定义的"，
        /// 这样升级程序后没改过的项能跟随新基线。见 ProfileInstance 注释。
        /// </summary>
        public ProfileInstance? Profile { get; set; }
        public bool AutoStart { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public string CalibrationLevel { get; set; } = "标准";
        public string CalibrationPreset { get; set; } = "均衡";
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var json = File.ReadAllText(Paths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (s?.Config != null)
                {
                    s.Config.FanCurve.Normalize();
                    s.Config.FreqCurve.Normalize();
                    if (s.Config.FanCurve.Points.Count == 0) s.Config.FanCurve = Presets.Fan(s.Config.ProfileName);
                    if (s.Config.FreqCurve.Points.Count == 0) s.Config.FreqCurve = Presets.Freq(s.Config.ProfileName);

                    // 安全线是模型基线（TJmax−5），老版本允许设到 99℃，这里统一拉回 95℃
                    if (s.Config.CriticalTempC > ParamConstraints.SafetyCeilingC)
                    {
                        Log.Warn($"安全线 {s.Config.CriticalTempC:0}℃ 高于模型上限 {ParamConstraints.SafetyCeilingC:0}℃，已按基线校正。");
                        s.Config.CriticalTempC = ParamConstraints.SafetyCeilingC;
                    }

                    // 配置档对账：老配置没有 Profile 就从当前值抽一份；有的话由基线重建 + 叠加增量
                    bool changed;
                    s.Profile = ProfileInstance.Reconcile(s.Profile, s.Config, out changed);
                    if (changed) Save(s);

                    Log.Info($"已加载配置文件（配置档：{s.Profile.DisplayName}）。");
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取配置失败，使用默认值：{ex.Message}");
        }

        // 首次运行 / 配置损坏：默认值也要落一份配置档，保证后续有"基线 + 增量"可对账
        var fresh = new AppSettings();
        fresh.Profile = ProfileInstance.Reconcile(null, fresh.Config, out _);
        return fresh;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(settings, Options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存配置失败：{ex.Message}");
        }
    }
}

/// <summary>一键导出"本机支持能力 + 传感器清单"，排障时直接发这个文件。</summary>
public static class SupportReport
{
    public static string Build(HardwareService hw, PowerManager power, UndervoltService uv, ThermalConfig cfg,
                               CpuControlService? cpu = null, ProfileInstance? profile = null)
    {
        var sb = new StringBuilder();

        sb.Append(EnvironmentCheck.ToText(EnvironmentCheck.Run(hw, power, uv, cpu)));
        sb.AppendLine();
        sb.AppendLine("---- 硬件设备 ----");
        foreach (var h in hw.HardwareNames) sb.AppendLine("  " + h);
        sb.AppendLine();
        sb.AppendLine("---- 传感器清单（硬件/类型/名称/当前值）----");
        foreach (var s in hw.SensorInventory) sb.AppendLine("  " + s);
        sb.AppendLine();

        var snap = hw.Read();
        sb.AppendLine("---- CPU 平台能力（决定高级参数是否开放）----");
        sb.AppendLine(CpuCapabilities.Refresh(cpu?.Msr).Report());
        sb.AppendLine();
        sb.AppendLine("---- 实时采样 ----");
        sb.AppendLine($"  CPU {snap.CpuTemp:F1}°C / 负载 {snap.CpuLoad:F0}% / {snap.CpuFrequencyMhz:F0}MHz / 封装功耗 {snap.CpuPower:F1}W");
        sb.AppendLine($"  GPU {snap.GpuTemp:F1}°C / 负载 {snap.GpuLoad:F0}% / 功耗 {snap.GpuPower:F1}W");
        sb.AppendLine($"  风扇通道 {snap.Fans.Count} 个：" +
                      (snap.Fans.Count == 0
                          ? "无"
                          : string.Join(" | ", snap.Fans.Select(f => $"{f.Name}={f.Rpm}RPM{(f.Controllable ? " 可调速" : "")}"))));
        sb.AppendLine();
        sb.AppendLine("---- 当前配置 ----");
        if (profile != null)
        {
            sb.AppendLine($"  配置档 {profile.DisplayName}（内置基线「{profile.Base}」+ {profile.Delta.Count} 项参数增量"
                          + (profile.Curves.Count > 0 ? $"，{profile.Curves.Count} 条曲线增量" : "") + "）");
            foreach (var kv in profile.Delta)
                sb.AppendLine($"    · {kv.Key} = {kv.Value}");
        }
        sb.AppendLine($"  预设 {cfg.ProfileName}；模式 {(cfg.TargetTempMode ? $"目标温度 {cfg.TargetTempC}°C (Kp={cfg.Kp}, Ki={cfg.Ki})" : "曲线")}");
        sb.AppendLine($"  风扇控制={cfg.EnableFanControl}，降频={cfg.EnableFrequencyControl}，降压={cfg.EnableUndervolt}({cfg.UndervoltMv}mV)");
        sb.AppendLine($"  CPU 精细调控={cfg.EnableCpuTuning}：PL1={cfg.CpuPl1W}W PL2={cfg.CpuPl2W}W τ={cfg.CpuTauS}s EPP={cfg.CpuEpp} 睿频上限={cfg.CpuTurboRatio}");
        sb.AppendLine($"  智能自动={cfg.AutoCpuMode}（{cfg.AutoCpuProfile}，目标 {cfg.AutoTargetC}℃，室温 {cfg.AmbientC}℃，预览={cfg.AutoCpuDryRun}）");
        sb.AppendLine($"  回差 {cfg.FanHysteresisC}°C，升速 {cfg.FanRampUpPctPerSec}%/s，降速 {cfg.FanRampDownPctPerSec}%/s，保护 {cfg.CriticalTempC}°C");
        sb.AppendLine("---- 平台功率（PL1/PL2/GPU 的上限来源）----");
        // 报告可能在任何时刻导出（甚至引擎没起），先按当前配置刷一遍，保证"来源"这一列是真探测结果
        PlatformPower.Apply(cfg, cpu?.Msr);
        sb.AppendLine($"  手填：CPU 设计功耗={cfg.CpuDesignW}W（0=自动），独显设计功耗={cfg.GpuDesignW}W（0=自动）");
        foreach (var line in PlatformPower.Detail)
            sb.AppendLine("  " + line);
        if (!string.IsNullOrWhiteSpace(cfg.LastCalibrationNote)) sb.AppendLine("  标定记录：" + cfg.LastCalibrationNote);
        sb.AppendLine();
        sb.AppendLine($"日志目录：{Log.LogDirectory}");
        sb.AppendLine($"配置文件：{Paths.SettingsFile}");
        return sb.ToString();
    }
}
