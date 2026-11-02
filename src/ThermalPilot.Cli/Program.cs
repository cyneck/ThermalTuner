using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThermalPilot.Core;

namespace ThermalPilot.Cli;

// ---------------------------------------------------------------------------
// tplab —— ThermalPilot 实验台（方法论验证用，不依赖 UI）
//
//   tplab probe                      探测硬件 / 风扇后端能力
//   tplab report                     生成 support-report（含新增变量）
//   tplab fan --status | --set 60 | --restore
//   tplab lab --plan <json> [--out dir] [--tag name]
//
// 设计目标：把"压测 → 采集 → 落盘"做成可复现的批处理，分析交给 hosts 侧脚本，
// 这样方法论可以在换算法、换机型时反复验证。
// ---------------------------------------------------------------------------

public sealed class LabPlan
{
    public string Name { get; set; } = "doe";
    public int SampleMs { get; set; } = 1000;
    public List<LabStage> Stages { get; set; } = new();
}

public sealed class LabStage
{
    public string Id { get; set; } = "S";
    public string Label { get; set; } = "";
    public int Sec { get; set; } = 120;
    /// <summary>idle | cpu</summary>
    public string Load { get; set; } = "idle";
    /// <summary>最大处理器状态 %</summary>
    public int Cap { get; set; } = 100;
    /// <summary>Turbo 策略 0-4，null = 不动</summary>
    public int? Boost { get; set; }
    /// <summary>风扇强制 %（null = 不干预）</summary>
    public double? Fan { get; set; }
}

