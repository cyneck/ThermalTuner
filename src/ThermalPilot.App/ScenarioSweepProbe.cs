using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ThermalPilot.App.Services;
using ThermalPilot.Core.Stress;

namespace ThermalPilot.App;

/// <summary>
/// 场景化压力测试 / 功耗覆盖扫描（只读压测，不写任何 MSR）：
///   以 0x610 实读 PL1 作为该机器的「默认稳态功率」，用标准场景阶梯
///   （空载 / 整数 / 标量浮点 / AVX2-FMA / 内存带宽 / 混合 / 脉冲 × 不同线程数）
///   逐级加压，测出每种场景能到达多少功耗、占默认功率的百分比。
///
/// 目的：通用产品不能假设任何机型的瓦数，也不能只用一种循环当「满载」。
/// 只有先找出能逼近实读 PL1 的场景，后续 A/B/C 降墙压测才有意义。
///
/// 用法：
///   ThermalPilot.App.exe --scenario-sweep --out report.txt --csv samples.csv
///                        [--seconds 12] [--target-ratio 0.9]
/// 不带消息泵运行时 DispatcherTimer 不会自走，因此逐拍显式驱动 SampleOnce()。
/// </summary>
internal static class ScenarioSweepProbe
{
    public sealed record Options(string OutPath, string CsvPath, int Seconds, double TargetRatio);

    private sealed record Row(
        DateTime At, string Scenario, double ElapsedS,
        double TempC, double PowerW, double LoadPct, double Ghz,
        double EffectivePl1W, double DecisionPl1W, string PowerSource,
        int Threads, StressKernel Kernel);

    private sealed record Stat(
        string Scenario, StressKernel Kernel, int Threads, int Count,
        double TempC, double MaxTempC,
        double PowerW, double MaxPowerW,
        double Ghz, double LoadPct);

