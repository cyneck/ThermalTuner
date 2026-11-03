using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ThermalPilot.App.Services;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Environment;

namespace ThermalPilot.App;

/// <summary>
/// 多档 PL1 阶梯「压测智能标定」（对应老版本 CalibrationEngine 的多阶段拟合思路）。
///
/// 做法：空载基线 → 依次把 PL1 下压到固件值的 100% / 80% / 60% / 45% / 35%
///       （每档真实写入 0x610、Tau 缩到 8s 让功耗墙生效）→ 满载稳态采样 → 冷却。
///       对每档的「实测包功耗 / 稳态温度」做最小二乘拟合 T = a + b·P，
///       再按目标温度上限反解可持续功率上限，给出推荐 PL1。
///
/// 与老版本差异：当前架构以 PL1/热阻为主控量，没有风扇曲线与倍频曲线编辑器，
/// 因此这里拟合的是 T(P) 而不是「降频%/风扇% → 温度」。推荐值不自动写 settings.json，
/// 由用户在高级参数页确认后应用并持久化（符合「软件侧标定值可持久化、固件默认不变」的模型）。
///
/// 用法：ThermalPilot.App.exe --calibrate-sweep --out report.txt --csv samples.csv
///                            [--seconds 25] [--ceiling 85]
/// 需要管理员权限；结束一定恢复固件默认值。
/// </summary>
internal static class CalibrationSweepProbe
{
    private const int IdleSeconds = 10;
    private const int CoolSeconds = 10;
    private const double SweepTauS = 8.0;

    // 相对固件 PL1 的阶梯比例：从满墙一路压到明显低于本机满载功耗
    private static readonly double[] Ratios = { 1.00, 0.80, 0.60, 0.45, 0.35 };

    private sealed record Row(
        DateTime At, string Stage, double ElapsedS,
        double TempC, double PowerW, double LoadPct, double Ghz,
        double FwPl1W, double TargetPl1W, int Threads);

    private sealed record StageStat(
        string Stage, double TargetPl1W, int Count,
        double TempC, double PowerW, double Ghz, double FwPl1W);

    public sealed record Options(
        string OutPath, string CsvPath, int Seconds, double CeilingC);

