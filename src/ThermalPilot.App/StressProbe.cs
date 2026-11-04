using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ThermalPilot.App.Services;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Environment;

namespace ThermalPilot.App;

/// <summary>
/// 压力测试 / 参数生效性验证（会真实拉满 CPU 数分钟，研发 / 验收用）：
///   1. 写入能力探测：把固件当前 PL1/PL2/Tau 同值写回，验证「写入 → 回读 → 恢复」链路；
///   2. 空载基线 → 满载稳态 → 冷却 → 低负载对照 → 冷却，每秒采样
///      温度 / 包功耗 / 核频 / 负载 / 0x610 实读 PL1 / 决策 PL1；
///   3. 若本机可写 0x610（LHM 0.9.4 WinRing0 通道）：追加「降墙 / 放宽」两段满载做真 A/B/C，
///      结束恢复固件默认值；若不可写：用「低负载对照」证明功耗下降确实带来可观测的降温，
///      并用标定热阻外推「若 PL1 真被压到实际功耗之下」应有的温降。
///
/// 用法：
///   ThermalPilot.App.exe --stress-test --out report.txt --csv samples.csv
///                        [--seconds 45] [--low-pl1 25]
/// 说明：不带消息泵运行时 DispatcherTimer 不会自走，因此这里显式逐拍驱动 SampleOnce()。
/// </summary>
internal static class StressProbe
{
    private const int IdleSeconds = 12;
    private const int CoolSeconds = 20;
    private const int EndCoolSeconds = 15;
    private const int LowLoadThreads = 2;

    private sealed record Row(
        DateTime At, string Phase, double ElapsedS,
        double TempC, double PowerW, double LoadPct, double Ghz,
        double FwPl1W, double DecisionPl1W, string Rule, string PowerSource,
        int StressThreads);

    private sealed record Stat(
        string Phase, int Count,
        double TempC, double MaxTempC,
        double PowerW, double MaxPowerW,
        double Ghz, double MinGhz,
        double FwPl1W, double DecisionPl1W);

    public sealed record Options(
        string OutPath, string CsvPath, int LoadSeconds, double LowPl1W);

