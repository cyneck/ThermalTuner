using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using ThermalTuner.Core;
using ThermalTuner.Core.Environment;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Monitor;

/// <summary>
/// 实时监控入口：读取本机真实温度/负载/功耗 → 驱动决策树 → 每秒输出一次控制决策。
/// 只读，不写 MSR，不改硬件。
///
/// 用法：ThermalTuner.Monitor [--seconds N] [--interval MS] [--profile silent|balanced|performance] [--out FILE]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // 输出被重定向（管道/文件）时统一 UTF-8，避免中文代码页不一致导致乱码
        if (Console.IsOutputRedirected)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 忽略 */ }
        }

        var opt = Options.Parse(args);
        if (opt is null)
        {
            Console.Error.WriteLine("参数错误。用法: ThermalTuner.Monitor [--seconds N] [--interval MS] [--profile silent|balanced|performance] [--out FILE]");
            return 2;
        }

        using var log = opt.OutPath is null ? null : new StreamWriter(opt.OutPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        void Emit(string line)
        {
            Console.WriteLine(line);
            log?.WriteLine(line);
        }

        // ---------- 环境探测 ----------
        var env = HardwareProbe.Probe();
        var limits = HardwareLimitResolver.Resolve(env);

        Emit("===== ThermalTuner Monitor =====");
        Emit($"CPU  : {env.BrandString}");
        Emit($"平台 : 代数 {env.Generation} · 后缀 {(string.IsNullOrEmpty(env.Suffix) ? "-" : env.Suffix)} · " +
             $"{env.FormFactor} · {(env.IsHybridArchitecture ? "混合架构" : "传统架构")} · {env.PhysicalCores}P/{env.LogicalCores}L");
        Emit($"约束 : TjMax {limits.TjMax:F0}°C (安全线 {limits.MaxSafeCeiling:F0}°C) · " +
             $"PL1 [{limits.MinPL1:F0},{limits.MaxPL1:F0}]W · PL2 [{limits.MinPL2:F0},{limits.MaxPL2:F0}]W");
        Emit($"模式 : profile={opt.Profile} · 采样 {opt.IntervalMs}ms · " +
             $"{(opt.Seconds > 0 ? $"运行 {opt.Seconds}s" : "Ctrl+C 结束")} · " +
             $"{(opt.Simulate ? "合成热模型(真实负载驱动)" : "只读监控(不写硬件)")}");

        bool elevated = IsElevated();
        using var telemetry = new TelemetrySource();
        if (opt.Simulate)
            Emit($"遥测 : 系统负载 (GetSystemTimes) + 合成热模型 · 权限: {(elevated ? "管理员" : "普通用户")}");
        else
            Emit($"遥测 : {(telemetry.DriverAvailable ? "LibreHardwareMonitor 驱动 (封装温度/包功耗/频率)" : "WMI 回退 (仅温度，可能不可用)")}" +
                 $" · 权限: {(elevated ? "管理员" : "普通用户")}");

        if (!elevated && !telemetry.DriverAvailable)
            Emit("[提示] 读取 CPU 封装温度/功耗需要管理员权限，请用「以管理员身份运行」重试。");

        if (opt.Diag)
        {
            Emit("");
            Emit("===== 传感器诊断 =====");
            foreach (var line in telemetry.DumpSensors()) Emit(line);
            return 0;
        }

        Emit("");
        Emit("时间      温度    负载   包功耗   PL1      方向  命中规则           因果依据");
        Emit(new string('-', 128));

        var controller = new AutoCpuController(limits);
        double ceiling = AutoCpuController.TargetCForProfile(opt.Profile);
        var plant = opt.Simulate ? new SyntheticPlant() : null;
        using var stress = opt.Stress ? new StressLoad(Environment.ProcessorCount) : null;
        if (stress != null)
            Emit($"[压测] 已启动 {stress.ThreadCount} 个后台线程制造真实 CPU 负载，进程退出自动停止");
        double dtSeconds = opt.IntervalMs / 1000.0;
        double appliedPl1 = Math.Clamp(35.0, limits.MinPL1, limits.MaxPL1);

        var stats = new Stats();
        var sw = Stopwatch.StartNew();
        double prevTemp = double.NaN;
        bool warnedNoTemp = false;
        int tick = 0;

        while (true)
        {
            if (opt.Seconds > 0 && sw.Elapsed.TotalSeconds >= opt.Seconds) break;

            var s = telemetry.Read();
            double temp = s.TempC, power = s.PowerW, load = s.LoadPercent, clock = s.MaxCoreGhz;

            if (plant != null)
            {
                var (t, p) = plant.Step(load, appliedPl1, dtSeconds);
                temp = t; power = p; clock = 0;
            }

            if (double.IsNaN(temp))
            {
                if (!warnedNoTemp)
                {
                    Emit("[警告] 未读到 CPU 温度。常见原因：未按管理员启动，或 PawnIO 驱动/设备对象未就绪。");
                    Emit("       解决：以管理员身份重试；在「诊断与日志」页确认 PawnIO 后端与模块来源；");
                    Emit("       或先用 `--simulate` 以真实负载驱动合成热模型，验证决策回路。");
                    warnedNoTemp = true;
                }
                Emit($"{DateTime.Now:HH:mm:ss}  T=  N/A  load={Fmt(load),4}%  —— 温度不可用，跳过本拍");
                Sleep(opt.IntervalMs);
                continue;
            }

            double dtDt = double.IsNaN(prevTemp) ? 0.0
                : Math.Clamp((temp - prevTemp) / dtSeconds, -5, 5);
            prevTemp = temp;

            // 包功耗：驱动读数为真实值；WMI 回退时为粗估
            if (double.IsNaN(power)) power = 5.0;

            var dec = controller.Update(
                tempC: temp,
                powerW: power,
                ceilingC: ceiling,
                ambientC: 25.0,
                profile: opt.Profile,
                // MaxTurboGhz 传 0：芯片真实睿频未知时不做频率墙判定，避免误判
                perf: new PerfSample { CoreClocksGhz = clock, MaxTurboGhz = 0 },
                loadPct: double.IsNaN(load) ? 50.0 : load,
                dTdt: dtDt);

            stats.Add(temp, load, dec.Pl1W, dec.Reason);
            appliedPl1 = dec.Pl1W;   // 闭环：本拍决策的 PL1 作用于下一拍功耗
            tick++;

            string arrow = dec.Direction switch { > 0 => "↑", < 0 => "↓", _ => "→" };
            Emit($"{DateTime.Now:HH:mm:ss}  T={temp,5:F1}°C {Fmt(load),4}%  " +
                 $"P≈{power,4:F0}W  {dec.Pl1W,5:F1}W {arrow}  {dec.Limit,-14}  {dec.Reason}");

            Sleep(opt.IntervalMs);
        }

        Emit("");
        if (tick == 0)
        {
            Emit("===== 汇总：无有效采样（温度不可用）。请以管理员身份运行后重试。");
            return 3;
        }

        Emit($"===== 汇总：{tick} 拍 · 温度 {stats.MinTemp:F1}~{stats.MaxTemp:F1}°C · " +
             $"平均负载 {stats.AvgLoad:F0}% · PL1 区间 {stats.MinPl1:F1}~{stats.MaxPl1:F1}W");
        if (stats.RuleCounts.Count > 0)
            Emit("命中分布：" + string.Join(" · ", stats.RuleCounts.Select(kv => $"{kv.Key} {kv.Value} 拍")));
        return 0;
    }

    private static string Fmt(double v) => double.IsNaN(v) ? " N/A" : v.ToString("F0");

    private static void Sleep(int ms) => Thread.Sleep(Math.Max(100, ms));

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private sealed class Stats
    {
        public double MinTemp { get; private set; } = double.MaxValue;
        public double MaxTemp { get; private set; } = double.MinValue;
        public double MinPl1 { get; private set; } = double.MaxValue;
        public double MaxPl1 { get; private set; } = double.MinValue;
        public double AvgLoad { get; private set; }
        public Dictionary<string, int> RuleCounts { get; } = new();
        private int _n;

        public void Add(double temp, double load, double pl1, string reason)
        {
            MinTemp = Math.Min(MinTemp, temp);
            MaxTemp = Math.Max(MaxTemp, temp);
            MinPl1 = Math.Min(MinPl1, pl1);
            MaxPl1 = Math.Max(MaxPl1, pl1);
            if (!double.IsNaN(load)) AvgLoad = (AvgLoad * _n + load) / (_n + 1);
            _n++;

            int l = reason.IndexOf('[', StringComparison.Ordinal);
            int r = reason.IndexOf(']', StringComparison.Ordinal);
            string rule = l >= 0 && r > l ? reason[(l + 1)..r] : "Unknown";
            RuleCounts[rule] = RuleCounts.GetValueOrDefault(rule) + 1;
        }
    }

    private sealed record Options(int Seconds, int IntervalMs, string Profile, string? OutPath, bool Diag, bool Simulate, bool Stress)
    {
        public static Options? Parse(string[] args)
        {
            int seconds = 0, interval = 1000;
            string profile = "balanced";
            string? outPath = null;
            bool diag = false, simulate = false, stress = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seconds" when i + 1 < args.Length && int.TryParse(args[++i], out int s) && s > 0:
                        seconds = s; break;
                    case "--interval" when i + 1 < args.Length && int.TryParse(args[++i], out int iv) && iv >= 100:
                        interval = iv; break;
                    case "--profile" when i + 1 < args.Length:
                        var p = args[++i].ToLowerInvariant();
                        if (p is not ("silent" or "balanced" or "performance")) return null;
                        profile = p; break;
                    case "--out" when i + 1 < args.Length:
                        outPath = args[++i]; break;
                    case "--diag":
                        diag = true; break;
                    case "--simulate":
                        simulate = true; break;
                    case "--stress":
                        stress = true; break;
                    default:
                        return null;
                }
            }
            return new Options(seconds, interval, profile, outPath, diag, simulate, stress);
        }
    }
}
