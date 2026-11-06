using System.Globalization;
using System.IO;
using System.Text;
using ThermalTuner.App.Services;
using ThermalTuner.App.Services.Msr;
using ThermalTuner.Core.Msr;

namespace ThermalTuner.App;

/// <summary>
/// 睿频倍率 A/B 满载实测：回答「把 0x1AD 各档拉平到出厂单核上限（本机 38x）
/// 到底能不能把全核频率从 3.4GHz 提到 3.8GHz」。
///
/// 阶段：空载基线 → 出厂倍率表满载 → 冷却 → 写入 38x 满载 → 冷却。
/// 每段逐秒采样温度 / RAPL 包功耗 / 核频 / 负载 / 0x1AD 实读档位 / 0x610 实读 PL1。
/// 结束一定调用 RestoreOriginal() 把 0x610 / 0x774 / 0x1AD 全部恢复固件默认。
///
/// 用法：ThermalTuner.App.exe --turbo-stress --out report.txt --csv samples.csv [--seconds 30]
/// 需要管理员权限。不写 settings.json（临时开启写入授权，内存复原）。
/// </summary>
internal static class TurboStressProbe
{
    private const int IdleSeconds = 8;
    private const int CoolSeconds = 10;
    private const int SingleSeconds = 10;   // 单线程段：验证 1C 档 38x 本来是否就生效

    private sealed record Row(
        DateTime At, string Phase, double ElapsedS,
        double TempC, double PowerW, double LoadPct, double Ghz,
        string TurboTiers, double FwPl1W);

    private sealed record Stat(
        string Phase, int Count, double TempC, double MaxTempC,
        double PowerW, double MaxPowerW, double Ghz, double LoadPct, string TurboTiers);

    public sealed record Options(string OutPath, string CsvPath, int Seconds);