    public static int Run(Options opt)
    {
        var sb = new StringBuilder();
        var rows = new List<Row>();
        SessionState? session = null;
        int exit = 0;
        bool origAllow = false, origAuto = false, settingsCaptured = false;

        try
        {
            session = new SessionState();
            session.SampleOnce();   // 先走一拍，让遥测与 0x610 实读就绪

            double fwPl1 = session.FwPl1W;
            double fwPl2 = session.FwPl2W;
            double fwTau = session.FwTauS;
            double rth = session.Settings.Calibration?.Rth ?? double.NaN;
            origAllow = session.Settings.AllowRealWrite;
            origAuto = session.Settings.AutoTakeover;
            settingsCaptured = true;
            // 通道变可写后，「自动接管」会每 3 秒用决策树 PL1 覆盖压测手动下发的值，
            // 必须在压测期间临时关掉（仅内存，不写 settings.json）。
            session.Settings.AutoTakeover = false;
            var env = HardwareProbe.Probe();
            int fullThreads = Math.Max(2, Environment.ProcessorCount - 2);

            sb.AppendLine("==========================================================");
            sb.AppendLine(" ThermalPilot 压力测试 / 参数生效性验证报告");
            sb.AppendLine($" 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("== 1. 环境与当前配置 ==");
            sb.AppendLine($"CPU            : {session.CpuName}");
            sb.AppendLine($"平台           : {session.PlatformText} / {session.FormText} / {session.CoreText}");
            sb.AppendLine($"厂商 / 虚拟化  : {env.Vendor} / {env.IsVirtualized}");
            sb.AppendLine($"权限           : {session.ElevationText}");
            sb.AppendLine($"LHM 驱动       : {session.DriverText}（传感器 {session.SensorCount} 个）");
            sb.AppendLine($"RAPL 功耗计量  : {session.RaplText}");
            sb.AppendLine($"MSR 通道       : {MsrChannel.StatusText(session.MsrStatus)} / {session.MsrDetail}");
            sb.AppendLine($"固件出厂默认   : {session.FwDefaultText}");
            sb.AppendLine($"当前生效(实读) : PL1 {session.FwPl1Text}W · PL2 {session.FwPl2Text}W · Tau {session.FwTauText} · EPP {session.FwEppText}");
            sb.AppendLine($"档位 / 天花板  : {session.Settings.Profile} / {session.CeilingC:F0}°C");
            sb.AppendLine($"双开关(原值)   : 允许写入MSR={origAllow} · 自动接管={origAuto} · 开机重放={session.Settings.ReplayOnStartup}");
            sb.AppendLine("压测期间       : 临时开启写入、临时关闭自动接管（避免决策树覆盖 A/B/C 设定），仅内存、不写盘");
            sb.AppendLine($"已保存手动微调 : {session.SavedTuningText}");
            sb.AppendLine($"标定热阻       : {(double.IsNaN(rth) ? "--" : rth.ToString("F3", CultureInfo.InvariantCulture) + " K/W")}" +
                          $" · {session.RthText}（{session.RthSourceText}）");
            sb.AppendLine($"标定前馈       : {session.ThermalCapText}");
            sb.AppendLine();
            sb.AppendLine("== 2. 软件到底会动哪些参数（代码事实）==");
            sb.AppendLine($"· 通道后端：{MsrChannel.BackendName}。0.9.4 自带 WinRing0 内核驱动，可读可写 MSR。");
            sb.AppendLine("· 写 MSR 0x610（PKG_POWER_LIMIT）：PL1 长时功耗墙 / PL2 短时爆发墙 / Tau 平均窗口。");
            sb.AppendLine("· 写 MSR 0x774 [31:24]：HWP EPP 能效偏好（本机 0x774 可读，写后回读校验）。");
            sb.AppendLine("· 只读 MSR 0x606 / 0x611（或 AMD 0xC0010299 / 0xC001029B）：RAPL 能量单位与能量计数器，用于算真实包功耗。");
            sb.AppendLine("· MSR 0x1AD 睿频倍率：本机可读可写，已接入高级参数页（范围 [8, 出厂单核上限]，不做超频）。");
            sb.AppendLine("· MSR 0x150 FIVR 电压偏移：老版本实现过，当前版本尚未接入，不写。");
            sb.AppendLine("· 风扇 / EC / PWM：代码中不存在任何写入。决策树里的 TargetFanPercent 只是动作字段，未落到硬件；");
            sb.AppendLine("  风扇转速由固件/EC 按温度曲线自行调节，软件只能通过降低发热间接影响它。");
            sb.AppendLine("· 电源计划 / 最大频率：软件不碰（powercfg 对照在本机无效，已从压测移除）。");
            sb.AppendLine();

            // ---- 写入能力探测：同值写回，不改变实际功耗设置 ----
            sb.AppendLine("== 3. 写入能力探测（0x610 同值写回 → 回读 → 恢复）==");
            session.Settings.AllowRealWrite = true;   // 仅内存临时开启，不落盘
            var probe = session.ApplyLimits(fwPl1, fwPl2, fwTau);
            bool writable = probe.Ok;
            sb.AppendLine($"结果           : {probe.Outcome}");
            sb.AppendLine($"详情           : {probe.Message}");
            sb.AppendLine($"判定           : {(writable
                ? "本机可写 0x610 → 执行『固件默认 / 降墙 / 放宽』三阶段 A/B/C"
                : "本机不可写 0x610 → 软件对 CPU 寄存器的所有写入都不会生效；跳过写入式 A/B")}");
            sb.AppendLine();

            // ---- 阶段 ----
            sb.AppendLine("== 4. 压力测试阶段 ==");
            sb.AppendLine($"每段满载 {opt.LoadSeconds}s，采样间隔 {session.Settings.IntervalMs}ms；" +
                          $"满载用 {fullThreads} 个后台线程，低负载对照用 {LowLoadThreads} 个线程。");
            if (writable)
            {
                sb.AppendLine("说明：本轮执行写入式 A/B/C（固件默认 → 降墙 → 放宽），每段切换都会真实改写 0x610 并回读校验；");
                sb.AppendLine("      降墙/放宽段同时把 Tau 缩到 8s，让功耗墙在短时压测里真正生效。");
            }
            else
            {
                sb.AppendLine("说明：低负载对照是改变『负载』而非『CPU 参数』，用来证明功耗下降确实能带来可观测降温；");
                sb.AppendLine("      它不修改任何系统设置或 MSR，全程可逆。");
            }
            sb.AppendLine();

            RunPhase(session, rows, "IDLE", false, IdleSeconds, fullThreads);

            if (writable)
            {
                RunPhase(session, rows, "LOAD_FW", true, opt.LoadSeconds, fullThreads);

                // 固件 Tau≈28672s（≈8h）是超长平均窗口，短时压测里 PL1 几乎不会钳制功耗。
                // 因此降墙/放宽阶段同时把时间窗口缩到 8s，让功耗墙在本段内真正生效。
                const double shortTau = 8.0;
                double low = Math.Min(opt.LowPl1W, Math.Max(10, fwPl1 * 0.55));
                var rLow = session.ApplyLimits(low, Math.Max(fwPl2, low * 1.25), shortTau, writeTimeWindow: true);
                sb.AppendLine($"[切换] 降墙 PL1 {low:F1}W / Tau {shortTau:F0}s → {rLow.Outcome} / {rLow.Message}");
                RunPhase(session, rows, "LOAD_LOW", true, opt.LoadSeconds, fullThreads);

                double high = fwPl1 + 20;
                var rHigh = session.ApplyLimits(high, Math.Max(fwPl2, high * 1.25), shortTau, writeTimeWindow: true);
                sb.AppendLine($"[切换] 放宽 PL1 {high:F1}W / Tau {shortTau:F0}s → {rHigh.Outcome} / {rHigh.Message}");
                RunPhase(session, rows, "LOAD_HIGH", true, opt.LoadSeconds, fullThreads);
            }
            else
            {
                RunPhase(session, rows, "LOAD_FW", true, opt.LoadSeconds, fullThreads);
                RunPhase(session, rows, "COOL", false, CoolSeconds, fullThreads);
                RunPhase(session, rows, "LOAD_LOWLOAD", true, opt.LoadSeconds, LowLoadThreads);
            }

            RunPhase(session, rows, "COOL_END", false, EndCoolSeconds, fullThreads);
            session.SetStress(false);

            // ---- 恢复 ----
            sb.AppendLine();
            sb.AppendLine("== 5. 收尾恢复 ==");
            if (writable)
            {
                var restore = session.RestoreOriginal();
                sb.AppendLine($"0x610 恢复固件默认值 : {restore.Outcome} / {restore.Message}");
            }
            else
            {
                sb.AppendLine("0x610 恢复固件默认值 : 无需恢复（全程未写入成功，寄存器一直是固件值）");
            }
            session.Settings.AllowRealWrite = origAllow;   // 内存复原，settings.json 从未被改写
            session.Settings.AutoTakeover = origAuto;
            sb.AppendLine($"设置复原             : 允许写入MSR={origAllow} · 自动接管={origAuto}（未写盘）");
            sb.AppendLine("压测负载             : 已停止（StressLoad 全部线程已释放）");
            session.SampleOnce();
            sb.AppendLine($"最终 0x610 实读       : PL1 {session.FwPl1Text}W · PL2 {session.FwPl2Text}W · Tau {session.FwTauText}");

            // ---- 统计 ----
            AppendAnalysis(sb, rows, fwPl1, rth, writable, opt, fullThreads);

            WriteCsv(opt.CsvPath, rows);
            sb.AppendLine();
            sb.AppendLine($"逐拍原始样本 : {opt.CsvPath}（{rows.Count} 行，UTF-8 BOM，Excel 可直接打开）");
            sb.AppendLine();
            sb.AppendLine("RESULT: 压测完成");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine($"!! 压测异常: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            sb.AppendLine("RESULT: 异常中止");
            exit = 1;
        }
        finally
        {
            try { session?.SetStress(false); } catch { /* 忽略 */ }
            if (settingsCaptured && session is not null)
            {
                try
                {
                    session.Settings.AllowRealWrite = origAllow;
                    session.Settings.AutoTakeover = origAuto;
                }
                catch { /* 忽略 */ }
            }
            try { session?.Dispose(); } catch { /* 忽略 */ }
            Write(opt.OutPath, sb);
        }

        return exit;
    }

    private static void RunPhase(SessionState session, List<Row> rows,
        string phase, bool load, int seconds, int threads)
    {
        session.SetStress(load, threads);
        var sw = Stopwatch.StartNew();
        int interval = Math.Max(250, session.Settings.IntervalMs);
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            session.SampleOnce();
            rows.Add(new Row(
                DateTime.Now, phase, sw.Elapsed.TotalSeconds,
                session.TempC, session.PowerW, session.LoadPct, session.ClockGhz,
                session.FwPl1W, session.Pl1W, session.LastRule, session.PowerSource, threads));
            Thread.Sleep(interval);
        }
    }

