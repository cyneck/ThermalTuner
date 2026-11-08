using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ThermalTuner.App.Services;

/// <summary>启动闸门的三种结果。</summary>
public enum StartupMode
{
    /// <summary>温度 / 功耗 / 写入通道都可用：可以正常微调。</summary>
    Ready,
    /// <summary>能监测但调不了（通常是受限权限）：进入界面但停用调优，并给出提权入口。</summary>
    ObserveOnly,
    /// <summary>连温度都读不到：不进入主界面，停在初始化页给修复动作。</summary>
    Blocked
}

public enum StartupStepState { Pending, Active, Done, Failed, Skipped }

public sealed record StartupProgress(int Index, StartupStepState State, string Detail = "");

/// <summary>
/// 启动计划：初始化页据此决定是「进入界面」还是「停在修复页」，
/// 并把已经打开的遥测实例交给会话复用，避免第二次枚举传感器。
/// </summary>
public sealed record StartupPlan(
    StartupMode Mode,
    CapabilitySnapshot? Capability,
    HardwareTelemetry? Telemetry,
    bool FromCache,
    TimeSpan Elapsed,
    string Headline,
    string Detail,
    bool TempReadable,
    bool PowerMeasured,
    bool ChannelWritable)
{
    public bool CanEnter => Mode != StartupMode.Blocked;
}

/// <summary>
/// 启动编排：先给一帧界面，再做真正需要时间的事。
///
/// 设计要点（与旧版「构造函数里同步跑完全部检测」的区别）：
/// 1. 能力快照按「版本 + 机器 + 权限」做指纹缓存，命中就直接用，不再每次启动重跑；
/// 2. 传感器枚举（约 2 秒，主要耗时）在缓存命中时转到后台，界面先出来，卡片显示「正在读取」；
/// 3. 只有首次运行 / 版本升级 / 换机器 / 权限变化 / 快照超过 30 天，才在初始化页里同步等;
/// 4. 闸门以「决策树依赖的参数」为准：温度与功耗上限读不到就不进主界面，避免出现「能调但其实没数据」的假象。
/// </summary>
public static class StartupCoordinator
{
    public static readonly string[] StepTitles =
    {
        "载入个人设置",
        "确认硬件与参数能力",
        "准备监测数据"
    };

    public static async Task<StartupPlan> RunAsync(
        IProgress<StartupProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        void Step(int i, StartupStepState st, string detail = "") =>
            progress?.Report(new StartupProgress(i, st, detail));

        // ---- 1. 个人设置：纯文件读取，毫秒级 ----
        Step(0, StartupStepState.Active);
        try { SettingsStore.Load(); }
        catch { /* 配置损坏时按默认值运行，不阻塞启动 */ }
        Step(0, StartupStepState.Done);

        // ---- 2. 能力快照：能复用就不重算 ----
        Step(1, StartupStepState.Active);
        bool cached = EnvironmentSelfCheck.TryLoadValid(out CapabilitySnapshot? snap, out string cacheReason);
        HardwareTelemetry? telemetry = null;
        bool temp, power;

        if (cached && snap is not null)
        {
            temp = snap.CanRead("Temp");
            power = snap.CanRead("Power");
            Step(1, StartupStepState.Done, $"沿用 {snap.CheckedAt:yyyy-MM-dd HH:mm} 的检测结果");
        }
        else
        {
            Step(1, StartupStepState.Active, cacheReason);
            // 判定温度 / 功耗可读性需要一次真实采样，这一步只能同步做（首次运行才走）
            telemetry = await Task.Run(() => new HardwareTelemetry(), ct).ConfigureAwait(true);
            TelemetrySample sample = await Task.Run(() => telemetry.Read(), ct).ConfigureAwait(true);
            temp = !double.IsNaN(sample.TempC);
            power = sample.PowerMeasured;
            string hint =
                $"温度 {(temp ? "可读" : "不可读")} · 功耗 {(power ? "实测" : "不可用")}";
            snap = await Task.Run(
                () => EnvironmentSelfCheck.Run(false, hint, temp, power), ct).ConfigureAwait(true);
            Step(1, StartupStepState.Done, cacheReason);
        }

        // ---- 3. 闸门 ----
        bool plOk = snap?.CanRead("PL1") ?? false;
        bool channelWritable = snap?.ChannelWritable ?? false;
        bool elevated = snap?.Elevated ?? false;

        (StartupMode mode, string headline, string detail) = Decide(
            temp, power, plOk, channelWritable, elevated);

        Step(2, StartupStepState.Done,
            telemetry is not null ? "传感器已就绪" : "传感器在后台读取");

        var plan = new StartupPlan(mode, snap, telemetry, cached, clock.Elapsed,
            headline, detail, temp, power, channelWritable);
        Record(plan);
        return plan;
    }