    public static int Run(Options opt)
    {
        var sb = new StringBuilder();
        var rows = new List<Row>();
        SessionState? session = null;
        int exit = 0;
        bool origAllow = false, origAuto = false, captured = false;

        try
        {
            session = new SessionState();
            session.SampleOnce();

            origAllow = session.Settings.AllowRealWrite;
            origAuto = session.Settings.AutoTakeover;
            captured = true;
            session.Settings.AllowRealWrite = true;
            session.Settings.AutoTakeover = false;

            double fwPl1 = session.FwPl1W;
            double fwPl2 = session.FwPl2W;
            double ceiling = double.IsNaN(opt.CeilingC) || opt.CeilingC < 50 || opt.CeilingC > 100
                ? session.CeilingC : opt.CeilingC;
            double savedRth = session.Settings.Calibration?.Rth ?? double.NaN;
            int fullThreads = Math.Max(2, Environment.ProcessorCount - 2);
            var env = HardwareProbe.Probe();

            sb.AppendLine("==========================================================");
            sb.AppendLine(" ThermalPilot 多档 PL1 阶梯压测智能标定");
            sb.AppendLine($" 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("== 1. 环境 ==");
            sb.AppendLine($"CPU                : {session.CpuName}");
            sb.AppendLine($"平台 / 虚拟化      : {session.PlatformText} / {env.Vendor} / 虚拟化={env.IsVirtualized}");
            sb.AppendLine($"权限               : {session.ElevationText}");
            sb.AppendLine($"MSR 通道           : {MsrChannel.BackendName} / {MsrChannel.StatusText(session.MsrStatus)}");
            sb.AppendLine($"固件出厂默认       : {session.FwDefaultText}");
            sb.AppendLine($"目标温度上限       : {F(ceiling, 0)}°C");
            sb.AppendLine($"已保存标定热阻     : {(double.IsNaN(savedRth) ? "--" : F(savedRth, 3) + " K/W")}");
            sb.AppendLine($"压测设置           : 每档 {opt.Seconds}s 满载 / {fullThreads} 线程 / Tau {F(SweepTauS, 0)}s");
            sb.AppendLine("说明               : 每档都真实写入 0x610 并回读校验；结束恢复固件默认值，推荐值不自动写盘。");
            sb.AppendLine();

            if (!session.MsrWritable)
            {
                sb.AppendLine("!! 通道不可写，无法执行阶梯标定。请以管理员运行并确认 WinRing0 驱动已加载。");
                sb.AppendLine("RESULT: 中止");
                return 2;
            }

            sb.AppendLine("== 2. 阶段 ==");
            RunStage(session, rows, "IDLE", false, 0, IdleSeconds, fullThreads);

            var stats = new List<StageStat>();
            for (int i = 0; i < Ratios.Length; i++)
            {
                double target = Math.Max(8, Math.Round(fwPl1 * Ratios[i] * 8) / 8.0); // 1/8 W 量化
                double pl2 = Math.Max(fwPl2, target * 1.25);
                var r = session.ApplyLimits(target, pl2, SweepTauS, writeTimeWindow: true);
                sb.AppendLine($"[档 {i + 1}/{Ratios.Length}] 目标 PL1 {F(target, 1)}W → {r.Outcome} / {r.Message}");
                if (!r.Ok) continue;

                string stage = $"SWEEP_{i + 1}";
                RunStage(session, rows, stage, true, target, opt.Seconds, fullThreads);
                var st = Summarize(rows, stage, target);
                if (st is not null) stats.Add(st);
            }

            session.SetStress(false);
            RunStage(session, rows, "COOL", false, 0, CoolSeconds, fullThreads);
            session.SetStress(false);

            sb.AppendLine();
            sb.AppendLine("== 3. 各档稳态（每档后 60% 样本均值）==");
            sb.AppendLine(PadR("阶段", 12) + PadL("目标PL1W", 10) + PadL("实读PL1W", 11) +
                          PadL("实测功耗W", 11) + PadL("稳态温度°C", 12) + PadL("均频GHz", 10));
            foreach (var s in stats)
                sb.AppendLine(PadR(s.Stage, 12) + PadL(F(s.TargetPl1W, 1), 10) + PadL(F(s.FwPl1W, 1), 11) +
                              PadL(F(s.PowerW, 2), 11) + PadL(F(s.TempC, 2), 12) + PadL(F(s.Ghz, 2), 10));

            sb.AppendLine();
            sb.AppendLine("== 4. 拟合与推荐 ==");
            if (stats.Count >= 3)
            {
                var pts = stats.Select(s => (x: s.PowerW, y: s.TempC))
                               .Where(p => !double.IsNaN(p.x) && !double.IsNaN(p.y) && p.x > 1)
                               .ToList();
                var (a, b, r2) = FitLinear(pts);
                sb.AppendLine($"拟合式             : T(°C) = {F(a, 2)} + {F(b, 3)} × P(W)，n={pts.Count}，R²={F(r2, 3)}");
                sb.AppendLine($"斜率含义           : 每 1W 实测包功耗带来 {F(b, 3)}°C 温升（短时阶梯，含热惯性）");

                if (b > 0.05)
                {
                    double pMax = (ceiling - a) / b;
                    double recommended = Math.Clamp(Math.Round(pMax * 8) / 8.0, 8, fwPl1);
                    sb.AppendLine($"反解 {F(ceiling, 0)}°C 上限    : 可持续包功耗 ≤ {F(pMax, 1)}W");
                    sb.AppendLine($"推荐 PL1           : {F(recommended, 1)}W（固件默认 {F(fwPl1, 1)}W，" +
                                  $"{(recommended < fwPl1 ? "低于" : "不低于")}默认值）");
                    sb.AppendLine("推荐值用法         : 在「高级参数」页填入 PL1 并点应用，软件会写入 0x610 并持久化；");
                    sb.AppendLine("                     固件默认值不变，软件不运行时 CPU 按固件默认散热逻辑运转。");
                }
                else
                {
                    sb.AppendLine("!! 拟合斜率过小或为负：各档功耗/温度未拉开（可能未顶到功耗墙或热惯性主导），");
                    sb.AppendLine("   本次不给出可靠推荐值，建议增大档位跨度或延长每档时长后重跑。");
                }

                if (!double.IsNaN(savedRth))
                    sb.AppendLine($"与已保存标定对比   : 本次短时拟合斜率 {F(b, 3)} K/W vs 两点稳态标定 Rth {F(savedRth, 3)} K/W；" +
                                  "短时斜率偏小属热容预期，长期稳态外推仍以稳态标定值为准。");
            }
            else
            {
                sb.AppendLine("有效档位不足 3 个，无法拟合。");
            }

            sb.AppendLine();
            sb.AppendLine("== 5. 收尾恢复 ==");
            var restore = session.RestoreOriginal();
            sb.AppendLine($"0x610 恢复固件默认值 : {restore.Outcome} / {restore.Message}");
            session.SampleOnce();
            sb.AppendLine($"最终 0x610 实读       : PL1 {session.FwPl1Text}W · PL2 {session.FwPl2Text}W · Tau {session.FwTauText}");
            sb.AppendLine("压测负载             : 已停止");
            sb.AppendLine();
            sb.AppendLine("RESULT: 阶梯标定完成");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine($"!! 异常: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            sb.AppendLine("RESULT: 异常中止");
            exit = 1;
        }
        finally
        {
            try { session?.SetStress(false); } catch { /* 忽略 */ }
            if (captured && session is not null)
            {
                try
                {
                    session.Settings.AllowRealWrite = origAllow;
                    session.Settings.AutoTakeover = origAuto;
                    session.RestoreOriginal();
                }
                catch { /* 忽略 */ }
            }
            try { session?.Dispose(); } catch { /* 忽略 */ }
            Write(opt.OutPath, sb.ToString());
            Write(opt.CsvPath, BuildCsv(rows));
        }

        return exit;
    }

    private static void RunStage(SessionState session, List<Row> rows,
        string stage, bool load, double targetPl1, int seconds, int threads)
    {
        session.SetStress(load, threads);
        var sw = Stopwatch.StartNew();
        int interval = Math.Max(250, session.Settings.IntervalMs);
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            session.SampleOnce();
            rows.Add(new Row(DateTime.Now, stage, sw.Elapsed.TotalSeconds,
                session.TempC, session.PowerW, session.LoadPct, session.ClockGhz,
                session.FwPl1W, targetPl1, threads));
            Thread.Sleep(interval);
        }
    }

    private static StageStat? Summarize(List<Row> rows, string stage, double targetPl1)
    {
        var set = rows.Where(r => r.Stage == stage).ToList();
        if (set.Count == 0) return null;
        int skip = (int)(set.Count * 0.4);
        var steady = set.Skip(skip).ToList();
        if (steady.Count == 0) steady = set;
        return new StageStat(stage, targetPl1, steady.Count,
            Avg(steady, r => r.TempC), Avg(steady, r => r.PowerW),
            Avg(steady, r => r.Ghz), Avg(steady, r => r.FwPl1W));
    }

    /// <summary>一元线性回归 y = a + b·x，返回 (a, b, R²)。</summary>
    private static (double a, double b, double r2) FitLinear(IReadOnlyList<(double x, double y)> pts)
    {
        int n = pts.Count;
        if (n < 2) return (0, 0, 0);
        double sx = 0, sy = 0;
        foreach (var p in pts) { sx += p.x; sy += p.y; }
        double mx = sx / n, my = sy / n;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            double dx = p.x - mx, dy = p.y - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        if (sxx < 1e-9) return (my, 0, 0);
        double b = sxy / sxx;
        double a = my - b * mx;
        double r2 = syy < 1e-9 ? 1 : (sxy * sxy) / (sxx * syy);
        return (a, b, Math.Clamp(r2, 0, 1));
    }

    private static double Avg(IEnumerable<Row> rows, Func<Row, double> pick)
    {
        var v = rows.Select(pick).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        return v.Count == 0 ? double.NaN : v.Average();
    }

    private static string F(double v, int digits) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "--" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static string PadR(string s, int width) => width <= s.Length ? s : s.PadRight(width);
    private static string PadL(string s, int width) => width <= s.Length ? s : s.PadLeft(width);

    private static string BuildCsv(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,阶段,阶段内秒,温度(°C),包功耗(W),负载(%),最高核频(GHz),0x610实读PL1(W),目标PL1(W),压测线程数");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                r.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Stage,
                r.ElapsedS.ToString("F1", CultureInfo.InvariantCulture),
                F(r.TempC, 2), F(r.PowerW, 2), F(r.LoadPct, 1), F(r.Ghz, 3),
                F(r.FwPl1W, 2), F(r.TargetPl1W, 2),
                r.Threads.ToString(CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    private static void Write(string path, string content)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠退出码判断 */ }
    }
}