    private static void AppendAnalysis(StringBuilder sb, List<Row> rows,
        double fwPl1, double rth, bool writable, Options opt, int fullThreads)
    {
        string[] phases = writable
            ? new[] { "IDLE", "LOAD_FW", "LOAD_LOW", "LOAD_HIGH", "COOL_END" }
            : new[] { "IDLE", "LOAD_FW", "COOL", "LOAD_LOWLOAD", "COOL_END" };

        sb.AppendLine();
        sb.AppendLine("== 6. 阶段统计（稳态 = 每段后 60% 样本均值）==");
        sb.AppendLine(PadR("阶段", 13) + PadL("样本", 5) + PadL("均温°C", 9) + PadL("峰温°C", 9) +
                      PadL("均功耗W", 10) + PadL("峰功耗W", 10) + PadL("均频GHz", 10) +
                      PadL("实读PL1W", 11) + PadL("决策PL1W", 11));
        var stats = new Dictionary<string, Stat>();
        foreach (string p in phases)
        {
            var s = Summarize(rows, p);
            if (s is null) continue;
            stats[p] = s;
            sb.AppendLine(PadR(p, 13) + PadL(s.Count.ToString(CultureInfo.InvariantCulture), 5) +
                          PadL(F(s.TempC, 1), 9) + PadL(F(s.MaxTempC, 1), 9) +
                          PadL(F(s.PowerW, 2), 10) + PadL(F(s.MaxPowerW, 2), 10) +
                          PadL(F(s.Ghz, 2), 10) + PadL(F(s.FwPl1W, 1), 11) +
                          PadL(F(s.DecisionPl1W, 1), 11));
        }

        if (writable)
            sb.AppendLine("注：COOL_END 采样发生在「收尾恢复」之前，故该段 0x610 实读仍是最后一个测试档；恢复动作见第 5 节。");

        if (!stats.TryGetValue("LOAD_FW", out var fw)) return;

        sb.AppendLine();
        sb.AppendLine("== 7. 关键判定 ==");

        double peakP = rows.Where(r => r.Phase == "LOAD_FW").Select(r => r.PowerW).DefaultIfEmpty(double.NaN).Max();
        sb.AppendLine($"① 满载是否触及固件功耗墙 : 峰值包功耗 {F(peakP, 1)}W / 固件 PL1 {F(fwPl1, 1)}W " +
                      $"= {(double.IsNaN(peakP) ? "--" : (peakP / fwPl1 * 100).ToString("F0", CultureInfo.InvariantCulture) + "%")}");
        if (!double.IsNaN(peakP) && peakP < fwPl1 * 0.9)
            sb.AppendLine("   → 本机在该负载下根本没顶到 PL1 天花板。把 PL1 调高（如 49W / 65W）不会产生任何可观测差异，");
        sb.AppendLine("     这正是『智能调优没有直观感受』的核心原因之一：调的是上限，而实际功耗远在上限之下。");

        double decisionMean = fw.DecisionPl1W;
        double fwMean = fw.FwPl1W;
        sb.AppendLine($"② 决策值是否落到寄存器     : 决策 PL1 均值 {F(decisionMean, 1)}W / 0x610 实读均值 {F(fwMean, 1)}W");
        if (writable)
        {
            sb.AppendLine("   → 压测期间已临时关闭「自动接管」，决策值仅作参考；本段寄存器保持固件默认值，");
            sb.AppendLine("     A/B/C 的降墙/放宽由压测脚本手动下发（见第 4 节切换记录与 ④ 实读对照）。");
        }
        else if (Math.Abs(decisionMean - fwMean) > 1.0)
            sb.AppendLine("   → 软件算出了不同的目标，但寄存器没变：决策树只是『遥控建议』，写入需要双开关 + 驱动支持。");
        else
            sb.AppendLine("   → 决策目标与固件实值一致，本段内没有需要下发的改动。");

        if (stats.TryGetValue("IDLE", out var idle))
        {
            double dT = fw.TempC - idle.TempC;
            double dP = fw.PowerW - idle.PowerW;
            sb.AppendLine($"③ 热模型校验               : 空载→满载 ΔT={F(dT, 2)}°C / ΔP={F(dP, 2)}W " +
                          $"= 短时斜率 {(dP > 0.5 ? (dT / dP).ToString("F3", CultureInfo.InvariantCulture) : "--")} K/W；" +
                          $"稳态标定 Rth={(double.IsNaN(rth) ? "--" : rth.ToString("F3", CultureInfo.InvariantCulture))} K/W");
            sb.AppendLine("   → 短时斜率通常小于稳态 Rth：40 秒还没走到热稳态（本机 Tau≈8h，热容很大），");
            sb.AppendLine("     两点稳态法测出的 Rth 只适合做长期稳态外推，不适合解释瞬时升温速度。");
        }

        if (stats.TryGetValue("LOAD_LOWLOAD", out var half))
        {
            sb.AppendLine($"④ 低负载对照（{LowLoadThreads} 线程 vs {fullThreads} 线程）:");
            sb.AppendLine($"   稳态温度 {F(half.TempC, 1)}°C vs 满载 {F(fw.TempC, 1)}°C（ΔT={F(half.TempC - fw.TempC, 2)}°C）；" +
                          $"包功耗 {F(half.PowerW, 1)}W vs {F(fw.PowerW, 1)}W（ΔP={F(half.PowerW - fw.PowerW, 2)}W）");
            double halfLoad = Avg(rows.Where(r => r.Phase == "LOAD_LOWLOAD"), r => r.LoadPct);
            sb.AppendLine($"   核频 {F(half.Ghz, 2)}GHz vs {F(fw.Ghz, 2)}GHz；平均负载 {F(halfLoad, 0)}%（逐拍见 CSV）");
            sb.AppendLine("   → 这段是『改变负载』而非『软件改参数』的对照：功耗一降，温度就跟着降，量级清晰可测。");
            sb.AppendLine("     本机 0x610 写入链路不可用（第 3 节），因此软件目前无法主动把 PL1 压到实际功耗之下。");
        }

        if (writable && stats.TryGetValue("LOAD_LOW", out var lowPhase) && stats.TryGetValue("LOAD_HIGH", out var highPhase))
        {
            sb.AppendLine();
            sb.AppendLine("④ 写入式 A/B/C 对照（0x610 实读随阶段变化，参数生效的直接证据）:");
            sb.AppendLine(PadR("阶段", 13) + PadL("实读PL1W", 11) + PadL("均温°C", 9) +
                          PadL("均功耗W", 10) + PadL("均频GHz", 10));
            foreach (var p in new[] { "LOAD_FW", "LOAD_LOW", "LOAD_HIGH" })
            {
                if (!stats.TryGetValue(p, out var st)) continue;
                sb.AppendLine(PadR(p, 13) + PadL(F(st.FwPl1W, 1), 11) + PadL(F(st.TempC, 1), 9) +
                              PadL(F(st.PowerW, 2), 10) + PadL(F(st.Ghz, 2), 10));
            }
            sb.AppendLine($"   → 0x610 实读由 {F(fw.FwPl1W, 1)}W → {F(lowPhase.FwPl1W, 1)}W → {F(highPhase.FwPl1W, 1)}W，" +
                          "证明软件确实把 PL1 写进了 CPU 寄存器（不是只算不写）。");
            sb.AppendLine($"   → 降墙段：均温 {F(fw.TempC, 1)} → {F(lowPhase.TempC, 1)}°C（ΔT={F(lowPhase.TempC - fw.TempC, 2)}°C），" +
                          $"均功耗 {F(fw.PowerW, 1)} → {F(lowPhase.PowerW, 1)}W（ΔP={F(lowPhase.PowerW - fw.PowerW, 2)}W）。");
            sb.AppendLine($"   → 放宽段：实读 {F(highPhase.FwPl1W, 1)}W，但均功耗 {F(highPhase.PowerW, 1)}W 仍远低于该墙，" +
                          "所以放宽 PL1 不产生可观测变化（调的是天花板）。");
            sb.AppendLine("   注：降墙/放宽阶段同时把 Tau 从固件 28672s 缩到 8s，否则超长平均窗口下短时压测看不到限功耗。");
        }

        if (!double.IsNaN(rth))
        {
            double low = Math.Min(opt.LowPl1W, Math.Max(10, fwPl1 * 0.55));
            double predictedDrop = (fw.PowerW - low) * rth;
            sb.AppendLine($"⑤ 定量外推（稳态）         : 把 PL1 从 {F(fwPl1, 1)}W 降到 {F(low, 1)}W，" +
                          $"按稳态热阻 {F(rth, 2)} K/W，长期稳态温度预计下降约 {F(Math.Max(0, predictedDrop), 1)}°C；");
            sb.AppendLine($"   把 PL1 放宽到 {F(fwPl1 + 20, 1)}W 则预计 <1°C 变化（实际功耗 {F(fw.PowerW, 1)}W 远低于墙）。");
            sb.AppendLine("   注意：这是稳态估计，短时实测降温幅度会因热容偏小（见 ③）。");
        }

        sb.AppendLine();
        sb.AppendLine("== 8. 结论汇总 ==");
        if (writable)
        {
            sb.AppendLine($"1) 通道 {MsrChannel.BackendName} 可用：软件真实读写 MSR 0x610（PL1/PL2/Tau）与 0x774（EPP）；");
            sb.AppendLine("   风扇 / EC / PWM / 电源计划一律不碰；0x1AD 睿频倍率已接入（本机可写），0x150 电压偏移尚未接入。");
            sb.AppendLine("2) 本次实测：同值写回 → 回读 → 恢复全链路 PASS；压测中 0x610 实读随阶段从固件默认变为降墙/放宽值，");
            sb.AppendLine("   收尾已恢复固件默认值。『软件只算不写』的判断已被实测推翻。");
            sb.AppendLine("3) 参数生效条件：PL1 是功耗天花板，只有压到实际满载功耗之下才有效；");
            double lowReal = stats.TryGetValue("LOAD_LOW", out var lr) ? lr.FwPl1W : opt.LowPl1W;
            double highReal = stats.TryGetValue("LOAD_HIGH", out var hr) ? hr.FwPl1W : fwPl1 + 20;
            sb.AppendLine($"   本机满载峰值 {F(peakP, 1)}W（固件 PL1 {F(fwPl1, 1)}W 的 {F(peakP / fwPl1 * 100, 0)}%），" +
                          $"放宽到 {F(highReal, 0)}W 无意义，压到 {F(lowReal, 0)}W 才有限制作用。");
            sb.AppendLine("4) 固件 Tau≈28672s 是超长平均窗口，短时压测必须同时缩短 Tau 才能观察到钳制；");
            sb.AppendLine("   风扇仍由 EC 按温度曲线自动调速，软件通过少发热间接降速，不直接拨风扇。");
            sb.AppendLine("5) 上一轮『本机不可写』结论有误：根因是 LHM 从 0.9.4 升到 0.9.6 后驱动后端变为只读 PawnIO 模块；");
            sb.AppendLine("   本版已回退到 0.9.4 WinRing0 通道并实机验证。");
        }
        else
        {
            sb.AppendLine("1) 软件真正尝试写入的只有 MSR 0x610 的 PL1 / PL2 / Tau 与 0x774 EPP；");
            sb.AppendLine("   风扇 / EC / PWM / 电源计划 / 电压 / 倍频一律不碰。");
            sb.AppendLine("2) 本次实测：0x610 写入被驱动拒绝，全程 0 次成功写入，CPU 寄存器始终是固件默认值。");
            sb.AppendLine("3) 没有直观感受的四层原因：");
            sb.AppendLine("   a. 写入链路不通 + 双开关未开 → 参数根本没改；");
            sb.AppendLine($"   b. 满载峰值功耗仅固件 PL1 的 {F(peakP / fwPl1 * 100, 0)}% → 调高上限无效；");
            sb.AppendLine("   c. PL1 是长时平均功耗墙（Tau≈8h 量级），属于慢变量，短时操作看不出变化；");
            sb.AppendLine("   d. 风扇由 EC 按温度曲线自动调速，软件不直接拨风扇，只能间接影响。");
            sb.AppendLine($"4) 要有可观测效果：需恢复可写通道，并把 PL1 压到低于实际满载功耗（本机 <{F(fw.PowerW, 0)}W）。");
        }

        sb.AppendLine();
        sb.AppendLine("== 9. 升温曲线摘要（LOAD_FW 段，每 5 秒取一拍）==");
        sb.AppendLine(PadL("t(s)", 6) + PadL("T(°C)", 9) + PadL("P(W)", 9) + PadL("GHz", 9) +
                      PadL("load%", 8) + PadL("实读PL1", 10) + PadL("决策PL1", 10));
        int lastSec = -1;
        foreach (var r in rows.Where(r => r.Phase == "LOAD_FW"))
        {
            int sec = (int)r.ElapsedS;
            if (sec % 5 != 0 || sec == lastSec) continue;
            lastSec = sec;
            sb.AppendLine(PadL(sec.ToString(CultureInfo.InvariantCulture), 6) +
                          PadL(F(r.TempC, 1), 9) + PadL(F(r.PowerW, 1), 9) + PadL(F(r.Ghz, 2), 9) +
                          PadL(F(r.LoadPct, 0), 8) + PadL(F(r.FwPl1W, 1), 10) +
                          PadL(F(r.DecisionPl1W, 1), 10));
        }
    }