    /// <summary>进入托管入口的时刻：用来把「运行时 + WPF 初始化」与「本程序自己的准备」分开算账。</summary>
    public static void MarkEntry()
    {
        try
        {
            long age = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  进入主流程：进程启动后 {age} ms");
        }
        catch { /* 取不到启动时间就不记 */ }
    }

    /// <summary>启动页真正显示出来的时刻：用户「感觉有反应」的起点。</summary>
    public static void MarkSplashShown()
    {
        try
        {
            long age = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppendLine($"     └ 启动页显示：进程启动后 {age} ms");
        }
        catch { /* 取不到启动时间就不记 */ }
    }

    /// <summary>
    /// 记一行启动耗时，供诊断页与后续排障使用。只保留最近 40 行，避免无限增长。
    /// </summary>
    private static void Record(StartupPlan plan)
    {
        AppendLine(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  准备用时 {plan.Elapsed.TotalMilliseconds:F0} ms" +
            $"  缓存 {(plan.FromCache ? "命中" : "未命中")}" +
            $"  结果 {plan.Mode}" +
            $"  温度 {(plan.TempReadable ? "可读" : "不可读")}" +
            $"  功耗 {(plan.PowerMeasured ? "实测" : "不可用")}");
    }

    /// <summary>主界面真正显示出来的时刻（含进程启动与 WPF 初始化），用于衡量「点开要等多久」。</summary>
    public static void RecordMainWindow(string? phases = null)
    {
        try
        {
            long age = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppendLine($"     └ 主界面显示：进程启动后 {age} ms" +
                       (string.IsNullOrEmpty(phases) ? string.Empty : $"（{phases}）"));
        }
        catch { /* 取不到启动时间就不记 */ }
    }

    private static void AppendLine(string line)
    {
        try
        {
            string path = Path.Combine(SettingsStore.DataDirectory, "startup-log.txt");
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            lines.Add(line);
            if (lines.Count > 40) lines.RemoveRange(0, lines.Count - 40);
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
        }
        catch { /* 日志失败不影响启动 */ }
    }

    private static (StartupMode, string, string) Decide(
        bool temp, bool power, bool plOk, bool channelWritable, bool elevated)
    {
        if (!temp)
        {
            return (StartupMode.Blocked, "环境未就绪",
                elevated
                    ? "读不到 CPU 温度，无法判断散热状态，微调已停用。请重新安装调优组件后重启程序。"
                    : "读不到 CPU 温度：读取温度需要管理员权限。请以管理员身份重新启动。");
        }
        // 权限不足时「读不到功耗参数」是预期结果，不是环境坏了：进观测模式并给提权入口，
        // 否则普通用户每次双击都会卡在初始化页。
        if (!elevated)
        {
            return (StartupMode.ObserveOnly, "环境已就绪 · 仅观测",
                "当前是普通权限，可以查看全部指标，但读不到实测功耗、也不能改设置。需要微调时请以管理员身份重新启动。");
        }
        if (!plOk)
        {
            return (StartupMode.Blocked, "环境未就绪",
                "读不到功耗上限，无法判断当前设置，微调已停用。请检查调优组件是否安装完整后重启程序。");
        }
        if (!power)
        {
            return (StartupMode.ObserveOnly, "环境已就绪 · 仅观测",
                "读不到实测功耗，无法做热阻标定，微调按模型估算运行。可先查看「诊断与日志」里的功耗来源说明。");
        }
        if (!channelWritable)
        {
            return (StartupMode.ObserveOnly, "环境已就绪 · 仅观测",
                "调优通道当前只读，参数可以查看但不能下发。请到「诊断与日志」检查通道状态。");
        }
        return (StartupMode.Ready, "环境已就绪", "温度、功耗与调优通道均可用，可以直接微调。");
    }
}