public static class Program
{
    private static HardwareService? _hw;
    private static PowerManager? _pm;
    private static StressTest? _stress;
    private static volatile bool _abort;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cmd = args.Length == 0 ? "help" : args[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "probe": return CmdProbe();
                case "fan": return CmdFan(args);
                case "lab": return CmdLab(args);
                case "report": return CmdReport();
                case "model": return CmdModel(args);
                case "procs": return CmdProcs(args);
                case "caps": return CmdCaps();
                case "power": return CmdPower(args);
                case "perf": return CmdPerf();
                case "sensors": return CmdSensors();
                case "tempcheck": return CmdTempCheck(args);
                case "ec": return CmdEc();
                case "fit": return CmdFit(args);
                case "replay": return CmdReplay(args);
                case "selftest": return CmdSelfTest();
                default:
                    Console.WriteLine("tplab probe | report | caps | power | perf | sensors | tempcheck [--sec N] | ec | fit --csv <samples.csv> | fan --status|--set N|--restore | lab --plan <json> [--out dir] | procs [--write] | selftest");
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex);
            return 1;
        }
    }

    private static string Arg(string[] a, string name, string fallback)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        return fallback;
    }

    /// <summary>
    /// 从采样 CSV 拟合热阻：T = a + R·P（只用满载准稳态样本）。
    ///
    /// 用途有两个：① 事后核对程序里显示的 R 是不是被轻载样本带偏；
    /// ② 换硅脂/相变片之后重新确定"这台机器真实的 R 与合理 PL1"。
    /// </summary>
    private static int CmdFit(string[] args)
    {
        string csv = Arg(args, "--csv", "");
        if (string.IsNullOrWhiteSpace(csv) || !File.Exists(csv))
        {
            Console.WriteLine("用法：tplab fit --csv <samples-*.csv> [--ambient 24] [--ceiling 85]");
            Console.WriteLine("  缺省取日志目录里最新的 samples-*.csv");
            var dir = new DirectoryInfo(Log.LogDirectory);
            var latest = dir.Exists
                ? dir.GetFiles("samples-*.csv").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latest == null) return 1;
            csv = latest.FullName;
            Console.WriteLine("  使用：" + csv);
        }

        double amb = double.TryParse(Arg(args, "--ambient", "24"), out double a0) ? a0 : 24;
        double ceil = double.TryParse(Arg(args, "--ceiling", "85"), out double c0) ? c0 : 85;

        var rows = LoadSamples(csv);
        if (rows.Count == 0) { Console.WriteLine("没有可用样本"); return 1; }

        // 只保留"有负载"的样本：负载 ≥35% 且功率 ≥12W（与在线估计同一套门）
        var loaded = rows.Where(r => (double.IsNaN(r.Load) || r.Load >= 35) && r.P >= 12).ToList();
        Console.WriteLine($"样本 {rows.Count} 行，其中满载可用 {loaded.Count} 行。");

        var fit = ThermalResistance.Fit(loaded.Select(r => (r.P, r.T)));
        Console.WriteLine();
        Console.WriteLine($"拟合：T = {fit.a:F1}℃ + {fit.r:F3} K/W × P    R²={fit.r2:F3}（n={fit.n}）");
        Console.WriteLine($"      功率 {fit.pMin:F1}~{fit.pMax:F1}W，温度 {fit.tMin:F1}~{fit.tMax:F1}℃");
        Console.WriteLine($"      截距 {fit.a:F1}℃ ＝ 室温 {amb:F0}℃ + 固定偏置 {fit.a - amb:F1}℃（die/封装/uncore）");
        Console.WriteLine();
        Console.WriteLine($"换算：ΔP=10W → 稳态升温 {fit.r * 10:F1}℃；到上限 {ceil:F0}℃ 的可持续功率 "
                          + $"P = ({ceil:F0} − {amb:F0}) / {fit.r:F3} = {(ceil - amb) / fit.r:F1}W");

        // 分阶段均值，用来解释"为什么不同阶段功率差不多、温度差几度"
        Console.WriteLine();
        Console.WriteLine($"  {"阶段",-16}{"n",4}{"T均",7}{"P均",7}{"负载%",7}{"风扇%",7}");
        foreach (var g in rows.GroupBy(r => r.Stage))
        {
            var list = g.ToList();
            Console.WriteLine($"  {Trim(g.Key, 16),-16}{list.Count,4}"
                + $"{list.Average(x => x.T),7:F1}{list.Average(x => x.P),7:F1}"
                + $"{(double.IsNaN(list.Average(x => x.Load)) ? 0 : list.Average(x => x.Load)),7:F1}"
                + $"{(double.IsNaN(list.Average(x => x.Fan)) ? 0 : list.Average(x => x.Fan)),7:F1}");
        }

        // 判定：R 越小越"健康"
        Console.WriteLine();
        string verdict = fit.r switch
        {
            < 0.35 => "接触极好（水冷/大散热器量级）",
            < 0.9 => "导热良好（相变片/好硅脂 + 正常压装）",
            < 1.5 => "正常笔记本水平（偏薄机型）",
            < 1.9 => "偏高：散热器压装或硅脂状态一般",
            _ => "异常：接近断链（硅脂干涸 / 压装不实 / 风扇失效）"
        };
        Console.WriteLine($"结论：R={fit.r:F2} K/W，{verdict}");
        if (!double.IsNaN(fit.a) && fit.a - amb > 8)
            Console.WriteLine($"注意：截距比室温高 {fit.a - amb:F1}℃，说明低温端有固定偏置（传感器/uncore），"
                              + "所以不能直接用 (T−室温)/P 当热阻。");
        return 0;
    }

    /// <summary>
    /// 用历史采样回放控制器（离线验证控制律与热阻估计）。
    ///
    /// 意义：换散热之后最容易出的问题是"模型还以为机器很热"——旧热阻把预算压得很低，
    /// 或者反过来一路加功率。回放能直接看出"同一份真实温度数据，新律会怎么决策"，
    /// 不用真的再压一遍机器。
    /// </summary>
    private static int CmdReplay(string[] args)
    {
        string csv = Arg(args, "--csv", "");
        if (string.IsNullOrWhiteSpace(csv) || !File.Exists(csv))
        {
            var dir = new DirectoryInfo(Log.LogDirectory);
            var latest = dir.Exists
                ? dir.GetFiles("samples-*.csv").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latest == null) { Console.WriteLine("没有 samples-*.csv"); return 1; }
            csv = latest.FullName;
        }
        double amb = double.TryParse(Arg(args, "--ambient", "24"), out double a0) ? a0 : 24;
        double ceil = double.TryParse(Arg(args, "--ceiling", "85"), out double c0) ? c0 : 85;
        double turbo = double.TryParse(Arg(args, "--turbo", "3400"), out double t0) ? t0 : 3400;

        var rows = LoadSamples(csv);
        if (rows.Count == 0) { Console.WriteLine("没有样本"); return 1; }

        var cc = new AutoCpuController();
        Console.WriteLine($"回放 {Path.GetFileName(csv)}：{rows.Count} 点，上限 {ceil:F0}℃，室温 {amb:F0}℃，全核睿频 {turbo:F0}MHz");
        Console.WriteLine($"  {"t(s)",6}{"T℃",7}{"P W",7}{"负载%",7}{"PL1",7}{"R",8}  判定 / 决策");

        var hist = new List<(double sec, double t)>();
        int lastPrint = -99;
        AutoCpuDecision? d = null;
        foreach (var r in rows)
        {
            // 与引擎一致：跨 ~30 秒求 dT/dt（CSV 温度是 1℃ 量化，逐点差分全是噪声）
            hist.Add((r.Sec, r.T));
            while (hist.Count > 2 && r.Sec - hist[0].sec > 30) hist.RemoveAt(0);
            double dTdt = double.NaN;
            if (hist.Count >= 2 && r.Sec - hist[0].sec >= 8)
                dTdt = (r.T - hist[0].t) / (r.Sec - hist[0].sec);

            // 用 CSV 里的瞬时倍频近似"是否顶到全核上限"（回放没有 MSR 限制位）
            bool capped = !double.IsNaN(r.Mhz) && r.Mhz >= turbo - 15 && r.Load >= 30;
            var perf = capped
                ? new PerfSample { Valid = true, Limit = BindingLimit.FrequencyCap, LimitText = "频率上限" }
                : (PerfSample?)null;

            d = cc.Update(r.T, r.P, ceil, amb, "balanced", 10, 80, perf, r.Load, dTdt);

            if (r.Sec - lastPrint >= 60)
            {
                lastPrint = (int)r.Sec;
                Console.WriteLine($"  {r.Sec,6:F0}{r.T,7:F0}{r.P,7:F1}{(double.IsNaN(r.Load) ? 0 : r.Load),7:F0}"
                    + $"{d.Pl1W,7:F0}{(double.IsNaN(d.REff) ? 0 : d.REff),8:F2}  {d.Limit switch
                    {
                        BindingLimit.Thermal => "温度墙", BindingLimit.FrequencyCap => "频率上限",
                        BindingLimit.Power => "功率墙", BindingLimit.Idle => "轻载", _ => "未受限"
                    }} / {d.Reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"结束状态：PL1 {d!.Pl1W:F0}W · 预测 {d.PredictedC:F0}℃ · R {d.RQuality}");
        Console.WriteLine($"热阻估计器：{cc.Resistance.Quality}；拒绝样本 {cc.Resistance.Rejected} 个"
                          + (cc.Resistance.LastReject is { Length: > 0 } rj ? $"（最近原因：{rj}）" : ""));
        return 0;
    }

    /// <summary>采样 CSV 的一行（字段名与 SampleLogger 对齐）。</summary>
    private sealed class SampleRow
    {
        public string Stage = "";
        public double T, P, Load, Fan, Mhz, Sec;
    }

    private static List<SampleRow> LoadSamples(string csv)
    {
        var rows = new List<SampleRow>();
        try
        {
            using var sr = new StreamReader(csv, Encoding.UTF8);
            string? header = sr.ReadLine();
            if (header == null) return rows;
            var cols = SplitCsv(header.TrimStart('\uFEFF'));
            int iT = cols.FindIndex(c => c == "CpuTempC"), iP = cols.FindIndex(c => c == "CpuPowerW");
            int iL = cols.FindIndex(c => c == "CpuLoadPct"), iS = cols.FindIndex(c => c == "Stage");
            int iF = cols.FindIndex(c => c == "FanOutPct"), iM = cols.FindIndex(c => c == "CpuMHz");
            int iE = cols.FindIndex(c => c == "ElapsedSec");
            if (iT < 0 || iP < 0) return rows;

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var v = SplitCsv(line);
                if (v.Count <= Math.Max(iT, iP)) continue;
                if (!double.TryParse(v[iT], out double t) || !double.TryParse(v[iP], out double p)) continue;
                rows.Add(new SampleRow
                {
                    Stage = iS >= 0 ? v[iS] : "",
                    T = t, P = p,
                    Load = iL >= 0 && double.TryParse(v[iL], out double l) ? l : double.NaN,
                    Fan = iF >= 0 && double.TryParse(v[iF], out double f) ? f : double.NaN,
                    Mhz = iM >= 0 && double.TryParse(v[iM], out double m) ? m : double.NaN,
                    Sec = iE >= 0 && double.TryParse(v[iE], out double e) ? e : 0
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("读取失败：" + ex.Message); }
        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// 检查 EC 访问健康：近 30 分钟 Windows 是否报过 ACPI 13（EC 未响应）。
    /// 笔记本风扇只能直写 EC，写太勤会连累电池读数 —— 这就是"检测不到电池"的来源。
    /// </summary>
    private static int CmdEc()
    {
        int n = EcHealth.RecentTimeouts(out string raw, 30);
        if (n < 0)
        {
            Console.WriteLine("读不到系统事件日志：" + raw + "（需要管理员）");
            return 0;
        }
        Console.WriteLine(n == 0
            ? "近 30 分钟：没有 ACPI EC 超时（事件 13）。"
            : $"近 30 分钟：{n} 次 ACPI 13「嵌入式控制器(EC)未响应」。");
        Console.WriteLine(EcHealth.Check(30).Advice);
        return 0;
    }

    /// <summary>
    /// 打印本次采样**实际采用**的数值，以及所有候选传感器的原始值。
    ///
    /// 这是为了"数值对不上"这类问题：用户拿 OEM 工具（OMEN Hub / 任务管理器）一比就知道差多少，
    /// 但不知道程序取了哪个传感器。这里把"用了谁、还有哪些候选、各是多少"一次列出来，
    /// 不需要再猜。本机实测就靠它发现「GPU 温度取到了 Hot Spot」。
    /// </summary>
    private static int CmdSensors()
    {
        var hw = Hw();
        if (!hw.IsOpen)
        {
            Console.WriteLine("硬件监控打不开：" + hw.OpenError);
            Console.WriteLine("（需要管理员 + 关闭内存完整性 HVCI；本机 CPU 温度/功率来自 MSR 通道）");
            return 1;
        }

        // 负载是差分量：第一拍只建基线，必须等过一个 ≥1 秒窗口再读，否则永远显示 0%
        _ = hw.Read();
        Thread.Sleep(1200);
        var s = hw.Read();
        Console.WriteLine("== 程序实际采用的值 ==");
        Console.WriteLine($"  CPU 温度      {s.CpuTempShown,6:F1} ℃   （CPU Package，5 点中值抗毛刺；本拍原始 {s.CpuTemp:F1} ℃）");
        Console.WriteLine($"  最热核心      {s.CpuCoreMaxTemp,6:F1} ℃   （Core Max，只作参考）");
        Console.WriteLine($"  CPU 负载      {s.CpuLoad,6:F0} %    （GetSystemTimes 差分，≥1 秒窗口）");
        Console.WriteLine($"  CPU 频率(瞬时) {s.CpuFrequencyMhz,6:F0} MHz");
        Console.WriteLine($"  CPU 功率      {s.CpuPower,6:F1} W");
        Console.WriteLine($"  GPU 温度      {s.GpuTempShown,6:F1} ℃   （GPU Core，同样抗毛刺；本拍原始 {s.GpuTemp:F1} ℃）");
        Console.WriteLine($"  GPU 热点      {s.GpuHotSpotTemp,6:F1} ℃   （Hot Spot，只作参考）");
        Console.WriteLine($"  GPU 负载      {s.GpuLoad,6:F0} %");
        Console.WriteLine($"  内存          {s.RamPct,6:F0} %    （{s.RamUsedMb / 1024:F1} / {s.RamTotalMb / 1024:F1} GB）");
        Console.WriteLine();

        Console.WriteLine("== 全部温度传感器（看候选与差值）==");
        foreach (var line in hw.SensorInventory.Where(l => l.Contains(":: Temperature ::")))
            Console.WriteLine("  " + line);
        Console.WriteLine();
        Console.WriteLine("口径说明：大数字取 Package / GPU Core，与任务管理器、OMEN Hub 一致；");
        Console.WriteLine("          Core Max 是「最热单核」（高 1~3℃），Hot Spot 是「热点」（高 8~12℃），都不该当主指标。");
        return 0;
    }

    /// <summary>
    /// 现场验证「温度突然跳一下」到底是真温升还是读数毛刺。
    ///
    /// 判据只有一条，且是物理的：温度涨 X℃ 必须有相应的功率。按本机标定 R≈1.2 K/W，
    /// ΔT/ΔP 应该接近这个数。若某拍温度跳了 30℃ 而功率只动 1W，那不是硅片在发热，
    /// 是 DTS 读数偶发取到了别的值（真机 704 个样本里 43 个这样，占 6%）。
    ///
    /// 用法：tplab tempcheck [--sec 20]   （需要管理员，温度走 MSR）
    /// </summary>
    private static int CmdTempCheck(string[] args)
    {
        int sec = 20;
        for (int i = 0; i < args.Length - 1; i++)
            if ((args[i] == "--sec" || args[i] == "-s") && int.TryParse(args[i + 1], out int v))
                sec = Math.Clamp(v, 5, 180);

        var hw = Hw();
        if (!hw.IsOpen)
        {
            Console.WriteLine("硬件监控打不开：" + hw.OpenError + "（需要管理员 + 关闭内存完整性 HVCI）");
            return 1;
        }

        const double R = 1.2;   // K/W，本机换相变片后拟合值 1.21（见 docs/MODEL.md）
        Console.WriteLine($"== 连续 {sec} 秒、每秒一读：原始值 vs 抗毛刺值（R≈{R:F1} K/W）==");
        Console.WriteLine("   温差需要的额外功率 = ΔT / R；功率没跟上 → 读数毛刺，不是真发热");
        Console.WriteLine();
        Console.WriteLine("    t(s)   原始    平滑    ΔT    功率W   ΔP W   需要W   判定");
        Console.WriteLine("   ----  ------  ------  -----  ------  ------  ------  ----");

        double prevRaw = double.NaN, prevP = double.NaN;
        int glitches = 0, n = 0;
        double minRaw = 999, maxRaw = -999, maxSmooth = -999, maxJump = 0;

        for (int k = 1; k <= sec; k++)
        {
            var s = hw.Read();
            double raw = s.CpuTemp, smooth = s.CpuTempShown, p = s.CpuPower;

            double dT = double.IsNaN(prevRaw) ? 0 : raw - prevRaw;
            double dP = double.IsNaN(prevP) ? 0 : p - prevP;
            double need = Math.Abs(dT) / R;

            string verdict = "正常";
            if (Math.Abs(dT) >= 10)
            {
                // 功率跟不上：需要的额外功率远大于实际功率变化 → 读数问题
                if (Math.Abs(dP) < need * 0.5) { verdict = "毛刺（功率不匹配）"; glitches++; }
                else verdict = "真实阶跃（功率同步）";
            }
            else if (Math.Abs(raw - smooth) >= 6) { verdict = "本拍已被平滑"; }

            if (k <= 12 || Math.Abs(dT) >= 10 || Math.Abs(raw - smooth) >= 6)
                Console.WriteLine($"   {k,4}  {raw,6:F1}  {smooth,6:F1}  {dT,+5:F1}  {p,6:F1}  {dP,+6:F1}  {need,6:F1}  {verdict}");

            if (raw > 0)
            {
                n++;
                minRaw = Math.Min(minRaw, raw); maxRaw = Math.Max(maxRaw, raw);
                maxSmooth = Math.Max(maxSmooth, smooth);
                maxJump = Math.Max(maxJump, Math.Abs(dT));
            }
            prevRaw = raw; prevP = p;
            if (k < sec) Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine($"   样本 {n} 个　原始区间 {minRaw:F0}~{maxRaw:F0}℃　平滑后最高 {maxSmooth:F0}℃　单拍最大跳变 {maxJump:F0}℃");
        Console.WriteLine(glitches == 0
            ? "   本次没抓到功率不匹配的跳变 —— 说明刚才那个尖刺不是每拍都有，属于偶发读数问题。"
            : $"   抓到 {glitches} 个「功率跟不上」的跳变 —— 这些就是界面上看到的假尖刺，已被中值滤波吃掉。");
        Console.WriteLine("   另：采样越慢只会漏掉尖刺，不会制造尖刺。若要看清突发，间隔应该更短而不是更长。");
        return 0;
    }

    /// <summary>
    /// 现场测一次「有效频率」：连采两次（间隔 3 秒），打印有效频率 / C0 内平均 / 瞬时倍频 / 包功率。
    ///
    /// 为什么会需要这个命令：有效频率是整份报告里最容易写错的一个数（32 位回绕、跨核配对、
    /// 拿 MPERF 当分母），而这些错误只在真机上表现为"数字离谱"。所以留一个能当场跑、
    /// 当场对（是否 ≤ 芯片最高频率）的出口，比在生产面板上争要可靠。
    /// </summary>
    private static int CmdPerf()
    {
        var cpu = new CpuControlService();
        if (!cpu.EnsureAvailable())
        {
            Console.WriteLine("读不到 MSR：" + cpu.LastError);
            Console.WriteLine();
            Console.WriteLine("有效频率依赖 APERF/MPERF（0xE8/0xE7），前提是：管理员 + 关闭内存完整性(HVCI) + Ring0 驱动加载成功。");
            Console.WriteLine("请用管理员权限运行本命令；或者直接启动主程序（它本身要求管理员），看仪表盘的「CPU 有效频率」。");
            return 0;
        }

        PlatformPower.Apply(null, cpu.Msr, force: true);
        var pm = new PerfMetrics(cpu.Msr);
        Console.WriteLine($"基准频率 {pm.BaseMhz:F0}MHz（MPERF 推进速率，注册表 ~MHz；只用于交叉校验，不参与计算）");

        double pl1 = PlatformPower.CpuDesignW;
        _ = pm.Sample(pl1, 0, 88, 15);      // 第一轮只建基线
        Console.WriteLine("第一轮只建基线，等 3 秒再采……");
        Thread.Sleep(3000);

        var s = pm.Sample(pl1, 0, 88, 15);
        Console.WriteLine();
        if (s.EffectiveMhz > 0)
            Console.WriteLine($"  有效频率   {s.EffectiveMhz,6:F0} MHz   （ΔAPERF / 实际经过时间，含睿频/降频/休眠）");
        else
            Console.WriteLine($"  有效频率   ——        本轮不可用：{s.EffNote}");
        Console.WriteLine($"  C0 内平均  {(s.C0Mhz > 0 ? s.C0Mhz.ToString("F0") : "—"),6} MHz   （只在核心活跃时统计）");
        Console.WriteLine($"  瞬时倍频   {s.CurrentMhz,6:F0} MHz   （内核请求的 P-state，空闲时也会偏高）");
        Console.WriteLine($"  全核上限   {s.TurboLimitMhz,6:F0} MHz   单核上限 {s.TurboLimit1CoreMhz:F0} MHz");
        Console.WriteLine($"  包功率     {s.PackagePowerW,6:F1} W");
        Console.WriteLine($"  限制判定   {s.LimitText}");
        if (!string.IsNullOrEmpty(s.Evidence)) Console.WriteLine($"  依据       {s.Evidence}");

        if (s.EffectiveMhz > 0 && s.TurboLimit1CoreMhz > 0 && s.EffectiveMhz > s.TurboLimit1CoreMhz * 1.02)
            Console.WriteLine($"  [x] 有效频率超过单核睿频上限，说明计数器读取仍有问题，请把这段输出反馈给我。");
        return 0;
    }

    /// <summary>
    /// 打印平台功率上限（PL1/PL2/GPU 的可用区间从哪来）。
    /// 带 --set-cpu N / --set-gpu N 时模拟用户手填，用来验证覆盖逻辑与区间联动。
    /// </summary>
    private static int CmdPower(string[] args)
    {
        var cpu = new CpuControlService();
        MsrAccess? msr = cpu.Available || cpu.EnsureAvailable() ? cpu.Msr : null;

        double setCpu = double.TryParse(Arg(args, "--set-cpu", "0"), out double c0) ? c0 : 0;
        double setGpu = double.TryParse(Arg(args, "--set-gpu", "0"), out double g0) ? g0 : 0;
        bool probing = setCpu > 0 || setGpu > 0;

        // 纯探测（不带覆盖），让"自动值"始终可见
        var probe = PlatformPower.Probe(msr);
        Console.WriteLine($"CPU 型号：{CpuCapabilities.Current.Brand}");
        Console.WriteLine();
        Console.WriteLine("自动探测：");
        Console.WriteLine($"  CPU 设计功耗  {probe.cpu.Text,-16} {probe.cpu.Evidence}");
        Console.WriteLine($"  GPU 设计功耗  {(probe.gpu.HasValue ? probe.gpu.Text : "未知"),-16} {probe.gpu.Evidence}");

        var cfg = new ThermalConfig { CpuDesignW = setCpu, GpuDesignW = setGpu };
        PlatformPower.Apply(cfg, msr, force: true);
        Console.WriteLine();
        Console.WriteLine(probing ? "应用（含手填覆盖）后：" : "生效值：");
        Console.WriteLine($"  CPU 设计功耗  {PlatformPower.Cpu.Text}");
        Console.WriteLine($"  PL2 上限      {PlatformPower.Pl2CeilingW:F0}W");
        Console.WriteLine($"  GPU 设计功耗  {(PlatformPower.Gpu.HasValue ? PlatformPower.Gpu.Text : "未知")}");
        Console.WriteLine($"  → PL1 允许 5–{PlatformPower.CpuDesignW:F0}W，PL2 允许 5–{PlatformPower.Pl2CeilingW:F0}W");

        var v = ParamConstraints.Validate(
            new ThermalConfig { AutoCpuMode = true, CpuPl1W = (int)PlatformPower.CpuDesignW, CpuPl2W = (int)PlatformPower.Pl2CeilingW },
            1.2);
        Console.WriteLine($"  校验（PL1 取满 {PlatformPower.CpuDesignW:F0}W）：{(v.Count == 0 ? "通过" : string.Join("；", v.Select(x => x.Message)))}");
        if (setCpu > 0 && setCpu > PlatformPower.AbsoluteCpuMaxW)
            Console.WriteLine($"  注意：手填 {setCpu:F0}W 已被夹到 {PlatformPower.AbsoluteCpuMaxW:F0}W（绝对上限）");
        return 0;
    }

    /// <summary>
    /// 打印 CPU 平台能力矩阵 —— 决定高级参数里哪些旋钮在本机是可编辑的。
    /// 有 MSR 权限时顺带做真实探测，否则只给静态部分（厂商 / 睿频 / HWP）。
    /// </summary>
    private static int CmdCaps()
    {
        var cpu = new CpuControlService();
        var caps = CpuCapabilities.Refresh(cpu.Available || cpu.EnsureAvailable() ? cpu.Msr : null);

        Console.WriteLine(caps.CpuLine);
        Console.WriteLine($"CPUID={(caps.CpuidOk ? "可用" : "不可用")} · Turbo={(caps.HasTurboBoost ? "是" : "否")} · "
                          + $"HWP={(caps.HasHwp ? "是" : "否")} · MSR={(caps.MsrReady ? "可用" : caps.MsrError)}");
        Console.WriteLine();
        Console.WriteLine($"  {"能力",-22}{"结论",-14}说明");
        foreach (var it in caps.Items)
            Console.WriteLine($"  {it.Label,-22}{it.Badge,-14}{it.Detail}");
        Console.WriteLine();
        Console.WriteLine(caps.AllMsrUnsupported
            ? "结论：本机无法使用 MSR 精细调控，自动退回 Windows 电源计划限频。"
            : "结论：高级参数中标记 ✗ 的项在本机不可编辑，其余可用。");
        return 0;
    }

    /// <summary>
    /// 打印一次 top10 进程负载（功耗 → CPU → 内存 → IO）。
    /// --write 会先写 64MB 临时文件，用来验证 IO 计数器确实在工作（排查"IO 列恒为 0"）。
    /// </summary>
    private static int CmdProcs(string[] args)
    {
        bool write = args.Any(a => a.Equals("--write", StringComparison.OrdinalIgnoreCase));

        double pkg = 0;
        var cpu = new CpuControlService();
        if (cpu.EnsureAvailable())
        {
            var perf = new PerfMetrics(cpu.Msr);
            perf.Sample(0, 0, 90, 0);
            Thread.Sleep(1200);
            var ps = perf.Sample(0, 0, 90, 0);
            if (ps.Valid) pkg = ps.PackagePowerW;
        }
        if (pkg <= 0) Console.WriteLine("（包功率不可用：无 MSR 权限 → 功耗列全 0，排序退化为按 CPU）");

        var mon = new ProcessMonitor();
        mon.Sample(pkg, 10);
        if (write)
        {
            string path = Path.Combine(Path.GetTempPath(), "tplab-io-test.bin");
            var buf = new byte[64 * 1024 * 1024];
            using (var fs = File.Create(path)) { fs.Write(buf, 0, buf.Length); fs.Flush(true); }
            Console.WriteLine($"已写 64MB 到 {path}（用于验证 IO 列）");
        }
        Thread.Sleep(1200);
        var s = mon.Sample(pkg, 10);

        Console.WriteLine($"包功率 {(pkg > 0 ? pkg.ToString("F1") + "W" : "不可用")} · {s.ProcessCount} 个进程"
                          + $" · 合计 CPU {s.TotalCpuPct:F1}% / {s.TotalPowerW:F1}W / {s.TotalMemMb:F0}MB / {s.TotalDiskMBps:F2}MBps");
        Console.WriteLine($"  {"#",-3}{"程序",-22}{"实例",4}{"功耗W",8}{"CPU%",8}{"内存MB",10}{"IO MB/s",10}");
        for (int i = 0; i < s.Rows.Count; i++)
        {
            var r = s.Rows[i];
            Console.WriteLine($"  {i + 1,-3}{r.Name,-22}{r.Instances,4}{r.PowerW,8:F2}{r.CpuPct,8:F1}{r.MemMb,10:F0}{r.DiskMBps,10:F2}");
        }
        return 0;
    }

    /// <summary>离线自检：MSR 编码/解码 + 自动控制器收敛；有权限时附带真实 MSR 探测。</summary>
    private static int CmdSelfTest()
    {
        int bad = 0;

        Console.WriteLine("== CpuControlService 编码自检 ==");
        var e1 = CpuControlService.SelfTest();
        if (e1.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e1) Console.WriteLine("  [x] " + x); bad += e1.Count; }

        Console.WriteLine("== AutoCpuController 收敛自检 ==");
        var e2 = AutoCpuController.SelfTest();
        if (e2.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e2) Console.WriteLine("  [x] " + x); bad += e2.Count; }

        Console.WriteLine("== 参数机理约束自检 ==");
        var e3 = ParamConstraints.SelfTest();
        if (e3.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e3) Console.WriteLine("  [x] " + x); bad += e3.Count; }

        Console.WriteLine("== 参数中心（草稿/已应用/差异）自检 ==");
        var e4 = ParameterCenter.SelfTest();
        if (e4.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e4) Console.WriteLine("  [x] " + x); bad += e4.Count; }

        Console.WriteLine("== 取向预设自检 ==");
        var e5 = ProfilePreset.SelfTest();
        if (e5.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e5) Console.WriteLine("  [x] " + x); bad += e5.Count; }

        Console.WriteLine("== 取向绑定（静音 / 静音 · 自定义）自检 ==");
        var e6 = ProfileBinding.SelfTest();
        if (e6.Count == 0)
        {
            var demo = new ThermalConfig();
            ProfilePreset.Apply(demo, "静音");
            Console.WriteLine($"  全部通过　纯预设=\"{ProfileBinding.DisplayName(demo)}\"");
            demo.AutoTargetC = 84; demo.CpuPl1W = 30; demo.CpuPl2W = 34;
            Console.WriteLine($"  手改两项后=\"{ProfileBinding.DisplayName(demo)}\"（{ProfileBinding.Short(demo)}）");
            foreach (var line in ProfileBinding.Describe(demo).Split('\n'))
                Console.WriteLine("    " + line.TrimEnd());
        }
        else { foreach (var x in e6) Console.WriteLine("  [x] " + x); bad += e6.Count; }

        Console.WriteLine("== 配置档（内置基线 + 增量 / 持久化）自检 ==");
        var e7 = ProfileInstance.SelfTest();
        if (e7.Count == 0)
        {
            var baseCfg = new ThermalConfig();
            ProfilePreset.Apply(baseCfg, "静音");
            baseCfg.CpuPl1W = 30; baseCfg.CpuPl2W = 34; baseCfg.AutoTargetC = 84;
            var inst = ProfileInstance.Capture(baseCfg);
            Console.WriteLine($"  全部通过　配置档=\"{inst.DisplayName}\"：{inst.Summary()}");
            foreach (var r in ProfileInstance.Rows(baseCfg, 1.82, 38).Where(r => r.IsOverride || r.IsLocked))
                Console.WriteLine($"    {r.Source,-8} {r.Label,-20} {r.BaseText,8} → {r.ValueText,-8}  {r.BoundText}");
        }
        else { foreach (var x in e7) Console.WriteLine("  [x] " + x); bad += e7.Count; }

        Console.WriteLine("== 进程负载排序（功耗→CPU→内存→IO）自检 ==");
        var e8 = ProcessMonitor.SelfTest();
        if (e8.Count == 0)
        {
            var mon = new ProcessMonitor();
            mon.Sample(30, 10);
            System.Threading.Thread.Sleep(800);
            var ps = mon.Sample(30, 10);
            Console.WriteLine($"  全部通过　{ps.ProcessCount} 个进程，合计 CPU {ps.TotalCpuPct:F1}% · {ps.TotalPowerW:F1}W");
            Console.WriteLine($"    {"程序",-22}{"实例",4}{"功耗W",8}{"CPU%",8}{"内存MB",10}{"IO MB/s",10}");
            foreach (var r in ps.Rows)
                Console.WriteLine($"    {r.Name,-22}{r.Instances,4}{r.PowerW,8:F2}{r.CpuPct,8:F1}{r.MemMb,10:F0}{r.DiskMBps,10:F2}");
        }
        else { foreach (var x in e8) Console.WriteLine("  [x] " + x); bad += e8.Count; }

        Console.WriteLine("== CPU 平台能力（决定高级参数是否可编辑）自检 ==");
        var e9 = CpuCapabilities.SelfTest();
        if (e9.Count == 0)
        {
            var rc = CpuCapabilities.Refresh(null);
            Console.WriteLine($"  全部通过　{rc.CpuLine}");
            Console.WriteLine($"    {rc.BadgeLine}");
            var demo = new ThermalConfig { ControlChannel = ControlChannels.Msr, EnableCpuTuning = true, CpuPl1W = 30, CpuEpp = 128 };
            var amd = CpuCapabilities.Synthetic("AuthenticAMD", 0x19, 0x21, true, false, CapState.Unsupported);
            amd.Sanitize(demo);
            Console.WriteLine($"    AMD 上清洗后：通道={ControlChannels.Label(demo.ControlChannel)} "
                              + $"PL1={demo.CpuPl1W} EPP={demo.CpuEpp} 手工调控={demo.EnableCpuTuning}");
        }
        else { foreach (var x in e9) Console.WriteLine("  [x] " + x); bad += e9.Count; }

        Console.WriteLine("== 热阻估计（ΔT/ΔP 斜率，只吃准稳态满载样本）自检 ==");
        var e10 = ThermalResistance.SelfTest();
        if (e10.Count == 0) Console.WriteLine("  全部通过");
        else { foreach (var x in e10) Console.WriteLine("  [x] " + x); bad += e10.Count; }

        Console.WriteLine("== 平台功率探测（PL1/PL2/GPU 可用区间的来源）自检 ==");
        var e11 = PlatformPower.SelfTest();
        if (e11.Count == 0)
        {
            var (pc, pg) = PlatformPower.Probe(null);
            Console.WriteLine($"  全部通过　CPU 型号表={pc.Badge}，GPU 型号表={pg.Badge}");
            var auto = new ThermalConfig();
            PlatformPower.Apply(auto, null, force: true);
            Console.WriteLine($"    自动 → CPU {PlatformPower.CpuDesignW:F0}W / PL2 ≤ {PlatformPower.Pl2CeilingW:F0}W / GPU {PlatformPower.Gpu.Text}");
            PlatformPower.Apply(new ThermalConfig { CpuDesignW = 28, GpuDesignW = 50 }, null, force: true);
            Console.WriteLine($"    手填 28/50 → CPU {PlatformPower.CpuDesignW:F0}W / PL2 ≤ {PlatformPower.Pl2CeilingW:F0}W / GPU {PlatformPower.GpuDesignW:F0}W");
            PlatformPower.Apply(new ThermalConfig(), null, force: true);
        }
        else { foreach (var x in e11) Console.WriteLine("  [x] " + x); bad += e11.Count; }

        Console.WriteLine("== 有效频率（ΔAPERF/ΔMPERF）算法自检 ==");
        var e12 = PerfMetrics.SelfTest();
        if (e12.Count == 0)
        {
            Console.WriteLine("  全部通过（3 秒窗口计数器回绕 / 深睡噪声 / 2 倍比率 / 过短窗口 / 单核 turbo）");
            var pm = new PerfMetrics(new CpuControlService().Msr);
            if (pm.BaseMhz > 200) Console.WriteLine($"    基准频率（MPERF 推进速率）={pm.BaseMhz:F0}MHz");
        }
        else { foreach (var x in e12) Console.WriteLine("  [x] " + x); bad += e12.Count; }

        Console.WriteLine("== 内存占用（物理内存，与任务管理器同口径）自检 ==");
        var e14 = SystemMemory.SelfTest();
        if (e14.Count == 0)
        {
            var (mt, mu, mp) = SystemMemory.Read();
            Console.WriteLine($"  全部通过　{mu / 1024:F1} / {mt / 1024:F1} GB（{mp:F0}%）");
        }
        else { foreach (var x in e14) Console.WriteLine("  [x] " + x); bad += e14.Count; }

        Console.WriteLine("== NBFC 状态口径（Target 是百分比 / Current 是步进）自检 ==");
        var e13 = NbfcBridge.SelfTest();
        if (e13.Count == 0)
        {
            var live = new NbfcBridge();
            if (live.Detect())
            {
                var ls = live.TryReadStatus();
                Console.WriteLine($"  全部通过　现场：Auto control={ls.AutoControl} Target={ls.TargetFanPct?.ToString("F0") ?? "—"}%"
                                  + $" Current={ls.CurrentRaw?.ToString("F1") ?? "—"}/{ls.SpeedSteps?.ToString() ?? "—"} 步");
            }
            else Console.WriteLine("  全部通过（本机未装 NBFC，只跑了纯解析用例）");
        }
        else { foreach (var x in e13) Console.WriteLine("  [x] " + x); bad += e13.Count; }

        Console.WriteLine("== 温度抗毛刺滤波（单点离群值）自检 ==");
        var e15 = TempFilter.SelfTest();
        if (e15.Count == 0) Console.WriteLine("  全部通过（单点/交替毛刺剔除，真实升降阶跃保留）");
        else { foreach (var x in e15) Console.WriteLine("  [x] " + x); bad += e15.Count; }

        Console.WriteLine("== MSR 探测 ==");
        var cpu = new CpuControlService();
        if (!cpu.EnsureAvailable())
        {
            Console.WriteLine("  不可用：" + cpu.LastError + "（非管理员 / HVCI / 驱动未加载都属正常）");
        }
        else
        {
            var st = cpu.ReadCurrent();
            Console.WriteLine(st == null
                ? "  可访问，但读取 0x610 失败"
                : $"  PL1={st.Pl1W:F1}W PL2={st.Pl2W:F1}W τ={st.TauS:F0}s EPP={st.Epp} 睿频={st.TurboRatio1Core}");

            // 原始值对排错很关键：τ 是 log2 编码，字段异常时只有裸值能看出问题
            if (cpu.Msr.TryRead(CpuControlService.MSR_PKG_POWER_LIMIT, out ulong raw610))
            {
                ulong tauField = (raw610 >> 17) & 0x7F;
                ulong tau2Field = (raw610 >> 49) & 0x7F;
                Console.WriteLine($"  0x610 = 0x{raw610:X16}  PL1time字段={tauField}(2^{tauField}={Math.Pow(2, tauField):0})"
                                  + $"  PL2time字段={tau2Field}(2^{tau2Field}={Math.Pow(2, tau2Field):0})"
                                  + $"  PL1en={(raw610 >> 15) & 1} PL1clamp={(raw610 >> 16) & 1}"
                                  + $" PL2en={(raw610 >> 47) & 1} PL2clamp={(raw610 >> 48) & 1}");
            }

            // 交付性能指标（有效频率 / 包功率 / 谁在限制）——两次采样间隔 1.2s 才能出 ΔAPERF/ΔMPERF
            Console.WriteLine("== CPU 交付指标（PerfMetrics）==");
            var perf = new PerfMetrics(cpu.Msr);
            Console.WriteLine($"  基准频率 {perf.BaseMhz:F0}MHz，逻辑核 {Environment.ProcessorCount}");
            perf.Sample(st?.Pl1W ?? 0, 0, 90, 0);
            System.Threading.Thread.Sleep(1200);
            var p = perf.Sample(st?.Pl1W ?? 0, 0, 90, 0);
            Console.WriteLine(p.Valid
                ? $"  {p.Detail}  →  {p.LimitText}"
                : "  暂无读数（可能需要连续采样数秒）");
        }

        Console.WriteLine(bad == 0 ? "自检结果：通过" : $"自检结果：{bad} 项失败");
        return bad == 0 ? 0 : 2;
    }

    private static HardwareService Hw()
    {
        if (_hw != null) return _hw;
        _hw = new HardwareService();
        if (!_hw.Open()) Console.Error.WriteLine("硬件监控打开失败：" + _hw.OpenError);
        return _hw;
    }

    private static PowerManager Pm()
    {
        if (_pm != null) return _pm;
        _pm = new PowerManager();
        _pm.Initialize();
        return _pm;
    }

    // ---------------------------------------------------------------- probe
    private static int CmdProbe()
    {
        var hw = Hw();
        var pm = Pm();
        Console.WriteLine($"设备：{string.Join(" | ", hw.HardwareNames)}");
        Console.WriteLine($"传感器 {hw.SensorCount} 个；LHM 转速通道 {hw.FanChannels.Count(c => c.RpmSensor != null)}，" +
                          $"LHM 调速通道 {hw.FanChannels.Count(c => c.Controllable)}");
        Console.WriteLine($"NBFC：{(hw.Nbfc.Installed ? hw.Nbfc.ExePath + " / " + hw.Nbfc.ConfigName : "未安装")}");
        Console.WriteLine($"风扇可调={hw.FanControlSupported} 可读转速={hw.RpmSupported}");
        Console.WriteLine($"电源：scheme={pm.ActiveScheme} origProcMax={pm.OriginalProcMax}% boost={pm.OriginalBoostMode}");

        var s = hw.Read();
        Thread.Sleep(1100);
        s = hw.Read();
        Console.WriteLine($"采样：CPU {s.CpuTemp:F1}°C(coreMax {s.CpuCoreMaxTemp:F1}) / 负载 {s.CpuLoad:F0}% / " +
                          $"{s.CpuFrequencyMhz:F0}MHz / {s.CpuVoltage:F3}V / {s.CpuPower:F1}W；" +
                          $"GPU {s.GpuTemp:F1}°C / {s.GpuLoad:F0}%；风扇 {s.FanRpm} RPM");
        return 0;
    }

    // ------------------------------------------------------------------ fan
    private static int CmdFan(string[] args)
    {
        var hw = Hw();
        if (args.Contains("--status"))
        {
            foreach (var f in hw.Read().Fans)
                Console.WriteLine($"{f.Name} | {f.Source} | {f.Rpm}RPM | out={f.OutputPct?.ToString("F0") ?? "-"} | 可控={f.Controllable}");
            return 0;
        }

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--set" && double.TryParse(args[i + 1], out double pct))
            {
                hw.Nbfc.InvalidateCache();          // 手动命令不受"值未变/时间闸"影响
                int n = hw.SetFanPercent(pct, force: true);
                Console.WriteLine($"设置 {pct:F0}% → 生效通道 {n} 个");
                if (hw.Nbfc.Installed)
                {
                    var st = hw.Nbfc.TryReadStatus();
                    Console.WriteLine($"NBFC 回读：Auto control={st.AutoControl} · Target={st.TargetFanPct?.ToString("F0") ?? "—"}%"
                                      + $" · Current(EC 原始)={st.CurrentRaw?.ToString("F1") ?? "—"}/{st.SpeedSteps?.ToString() ?? "—"} 步");
                    if (!string.IsNullOrEmpty(hw.Nbfc.LastError)) Console.WriteLine("失败原因：" + hw.Nbfc.LastError);
                    else if (st.ManualActive) Console.WriteLine("已确认接管：后续写入会被 NBFC 当作手动目标执行。");
                }
                return n > 0 ? 0 : 2;
            }
        }

        if (args.Contains("--restore")) { hw.RestoreFanDefault(); Console.WriteLine("已还原风扇。"); return 0; }
        return 0;
    }

    private static int CmdReport()
    {
        var settings = SettingsStore.Load();
        var cfg = settings?.Config ?? new ThermalConfig();
        string text = SupportReport.Build(Hw(), Pm(), new UndervoltService(), cfg, null, settings?.Profile);
        File.WriteAllText(Paths.ReportFile, text, new UTF8Encoding(false));
        Console.WriteLine($"已写入 {Paths.ReportFile}");
        return 0;
    }

    // ------------------------------------------------------------------ lab
    private static int CmdLab(string[] args)
    {
        string planPath = Arg(args, "--plan", "");
        string tag = Arg(args, "--tag", "doe");
        string outDir = Arg(args, "--out", Path.Combine(Paths.DataDirectory, "lab"));

        LabPlan plan;
        if (planPath.Length > 0 && File.Exists(planPath))
        {
            plan = JsonSerializer.Deserialize<LabPlan>(File.ReadAllText(planPath),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LabPlan();
        }
        else
        {
            plan = DefaultPlan();
        }

        if (plan.Stages.Count == 0) { Console.Error.WriteLine("计划为空"); return 1; }

        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string csvPath = Path.Combine(outDir, $"lab-{tag}-{stamp}.csv");
        string manifestPath = Path.Combine(outDir, $"lab-{tag}-{stamp}.json");

        var hw = Hw();
        var pm = Pm();
        _stress = new StressTest();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _abort = true; };

        int totalSec = plan.Stages.Sum(s => s.Sec);
        Console.WriteLine($"实验 {plan.Name}/{tag}：{plan.Stages.Count} 阶段，共 {totalSec / 60.0:F1} 分钟 → {csvPath}");
        Console.WriteLine("请勿在此期间使用本机（否则数据被污染）。Ctrl+C 可中止并还原。");

        var manifest = new List<object>();
        var rows = new List<string>
        {
            "timestamp,elapsed_s,stage,stage_elapsed_s,load,cpu_temp_c,cpu_core_max_c,cpu_volt_v,cpu_load_pct," +
            "cpu_mhz,cpu_power_w,gpu_temp_c,gpu_load_pct,gpu_power_w,fan_rpm,fan_out_pct,proc_cap,boost"
        };

        var swAll = Stopwatch.StartNew();
        try
        {
            foreach (var st in plan.Stages)
            {
                if (_abort) break;

                // 1) 施加控制量
                if (pm.Available) pm.SetProcessorMax(st.Cap);
                if (st.Boost is int b && pm.Available) pm.SetBoostMode(b);
                if (st.Fan is double fanPct) hw.SetFanPercent(fanPct);
                else if (st.Load is "idle" or "cool") hw.RestoreFanDefault();

                _stress.Start(st.Load.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? StressKind.Cpu : StressKind.Idle);

                var stageStart = DateTime.Now;
                var swStage = Stopwatch.StartNew();
                Console.WriteLine($"→ [{st.Id}] {st.Label} | {st.Sec}s | cap={st.Cap}% load={st.Load} fan={st.Fan?.ToString() ?? "auto"}");

                int i = 0;
                while (swStage.Elapsed.TotalSeconds < st.Sec && !_abort)
                {
                    var s = hw.Read();
                    double el = swAll.Elapsed.TotalSeconds;
                    double sel = swStage.Elapsed.TotalSeconds;
                    double fanOut = st.Fan ?? -1;

                    rows.Add(string.Join(',', new[]
                    {
                        s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        el.ToString("F1", CultureInfo.InvariantCulture),
                        st.Id,
                        sel.ToString("F1", CultureInfo.InvariantCulture),
                        st.Load,
                        F(s.CpuTemp), F(s.CpuCoreMaxTemp), s.CpuVoltage.ToString("F3", CultureInfo.InvariantCulture),
                        F(s.CpuLoad), s.CpuFrequencyMhz.ToString("F0", CultureInfo.InvariantCulture), F(s.CpuPower),
                        F(s.GpuTemp), F(s.GpuLoad), F(s.GpuPower),
                        s.FanRpm.ToString(CultureInfo.InvariantCulture),
                        fanOut < 0 ? "" : fanOut.ToString("F0", CultureInfo.InvariantCulture),
                        st.Cap.ToString(CultureInfo.InvariantCulture),
                        (st.Boost ?? -1).ToString(CultureInfo.InvariantCulture)
                    }));

                    if (++i % 5 == 0)
                        Console.WriteLine($"   {sel,5:F0}s  CPU {s.CpuTemp,5:F1}°C  {s.CpuFrequencyMhz,5:F0}MHz  " +
                                          $"{s.CpuVoltage:F2}V  {s.CpuPower,5:F1}W  风扇 {s.FanRpm}RPM  负载 {s.CpuLoad:F0}%");

                    Thread.Sleep(Math.Max(200, plan.SampleMs));
                }

                manifest.Add(new
                {
                    st.Id, st.Label, st.Sec, st.Load, st.Cap, st.Boost, st.Fan,
                    start = stageStart.ToString("s"),
                    end = DateTime.Now.ToString("s"),
                    elapsedStart = Math.Round(swAll.Elapsed.TotalSeconds - swStage.Elapsed.TotalSeconds, 1),
                    elapsedEnd = Math.Round(swAll.Elapsed.TotalSeconds, 1),
                    samples = i
                });
            }
        }
        finally
        {
            _stress.Stop();
            if (pm.Available) pm.RestoreOriginal();
            hw.RestoreFanDefault();
            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                plan.Name, tag, startedAt = DateTime.Now.ToString("s"),
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                machine = Environment.MachineName,
                fanControl = hw.FanControlSupported,
                rpm = hw.RpmSupported,
                stages = manifest
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            _hw?.Dispose();
        }

        Console.WriteLine($"完成：{rows.Count - 1} 个样本");
        Console.WriteLine("CSV  : " + csvPath);
        Console.WriteLine("清单 : " + manifestPath);
        return _abort ? 3 : 0;
    }

    private static int CmdModel(string[] args)
    {
        string file = Arg(args, "--file", Path.Combine(Paths.DataDirectory, "lab", "model-policy.json"));
        if (!File.Exists(file)) { Console.Error.WriteLine("找不到模型文件：" + file); return 1; }
        var m = PolicyModel.Load(file);
        if (m == null) return 1;

        Console.WriteLine($"模型：{m.SourceCsv} / 模式 {m.Mode}（风扇{(m.FanControllable ? "可控" : "由固件控制")}）");
        Console.WriteLine($"稳态模型 {m.SteadyModel.Type}；τ={m.Dynamic.TauS:F0}s");

        if (args.Contains("--list"))
        {
            foreach (var row in m.ControlLadder)
            {
                Console.WriteLine($"目标 ≤{row.THi:F0}℃");
                foreach (var o in row.Options)
                    Console.WriteLine($"   {o.Mode,-8} cap={o.Cap,3}%  fan={(o.Fan?.ToString("F0") ?? "-"),4}%  " +
                                      $"{o.F}MHz  {o.P:F1}W  → 预测 {o.PredT:F1}℃  ({o.SplDeltaDb:+0.0;-0.0} dB)");
            }
            return 0;
        }

        double temp = double.TryParse(Arg(args, "--temp", "80"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) ? t : 80;
        string strat = Arg(args, "--strategy", "balanced").ToLowerInvariant();
        var mode = strat switch { "power" => PolicyModel.Strategy.Power, "quiet" => PolicyModel.Strategy.Quiet, _ => PolicyModel.Strategy.Balanced };
        var pick = m.Resolve(temp, mode);
        if (pick == null) { Console.WriteLine($"目标 {temp}℃ 在当前模型下不可达。"); return 2; }

        Console.WriteLine($"目标 {temp}℃ / {strat} → cap {pick.Cap}%，风扇 {(pick.Fan?.ToString("F0") ?? "BIOS")}%，" +
                          $"预测 {pick.PredT:F1}℃（{pick.SplDeltaDb:+0.0;-0.0} dB）");
        return 0;
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>默认实验计划：变频上限阶梯（这是本机唯一可精确控制的热源强度旋钮）。</summary>
    private static LabPlan DefaultPlan() => new()
    {
        Name = "cap-ladder",
        SampleMs = 1000,
        Stages = new List<LabStage>
        {
            new() { Id = "S0", Label = "空闲基线", Sec = 90, Load = "idle", Cap = 100 },
            new() { Id = "S1", Label = "满载 cap100", Sec = 180, Load = "cpu", Cap = 100 },
            new() { Id = "S2", Label = "满载 cap80", Sec = 150, Load = "cpu", Cap = 80 },
            new() { Id = "S3", Label = "满载 cap65", Sec = 150, Load = "cpu", Cap = 65 },
            new() { Id = "S4", Label = "满载 cap50", Sec = 150, Load = "cpu", Cap = 50 },
            new() { Id = "S5", Label = "满载 cap35", Sec = 150, Load = "cpu", Cap = 35 },
            new() { Id = "S6", Label = "冷却", Sec = 90, Load = "idle", Cap = 100 }
        }
    };
}