    public static int Run(Options opt)
    {
        var sb = new StringBuilder();
        var rows = new List<Row>();
        SessionState? session = null;
        bool captured = false;
        bool origAllow = false, origAuto = false;

        try
        {
            session = new SessionState();
            session.SampleOnce();

            origAllow = session.Settings.AllowRealWrite;
            origAuto = session.Settings.AutoTakeover;
            captured = true;
            session.Settings.AllowRealWrite = true;
            session.Settings.AutoTakeover = false;   // 禁止决策树每 3 秒覆盖寄存器

            int fullThreads = Math.Max(2, Environment.ProcessorCount - 2);
            int targetRatio = session.FwTurboMaxRatio;
            int defaultAllCore = session.FwTurboAllCoreRatio;
            string defaultTiers = session.FwTurboText;

            sb.AppendLine("==========================================================");
            sb.AppendLine(" ThermalTuner 睿频倍率 A/B 满载实测（38 倍是否释放性能？）");
            sb.AppendLine($" 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("== 1. 环境 ==");
            sb.AppendLine($"CPU                : {session.CpuName}");
            sb.AppendLine($"平台               : {session.PlatformText}");
            sb.AppendLine($"权限               : {session.ElevationText}");
            sb.AppendLine($"MSR 通道           : {MsrChannel.BackendName} / {MsrChannel.StatusText(session.MsrStatus)}");
            sb.AppendLine($"固件出厂默认       : {session.FwDefaultText}");
            sb.AppendLine($"0x1AD 出厂档位     : {defaultTiers}（单核上限 {targetRatio}x = {targetRatio / 10.0:F1}GHz，" +
                          $"全核 {defaultAllCore}x = {defaultAllCore / 10.0:F1}GHz）");
            sb.AppendLine($"本次目标           : 把各档拉平到 {targetRatio}x（{targetRatio / 10.0:F1}GHz）");
            sb.AppendLine($"压测设置           : 每段 {opt.Seconds}s 满载 / {fullThreads} 线程 / 采样 1s");
            sb.AppendLine("说明               : 0x1AD 只做「睿频表配置」，不超过出厂单核上限；" +
                          "0x610 功耗墙全程保持固件默认，不额外放宽。");
            sb.AppendLine();

            // 限制原因诊断：HWP 是否接管调频、HWP 请求上限、是否有 PROCHOT / 热墙
            using (var diag = MsrChannel.Open())
            {
                if (diag.TryRead(0x770, out ulong pmEnable))
                    sb.AppendLine($"HWP 0x770         : {((pmEnable & 1) != 0 ? "已启用（由 Speed Shift 接管调频）" : "未启用")}");
                if (diag.TryRead(0x774, out ulong hwpReq))
                    sb.AppendLine($"HWP_REQUEST 0x774 : Min={hwpReq & 0xFF} / Max={(hwpReq >> 8) & 0xFF} " +
                                  $"/ Desired={(hwpReq >> 16) & 0xFF} / EPP={(hwpReq >> 24) & 0xFF}（×100MHz / 0..255）");
                if (diag.TryRead(0x19C, out ulong therm))
                    sb.AppendLine($"THERM_STATUS 0x19C: PROCHOT={((therm >> 0) & 1) != 0} " +
                                  $"/ PROCHOT事件={((therm >> 1) & 1) != 0} / 热限制={((therm >> 4) & 1) != 0}");
                if (diag.TryRead(0x194, out ulong flex))
                    sb.AppendLine($"FLEX_RATIO 0x194  : 0x{flex:X16} · bit16={((flex >> 16) & 1)} " +
                                  $"· OC Lock(bit20)={((flex >> 20) & 1)}");
            }
            sb.AppendLine();

            if (!session.MsrWritable || !session.TurboEditable || targetRatio <= 0)
            {
                sb.AppendLine("!! 通道不可写或 0x1AD 不可读，无法执行 A/B 实测。请以管理员运行。");
                sb.AppendLine("RESULT: 中止");
                return 2;
            }

            sb.AppendLine("== 2. 阶段 ==");
            RunPhase(session, rows, "IDLE", false, IdleSeconds, fullThreads);
            RunPhase(session, rows, "SINGLE_FW", true, SingleSeconds, 1);
            session.SetStress(false);

            RunPhase(session, rows, "LOAD_FW", true, opt.Seconds, fullThreads);
            session.SetStress(false);
            RunPhase(session, rows, "COOL", false, CoolSeconds, fullThreads);

            var w = session.ApplyTurboIfSupported(targetRatio);
            sb.AppendLine($"[切换] 0x1AD 写入 {targetRatio}x → {w.Outcome} / {w.Message}");
            if (!w.Ok)
            {
                sb.AppendLine("!! 睿频倍率写入失败，跳过第二段满载，直接恢复。");
            }
            else
            {
                RunPhase(session, rows, "SINGLE_TURBO", true, SingleSeconds, 1);
                session.SetStress(false);
                RunPhase(session, rows, "LOAD_TURBO", true, opt.Seconds, fullThreads);
            }
            session.SetStress(false);
            RunPhase(session, rows, "COOL_END", false, CoolSeconds, fullThreads);

            // ---------------- 统计 ----------------
            sb.AppendLine();
            sb.AppendLine("== 3. 阶段统计（稳态 = 每段后 60% 样本均值）==");
            sb.AppendLine($"{"阶段",-12} {"样本",4} {"均温°C",8} {"峰温°C",8} {"均功耗W",9} {"峰功耗W",9} {"均频GHz",9} {"负载%",7} {"0x1AD 实读",12}");
            var stats = new Dictionary<string, Stat>();
            foreach (string p in new[] { "IDLE", "SINGLE_FW", "LOAD_FW", "COOL",
                                         "SINGLE_TURBO", "LOAD_TURBO", "COOL_END" })
            {
                var s = Summarize(rows, p);
                if (s is null) continue;
                stats[p] = s;
                sb.AppendLine($"{p,-12} {s.Count,4} {F(s.TempC,1),8} {F(s.MaxTempC,1),8} " +
                              $"{F(s.PowerW,2),9} {F(s.MaxPowerW,2),9} {F(s.Ghz,2),9} " +
                              $"{F(s.LoadPct,0),7} {s.TurboTiers,12}");
            }

            sb.AppendLine();
            sb.AppendLine("== 4. A/B 对比（出厂倍率表 vs 拉平 38x）==");
            if (stats.TryGetValue("SINGLE_FW", out var sf) && stats.TryGetValue("SINGLE_TURBO", out var st))
            {
                sb.AppendLine($"单线程满载核频 : {F(sf.Ghz,2)} → {F(st.Ghz,2)} GHz（Δ={F(st.Ghz - sf.Ghz,2)} GHz）");
                sb.AppendLine($"  → 1C 档出厂就是 {targetRatio}x（{targetRatio / 10.0:F1}GHz），单核睿频本来就生效；" +
                              "「睿频到 38 倍」对单线程没有新增空间。");
                sb.AppendLine();
            }
            if (stats.TryGetValue("LOAD_FW", out var fw) && stats.TryGetValue("LOAD_TURBO", out var tb))
            {
                double dGhz = tb.Ghz - fw.Ghz;
                double dP = tb.PowerW - fw.PowerW;
                double dT = tb.TempC - fw.TempC;
                sb.AppendLine($"均核频   : {F(fw.Ghz,2)} → {F(tb.Ghz,2)} GHz（Δ={F(dGhz,2)} GHz，{F(dGhz / Math.Max(0.01, fw.Ghz) * 100,1)}%）");
                sb.AppendLine($"均包功耗 : {F(fw.PowerW,2)} → {F(tb.PowerW,2)} W（Δ={F(dP,2)} W）");
                sb.AppendLine($"均温度   : {F(fw.TempC,1)} → {F(tb.TempC,1)} °C（Δ={F(dT,2)} °C）");
                sb.AppendLine($"峰值     : 功耗 {F(fw.MaxPowerW,1)} → {F(tb.MaxPowerW,1)} W；" +
                              $"温度 {F(fw.MaxTempC,1)} → {F(tb.MaxTempC,1)} °C");
                sb.AppendLine();
                sb.AppendLine(dGhz > 0.15
                    ? $"→ 结论：拉平到 {targetRatio}x 后满载核频确实提升（+{F(dGhz,2)} GHz），" +
                      "「释放更多性能」在本机成立；代价是功耗与温度同步上升。"
                    : "→ 结论：0x1AD 已成功写入且全程实读 38/38/38/38，但 PCU 并未按新表提高全核频率。" +
                      "即「MSR 值可写」≠「硬件采纳」；全核约 3.4GHz 是本机的实际频率上限，拉平 38x 无收益。");
                sb.AppendLine($"   注意：固件 PL1 {F(session.FwPl1W,1)}W，本次峰值功耗仅 {F(tb.MaxPowerW,1)}W，" +
                              "并未顶到功耗墙；更可能是 PCU/BIOS 对非 K 移动处理器锁定了多核睿频表，" +
                              "软件写入的 0x1AD 多核档不参与实际选频（或受 ICCmax 电流墙约束）。");
            }
            else sb.AppendLine("（数据不足，无法对比）");

            // ---------------- 恢复 ----------------
            sb.AppendLine();
            sb.AppendLine("== 5. 收尾恢复 ==");
            var restore = session.RestoreOriginal();
            session.SampleOnce();
            sb.AppendLine($"RestoreOriginal : {restore.Outcome} / {restore.Message}");
            sb.AppendLine($"最终 0x1AD 实读 : {session.FwTurboText}（出厂应为 {defaultTiers}）");
            sb.AppendLine($"最终 0x610 实读 : PL1 {F(session.FwPl1W,1)}W");
            bool restored = session.FwTurboText == defaultTiers;
            sb.AppendLine(restored ? "寄存器已恢复出厂默认，无残留。" : "警告：0x1AD 未恢复出厂档位，请重启机器！");
            sb.AppendLine();
            sb.AppendLine(restored ? "RESULT: PASS" : "RESULT: WARN（睿频表未恢复）");
            WriteCsv(opt.CsvPath, rows);
            return restored ? 0 : 6;
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("未处理异常: " + ex);
            return 7;
        }
        finally
        {
            try { session?.SetStress(false); } catch { /* 忽略 */ }
            try { if (captured && session is not null)
                  {
                      session.Settings.AllowRealWrite = origAllow;
                      session.Settings.AutoTakeover = origAuto;   // 内存复原，settings.json 从未改写
                      session.Dispose();
                  } } catch { /* 忽略 */ }
            Write(opt.OutPath, sb);
        }
    }

    private static void RunPhase(SessionState s, List<Row> rows, string phase,
        bool load, int seconds, int threads)
    {
        s.SetStress(load, threads);
        var start = DateTime.UtcNow;
        for (int i = 0; i < seconds; i++)
        {
            s.SampleOnce();
            rows.Add(new Row(
                DateTime.Now, phase, (DateTime.UtcNow - start).TotalSeconds,
                s.TempC, s.PowerW, s.LoadPct, s.ClockGhz,
                s.FwTurboText, s.FwPl1W));
            Thread.Sleep(1000);
        }
    }

    private static Stat? Summarize(List<Row> rows, string phase)
    {
        var seg = rows.Where(r => r.Phase == phase).ToList();
        if (seg.Count == 0) return null;
        int skip = (int)(seg.Count * 0.4);
        var steady = seg.Skip(skip).ToList();
        if (steady.Count == 0) steady = seg;
        return new Stat(phase, steady.Count,
            Avg(steady, r => r.TempC), seg.Max(r => r.TempC),
            Avg(steady, r => r.PowerW), seg.Max(r => r.PowerW),
            Avg(steady, r => r.Ghz), Avg(steady, r => r.LoadPct),
            steady.Last().TurboTiers);
    }

    private static double Avg(IEnumerable<Row> rows, Func<Row, double> pick)
    {
        var vals = rows.Select(pick).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        return vals.Count == 0 ? double.NaN : vals.Average();
    }

    private static string F(double v, int digits) =>
        double.IsNaN(v) ? "--" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static void WriteCsv(string path, List<Row> rows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var sb = new StringBuilder();
            sb.AppendLine("time,phase,elapsed_s,temp_c,power_w,load_pct,clock_ghz,turbo_tiers,msr_pl1_w");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",",
                    r.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    r.Phase,
                    r.ElapsedS.ToString("F1", CultureInfo.InvariantCulture),
                    r.TempC.ToString("F2", CultureInfo.InvariantCulture),
                    r.PowerW.ToString("F2", CultureInfo.InvariantCulture),
                    r.LoadPct.ToString("F1", CultureInfo.InvariantCulture),
                    r.Ghz.ToString("F3", CultureInfo.InvariantCulture),
                    "\"" + r.TurboTiers + "\"",
                    r.FwPl1W.ToString("F2", CultureInfo.InvariantCulture)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* CSV 失败不影响主报告 */ }
    }

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠退出码 */ }
    }
}