    public static int Run(Options opt)
    {
        var sb = new StringBuilder();
        var rows = new List<Row>();
        SessionState? session = null;
        int exit = 0;
        bool origAuto = false, captured = false;

        try
        {
            session = new SessionState();
            session.SampleOnce();   // 让遥测与 0x610 实读就绪

            origAuto = session.Settings.AutoTakeover;
            captured = true;
            // 只读压测：避免自动接管在扫描期间改写 0x610（仅内存，不写盘）
            session.Settings.AutoTakeover = false;

            // 通用基准：能读到就用实读值，绝不回退到机型查表或硬编码瓦数
            double defaultPowerW = session.DefaultPowerW;
            if (double.IsNaN(defaultPowerW) || defaultPowerW <= 0)
                defaultPowerW = session.FwPl1W;
            bool haveDefault = !double.IsNaN(defaultPowerW) && defaultPowerW > 0;

            int cores = Math.Max(1, session.Environment.LogicalCores);
            var ladder = StressScenarioCatalog.StandardLadder(cores, opt.Seconds);

            sb.AppendLine("==========================================================");
            sb.AppendLine(" ThermalPilot 场景化压测 / 功耗覆盖扫描报告");
            sb.AppendLine($" 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("== 1. 基准（全部来自实读，不含机型常量）==");
            sb.AppendLine($"CPU            : {session.CpuName}");
            sb.AppendLine($"逻辑核 / 物理核: {session.Environment.LogicalCores}L / {session.Environment.PhysicalCores}P");
            var fw = session.FirmwareDefaults;
            sb.AppendLine($"固件出厂基线   : {(fw is { } d0 ? $"PL1 {F(d0.Pl1W, 1)}W · PL2 {F(d0.Pl2W, 1)}W · Tau {F(d0.TauS, 0)}s" : "不可读")}" +
                          "（启动时备份，固件值不可持久化修改；软件不运行时 CPU 按此运转）");
            sb.AppendLine($"默认稳态功率   : {(haveDefault ? F(defaultPowerW, 1) + " W（固件出厂默认 PL1）" : "不可读（无实读基准，覆盖率退化为相对比较）")}");
            string tauNow = double.IsNaN(session.FwTauS) ? "--" : F(session.FwTauS, 0) + "s";
            sb.AppendLine($"当前生效实读   : PL1 {session.FwPl1Text}W · PL2 {session.FwPl2Text}W · Tau {tauNow}");
            if (fw is { } d1)
            {
                bool diverged = Math.Abs(d1.Pl1W - session.FwPl1W) > 0.5
                                || Math.Abs(d1.Pl2W - session.FwPl2W) > 0.5
                                || Math.Abs(d1.TauS - session.FwTauS) > 0.5;
                sb.AppendLine($"软件重放状态   : {(diverged
                    ? $"0x610 已偏离出厂基线（Tau {F(d1.TauS, 0)}s → {F(session.FwTauS, 0)}s），" +
                      "说明开机重放已把软件侧微调写入寄存器；短 Tau 下 PL1 墙会在压测中真实钳制。"
                    : "当前生效值与出厂基线一致（或未启用重放）。")}");
            }
            sb.AppendLine($"功耗计量来源   : {session.PowerCaption} / {session.RaplText}");
            sb.AppendLine($"SIMD/FMA 可用  : {(StressWorker.SimdAvailable ? "是（AVX2 + FMA）" : "否（回退 Vector<T> 或标量）")}");
            sb.AppendLine($"目标覆盖率     : {F(opt.TargetRatio * 100, 0)}%（峰值功耗 / 默认功率）");
            sb.AppendLine($"采样间隔       : {session.Settings.IntervalMs} ms");
            sb.AppendLine();
            sb.AppendLine("== 2. 场景阶梯 ==");
            sb.AppendLine("从轻载到重载覆盖不同执行单元与线程规模；场景连续执行，温度存在累积效应，");
            sb.AppendLine("因此「是否触及功耗墙」以稳态段峰值功耗判定；切换瞬间的残留读数已剔除。");
            foreach (var sc in ladder)
                sb.AppendLine("  " + sc.Describe);
            sb.AppendLine();
            sb.AppendLine("== 3. 逐场景执行 ==");

            var stats = new List<Stat>();
            foreach (var sc in ladder)
            {
                var before = rows.Count;
                var sw = Stopwatch.StartNew();
                session.SetStress(sc.IsLoad, sc);
                int interval = Math.Max(250, session.Settings.IntervalMs);
                while (sw.Elapsed.TotalSeconds < sc.DurationSeconds)
                {
                    session.SampleOnce();
                    rows.Add(new Row(
                        DateTime.Now, sc.Name, sw.Elapsed.TotalSeconds,
                        session.TempC, session.PowerW, session.LoadPct, session.ClockGhz,
                        session.FwPl1W, session.Pl1W, session.PowerSource,
                        sc.Threads, sc.Kernel));
                    Thread.Sleep(interval);
                }

                var slice = rows.Skip(before).ToList();
                var st = Summarize(sc, slice);
                stats.Add(st);
                double cover = haveDefault ? st.MaxPowerW / defaultPowerW : double.NaN;
                sb.AppendLine($"  {sc.Name,-14} 均功耗 {F(st.PowerW, 2),7} W · 峰功耗 {F(st.MaxPowerW, 2),7} W" +
                              $" · 均温 {F(st.TempC, 1),5} °C · 均频 {F(st.Ghz, 2),5} GHz" +
                              $" · 覆盖 {(double.IsNaN(cover) ? "--" : F(cover * 100, 0) + "%"),6}");
            }

            session.SetStress(false);

            sb.AppendLine();
            sb.AppendLine("== 4. 汇总表（稳态 = 每段后 60% 样本均值）==");
            sb.AppendLine(PadR("场景", 15) + PadL("样本", 6) + PadL("内核", 14) + PadL("线程", 6) +
                          PadL("均温°C", 9) + PadL("峰温°C", 9) + PadL("均功耗W", 10) +
                          PadL("峰功耗W", 10) + PadL("覆盖%", 8) + PadL("均频GHz", 10));
            foreach (var st in stats)
            {
                double cover = haveDefault ? st.MaxPowerW / defaultPowerW : double.NaN;
                sb.AppendLine(PadR(st.Scenario, 15) + PadL(st.Count.ToString(CultureInfo.InvariantCulture), 6) +
                              PadL(st.Kernel.ToString(), 14) + PadL(st.Threads.ToString(CultureInfo.InvariantCulture), 6) +
                              PadL(F(st.TempC, 1), 9) + PadL(F(st.MaxTempC, 1), 9) +
                              PadL(F(st.PowerW, 2), 10) + PadL(F(st.MaxPowerW, 2), 10) +
                              PadL(double.IsNaN(cover) ? "--" : F(cover * 100, 0), 8) +
                              PadL(F(st.Ghz, 2), 10));
            }

            sb.AppendLine();
            sb.AppendLine("== 5. 判定 ==");
            var loads = stats.Where(s => s.Kernel != StressKernel.Idle).ToList();
            var best = loads.OrderByDescending(s => s.MaxPowerW).FirstOrDefault();

            if (best is not null)
            {
                double bestCover = haveDefault ? best.MaxPowerW / defaultPowerW : double.NaN;
                sb.AppendLine($"最高功耗场景     : {best.Scenario}（{best.Kernel} / {best.Threads} 线程）" +
                              $" 峰值 {F(best.MaxPowerW, 2)} W");
                if (haveDefault)
                {
                    var hit = loads.Where(s => s.MaxPowerW >= defaultPowerW * opt.TargetRatio)
                                   .OrderBy(s => s.MaxPowerW).FirstOrDefault();
                    sb.AppendLine($"相对默认功率     : {F(bestCover * 100, 0)}%（默认 {F(defaultPowerW, 1)} W）");
                    if (hit is not null)
                    {
                        sb.AppendLine($"可触发功耗墙场景 : {hit.Scenario} 起即可达目标覆盖率，" +
                                      "后续 A/B/C 降墙压测应至少使用该场景。");
                        sb.AppendLine("   → 峰值已贴近实读 PL1，负载确实顶到了功耗墙；此时放宽 PL1 才可能带来收益，");
                        sb.AppendLine("     而把 PL1 压到该峰值之下会立刻钳制（前提：Tau 为秒级短窗口，否则短时压测看不到钳制）。");
                    }
                    else
                    {
                        sb.AppendLine("未找到触墙场景   : 全部场景峰值功耗都低于默认功率 × 目标覆盖率。");
                        sb.AppendLine("   → 说明瓶颈不在 PL1 天花板，而在 CPU 选频 / 电流墙 / 散热策略；");
                        sb.AppendLine("     此时把 PL1 往上调物理等价，只有把 PL1 压到实测功耗之下才有效。");
                    }

                    var over = loads.Where(s => s.MaxPowerW > defaultPowerW * 1.05).ToList();
                    if (over.Count > 0)
                        sb.AppendLine($"   → 注意：{string.Join("、", over.Select(s => s.Scenario))} 峰值超过 PL1，" +
                                      "可能是 PL2 短时窗口或 Tau 平均窗口尚未钳制，需结合 0x610 实读复核。");
                }
                else
                {
                    sb.AppendLine("实读基准不可用   : 无法计算覆盖率；请确认 RAPL/LHM 功耗传感器可读（必要时以管理员运行）。");
                }
            }
            else
            {
                sb.AppendLine("未采到任何负载场景样本，检查遥测是否可用。");
                exit = 1;
            }

            sb.AppendLine();
            sb.AppendLine("== 6. 边界说明 ==");
            sb.AppendLine("· 本探针只读：不写 0x610 / 0x774 / 0x1AD，不碰风扇 / EC / PWM / 电源计划 / 电压。");
            sb.AppendLine("· 风扇由 EC 按温度曲线自动调速，软件只能通过改变发热量间接影响转速。");
            sb.AppendLine("· 默认功率取 MsrGuard 启动时备份的固件出厂默认 PL1，不是硬编码常量，也不是重放后的当前值。");
            sb.AppendLine("· 固件默认值不可持久化修改；软件只持久化标定与微调结果，开机重放写入 0x610，退出后 CPU 回到固件默认。");
            sb.AppendLine("· 场景功耗受电源计划、散热状态、后台负载影响，换机或换散热条件应重跑本扫描。");

            WriteCsv(opt.CsvPath, rows);
            sb.AppendLine();
            sb.AppendLine($"逐拍原始样本 : {opt.CsvPath}（{rows.Count} 行，UTF-8 BOM）");
            sb.AppendLine("RESULT: 场景扫描完成");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine($"!! 扫描异常: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            sb.AppendLine("RESULT: 异常中止");
            exit = 1;
        }
        finally
        {
            try { session?.SetStress(false); } catch { /* 忽略 */ }
            if (captured && session is not null)
            {
                try { session.Settings.AutoTakeover = origAuto; } catch { /* 忽略 */ }
            }
            try { session?.Dispose(); } catch { /* 忽略 */ }
            Write(opt.OutPath, sb);
        }

        return exit;
    }

    private static Stat Summarize(StressScenario sc, List<Row> slice)
    {
        if (slice.Count == 0)
            return new Stat(sc.Name, sc.Kernel, sc.Threads, 0,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        // 场景切换瞬间会残留上一场景的功耗/温度读数（曾出现 COOL 空载段峰值 34W 的伪影），
        // 因此均值与峰值统一取稳态段，不把切换点算进「可达功耗」。
        int skip = (int)(slice.Count * 0.4);
        var steady = slice.Skip(skip).ToList();
        return new Stat(
            sc.Name, sc.Kernel, sc.Threads, slice.Count,
            Avg(steady, r => r.TempC), steady.Max(r => r.TempC),
            Avg(steady, r => r.PowerW), steady.Max(r => r.PowerW),
            Avg(steady, r => r.Ghz), Avg(steady, r => r.LoadPct));
    }

    private static double Avg(IEnumerable<Row> src, Func<Row, double> pick)
    {
        var vals = src.Select(pick).Where(v => !double.IsNaN(v)).ToList();
        return vals.Count == 0 ? double.NaN : vals.Average();
    }

    private static string F(double v, int digits) => double.IsNaN(v)
        ? "--" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static string PadR(string s, int w) => s.PadRight(w);
    private static string PadL(string s, int w) => s.PadLeft(w);

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 报告写盘失败不影响主流程退出码 */ }
    }

    private static void WriteCsv(string path, List<Row> rows)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                "Time,Scenario,ElapsedS,TempC,PowerW,LoadPct,Ghz,EffectivePl1W,DecisionPl1W,PowerSource,Threads,Kernel"
            };
            foreach (var r in rows)
                lines.Add(string.Join(",",
                    r.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    r.Scenario,
                    F(r.ElapsedS, 2), F(r.TempC, 2), F(r.PowerW, 3), F(r.LoadPct, 1), F(r.Ghz, 3),
                    F(r.EffectivePl1W, 1), F(r.DecisionPl1W, 1),
                    Quote(r.PowerSource), r.Threads.ToString(CultureInfo.InvariantCulture), r.Kernel));
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }
        catch { /* CSV 写盘失败不影响报告 */ }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";
}