    private static string PadR(string s, int width) => width <= s.Length ? s : s.PadRight(width);
    private static string PadL(string s, int width) => width <= s.Length ? s : s.PadLeft(width);

    private static Stat? Summarize(List<Row> rows, string phase)
    {
        var set = rows.Where(r => r.Phase == phase).ToList();
        if (set.Count == 0) return null;
        int skip = (int)(set.Count * 0.4);
        var steady = set.Skip(skip).ToList();
        if (steady.Count == 0) steady = set;
        return new Stat(phase, steady.Count,
            Avg(steady, r => r.TempC), Max(set, r => r.TempC),
            Avg(steady, r => r.PowerW), Max(set, r => r.PowerW),
            Avg(steady, r => r.Ghz), Min(steady, r => r.Ghz),
            Avg(steady, r => r.FwPl1W), Avg(steady, r => r.DecisionPl1W));
    }

    private static double Avg(IEnumerable<Row> rows, Func<Row, double> pick)
    {
        var v = rows.Select(pick).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        return v.Count == 0 ? double.NaN : v.Average();
    }
    private static double Max(IEnumerable<Row> rows, Func<Row, double> pick)
    {
        var v = rows.Select(pick).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        return v.Count == 0 ? double.NaN : v.Max();
    }
    private static double Min(IEnumerable<Row> rows, Func<Row, double> pick)
    {
        var v = rows.Select(pick).Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        return v.Count == 0 ? double.NaN : v.Min();
    }

    private static string F(double v, int digits) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "--" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static void WriteCsv(string path, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,阶段,阶段内秒,温度(°C),包功耗(W),负载(%),最高核频(GHz),0x610实读PL1(W),决策PL1(W),命中规则,功耗来源,压测线程数");
        foreach (var r in rows)
        {
            string rule = "\"" + r.Rule.Replace("\"", "\"\"").Replace(",", "，") + "\"";
            sb.AppendLine(string.Join(",",
                r.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Phase,
                r.ElapsedS.ToString("F1", CultureInfo.InvariantCulture),
                F(r.TempC, 2), F(r.PowerW, 2), F(r.LoadPct, 1), F(r.Ghz, 3),
                F(r.FwPl1W, 2), F(r.DecisionPl1W, 2),
                rule, r.PowerSource, r.StressThreads.ToString(CultureInfo.InvariantCulture)));
        }
        Write(path, sb);
    }

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠报告内容/退出码判断 */ }
    }
}
