using System.Globalization;

namespace ThermalPilot.Core;

/// <summary>最小二乘工具（无第三方依赖）。</summary>
public static class Fit
{
    /// <summary>一元线性回归 y = a + b·x，返回 (a, b, R²)。点数不足返回 (0,0,0)。</summary>
    public static (double a, double b, double r2) Linear(IReadOnlyList<(double x, double y)> pts)
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
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx < 1e-9) return (my, 0, 0);
        double b = sxy / sxx;
        double a = my - b * mx;
        double r2 = syy < 1e-9 ? 1 : (sxy * sxy) / (sxx * syy);
        return (a, b, Math.Clamp(r2, 0, 1));
    }
}

/// <summary>标定任务编排：阶段越完整，拟合越可靠。</summary>
public static class CalibrationPlans
{
    public static readonly string[] Levels = { "快速", "标准", "完整" };

    public static List<CalibrationStage> Build(string level) => level switch
    {
        "快速" => new List<CalibrationStage>
        {
            new() { Name = "空闲基线", Description = "空载 30 秒，测环境温度基线", DurationSec = 30, Load = StressKind.Idle, CapPercent = 100 },
            new() { Name = "满载·满上限", Description = "CPU 满载，频率上限 100%", DurationSec = 70, Load = StressKind.Cpu, CapPercent = 100 },
            new() { Name = "满载·上限 60%", Description = "CPU 满载，频率上限 60%（拟合降频收益）", DurationSec = 70, Load = StressKind.Cpu, CapPercent = 60 },
            new() { Name = "满载·风扇 100%", Description = "CPU 满载，风扇强制 100%（拟合散热能力）", DurationSec = 60, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 100 }
        },
        "完整" => new List<CalibrationStage>
        {
            new() { Name = "空闲基线", Description = "空载 45 秒，测环境温度基线", DurationSec = 45, Load = StressKind.Idle, CapPercent = 100 },
            new() { Name = "满载·满上限", Description = "CPU 满载，频率上限 100%", DurationSec = 90, Load = StressKind.Cpu, CapPercent = 100 },
            new() { Name = "满载·上限 80%", Description = "CPU 满载，频率上限 80%", DurationSec = 90, Load = StressKind.Cpu, CapPercent = 80 },
            new() { Name = "满载·上限 60%", Description = "CPU 满载，频率上限 60%", DurationSec = 90, Load = StressKind.Cpu, CapPercent = 60 },
            new() { Name = "满载·上限 45%", Description = "CPU 满载，频率上限 45%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 45 },
            new() { Name = "满载·风扇 100%", Description = "CPU 满载，风扇强制 100%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 100 },
            new() { Name = "满载·风扇 70%", Description = "CPU 满载，风扇强制 70%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 70 },
            new() { Name = "满载·风扇 45%", Description = "CPU 满载，风扇强制 45%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 45 },
            new() { Name = "CPU+GPU 混合", Description = StressTest.ExternalGpuHint, DurationSec = 90, Load = StressKind.CpuGpu, CapPercent = 100 },
            new() { Name = "冷却", Description = "空载冷却 60 秒", DurationSec = 60, Load = StressKind.Idle, CapPercent = 100 }
        },
        _ => new List<CalibrationStage>   // 标准
        {
            new() { Name = "空闲基线", Description = "空载 40 秒，测环境温度基线", DurationSec = 40, Load = StressKind.Idle, CapPercent = 100 },
            new() { Name = "满载·满上限", Description = "CPU 满载，频率上限 100%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 100 },
            new() { Name = "满载·上限 70%", Description = "CPU 满载，频率上限 70%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 70 },
            new() { Name = "满载·上限 50%", Description = "CPU 满载，频率上限 50%", DurationSec = 80, Load = StressKind.Cpu, CapPercent = 50 },
            new() { Name = "满载·风扇 100%", Description = "CPU 满载，风扇强制 100%", DurationSec = 70, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 100 },
            new() { Name = "满载·风扇 60%", Description = "CPU 满载，风扇强制 60%", DurationSec = 70, Load = StressKind.Cpu, CapPercent = 100, FanOverride = 60 },
            new() { Name = "冷却", Description = "空载冷却 45 秒", DurationSec = 45, Load = StressKind.Idle, CapPercent = 100 }
        }
    };

    public static int TotalSeconds(IEnumerable<CalibrationStage> plan) => plan.Sum(s => s.DurationSec);
}

/// <summary>
/// 由实测模型生成曲线：
///   · 风扇曲线：以"把满载温度压到目标温度所需的风扇%"为锚点，按温度线性升到满转；
///   · 频率曲线：目标温度之前不限制性能，超过后按热模型反解需要的频率上限。
/// 生成结果始终可解释（每条都写进 Notes），高手可在高级面板逐点微调。
/// </summary>
public static class CurveGenerator
{
    public static (Curve fan, Curve freq, List<string> notes) Generate(ThermalModel m, CalibrationPreference p)
    {
        var notes = new List<string>();
        double t = p.TargetTemp;

        if (!m.CapValid && !m.FanValid)
        {
            notes.Add("实测数据不足（未取得可用温度/稳态点），已回退到内置预设曲线。");
            notes.Add("请确认：以管理员运行、能读到 CPU 温度、压测阶段未被打断。");
            return (Presets.Fan(p.Name), Presets.Freq(p.Name), notes);
        }

        if (m.CapValid)
            notes.Add($"降频收益：每降低 10% 频率上限约降 {Math.Abs(m.CapB * 10):F1} °C（R²={m.CapR2:F2}，{m.CapPoints.Count} 个稳态点）。");
        if (m.FanValid)
            notes.Add($"散热能力：每提高 10% 风扇约降 {Math.Abs(m.FanB * 10):F1} °C（R²={m.FanR2:F2}，{m.FanPoints.Count} 个稳态点）。");
        if (m.IdleTemp > 0 && m.FullLoadTemp > 0)
            notes.Add($"实测：空闲 {m.IdleTemp:F1} °C → 满载 {m.FullLoadTemp:F1} °C。");

        // ---- 风扇曲线 ----
        double fanAtTarget = m.FanValid ? m.FanForTemp(t) : (m.CapValid ? 70 : 65);
        fanAtTarget = Math.Clamp(fanAtTarget * p.Aggressiveness, p.MinFanPct, 100);
        double fanKnee = Math.Clamp(fanAtTarget + (100 - fanAtTarget) * 0.6, fanAtTarget, 100);

        var fanPts = new List<CurvePoint>
        {
            new(30, p.MinFanPct),
            new(Math.Max(34, t - 14), p.MinFanPct),
            new(t - 8, Math.Max(p.MinFanPct, fanAtTarget * 0.78)),
            new(t - 2, fanAtTarget),
            new(t + 4, fanKnee),
            new(t + 10, Math.Min(100, fanKnee + (100 - fanKnee) * 0.8)),
            new(95, 100)
        };

        // 静音偏好：日常压在噪声上限内，只有接近保护温度才允许满转
        if (p.NoiseCapPct < 100)
        {
            for (int i = 0; i < fanPts.Count; i++)
            {
                double temp = fanPts[i].Temperature;
                double limit = temp >= 90 ? 100 : temp >= t + 6 ? Math.Min(100, p.NoiseCapPct + 12) : p.NoiseCapPct;
                fanPts[i] = new CurvePoint(temp, Math.Min(fanPts[i].Value, limit));
            }
        }

        for (int i = 0; i < fanPts.Count; i++)
            fanPts[i] = new CurvePoint(fanPts[i].Temperature, Math.Clamp(fanPts[i].Value, p.MinFanPct, 100));

        // ---- 频率曲线 ----
        double capSoft = m.CapValid ? m.CapForTemp(t + 3) : 90;
        double capHard = m.CapValid ? m.CapForTemp(t + 8) : 70;
        capSoft = Math.Clamp(capSoft, 45, 100);
        capHard = Math.Clamp(Math.Min(capHard, capSoft - 5), 30, 100);

        var freqPts = new List<CurvePoint>
        {
            new(40, 100),
            new(Math.Max(44, t - 6), 100),
            new(t + 2, capSoft),
            new(t + 8, capHard),
            new(95, Math.Clamp(capHard - 10, 25, 100))
        };

        for (int i = 1; i < freqPts.Count; i++)
            freqPts[i] = new CurvePoint(freqPts[i].Temperature, Math.Min(freqPts[i].Value, freqPts[i - 1].Value));

        notes.Add($"目标温度 {t:F0} °C：风扇约需 {fanAtTarget:F0}%，推荐曲线已按「{p.Name}」偏好生成（激进系数 {p.Aggressiveness:F2}）。");
        if (!m.FanValid)
            notes.Add("未测到风扇散热模型（可能无调速通道），风扇曲线按经验生成，实际以降频曲线为主。");

        var fan = new Curve { Points = fanPts };
        var freq = new Curve { Points = freqPts };
        fan.Normalize();
        freq.Normalize();
        return (fan, freq, notes);
    }
}

/// <summary>
/// 标定执行器：按计划跑多阶段压测 → 采样 → 稳态判定 → 拟合热模型 → 生成推荐曲线。
/// 全程可取消；结束时一定还原电源方案与风扇控制权。
/// </summary>
public sealed class CalibrationEngine
{
    public event Action<string, int, int>? StageChanged;      // 阶段名、序号、总数
    public event Action<int, int>? Tick;                      // 本阶段已用秒 / 本阶段总秒
    public event Action<string>? Message;                     // 提示文本
    public event Action<CalibrationSample>? Sampled;

    public bool IsRunning { get; private set; }

    public async Task<CalibrationResult> RunAsync(
        ThermalEngine engine,
        List<CalibrationStage> plan,
        CalibrationPreference pref,
        CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("标定已在运行");

        IsRunning = true;
        var hw = engine.Hardware;
        var power = engine.Power;
        var cfg = engine.Config;
        var stress = new StressTest();
        var logger = new SampleLogger();
        var samples = new List<CalibrationSample>();
        var model = new ThermalModel();
        var notes = new List<string>();

        var started = DateTime.Now;
        logger.Open();

        try
        {
            // 不再 engine.Pause()：暂停会让风扇交回 BIOS —— 满载 77℃ 时风扇只有 22%，
            // 「温度」和「风扇」两列都对不上，标定数据作废，机器还在高温空转。
            // 改成标定接管：风扇与功率上限由阶段给定，超温保护仍然优先。
            engine.SetCalibrationStage(-1, 0);

            for (int si = 0; si < plan.Count; si++)
            {
                var stage = plan[si];
                ct.ThrowIfCancellationRequested();
                StageChanged?.Invoke(stage.Name, si + 1, plan.Count);
                Message?.Invoke(stage.Description);

                // 阶段开始时定好风扇值并交给引擎接管（曲线模式下按当前温度取一次，之后固定，
                // 这样 CSV 里「风扇」列就是一个确定的、可复核的数）。
                double stageFan;
                if (stage.FanOverride is double forcedFan) stageFan = forcedFan;
                else
                {
                    var probe = hw.Read();
                    stageFan = cfg.FanCurve.Evaluate(probe.Hotspot, cfg.FanMinPct);
                }
                stageFan = Math.Clamp(stageFan, 20, 100);   // 不低于 20%：满载阶段不允许把风扇停掉
                engine.SetCalibrationStage(stageFan, stage.CapPercent ?? 100);

                stress.Start(stage.Load);

                var stageSamples = new List<CalibrationSample>();
                for (int sec = 0; sec < stage.DurationSec; sec++)
                {
                    ct.ThrowIfCancellationRequested();

                    var snap = hw.Read();
                    var sample = new CalibrationSample
                    {
                        Time = DateTime.Now,
                        Stage = stage.Name,
                        // 标定也吃抗毛刺后的温度：单点离群值会让稳态判定（标准差）失败，
                        // 也会把 R 的拟合点拉偏。原始值另存一列，便于事后核对。
                        CpuTemp = snap.CpuTempShown,
                        CpuTempRaw = snap.CpuTemp,
                        GpuTemp = snap.GpuTemp,
                        CpuLoad = snap.CpuLoad,
                        GpuLoad = snap.GpuLoad,
                        CpuMhz = snap.CpuFrequencyMhz,
                        CpuPower = snap.CpuPower,
                        FanRpm = snap.FanRpm,
                        FanOut = snap.FanSoftwarePct < 0 ? stageFan : snap.FanSoftwarePct,
                        Cap = stage.CapPercent ?? 100,
                        FanCmd = stageFan
                    };
                    stageSamples.Add(sample);
                    samples.Add(sample);
                    logger.Write(sample, started, string.Join("|", snap.Fans.Select(f => $"{f.Name}:{f.Rpm}")));
                    Sampled?.Invoke(sample);
                    Tick?.Invoke(sec + 1, stage.DurationSec);

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                stress.Stop();

                var steady = Steady(stageSamples, out bool isSteady);
                if (steady == null) { notes.Add($"阶段「{stage.Name}」未取到温度，已跳过建模。"); continue; }

                if (!isSteady)
                    notes.Add($"阶段「{stage.Name}」未完全稳态（波动 {(steady.Max - steady.Min):F1}°C），取其均值参与拟合。");

                if (stage.Load == StressKind.Idle && stage.Name.Contains("空闲"))
                    model.IdleTemp = steady.Mean;

                if (stage.Load is StressKind.Cpu or StressKind.CpuGpu && stage.FitsCapModel)
                {
                    model.CapPoints.Add((stage.CapPercent!.Value, steady.Mean));
                    if (stage.CapPercent >= 100) model.FullLoadTemp = steady.Mean;
                }
                if (stage.Load is StressKind.Cpu or StressKind.CpuGpu && stage.FitsFanModel)
                    model.FanPoints.Add((stage.FanOverride!.Value, steady.Mean));

                Log.Info($"阶段「{stage.Name}」稳态均值 {steady.Mean:F1}°C（{stageSamples.Count} 样本，稳态={isSteady}）");
            }

            var (ca, cb, cr2) = Fit.Linear(model.CapPoints);
            model.CapA = ca; model.CapB = cb; model.CapR2 = cr2; model.CapValid = model.CapPoints.Count >= 2;
            var (fa, fb, fr2) = Fit.Linear(model.FanPoints);
            model.FanA = fa; model.FanB = fb; model.FanR2 = fr2; model.FanValid = model.FanPoints.Count >= 2;

            var (fan, freq, gen) = CurveGenerator.Generate(model, pref);
            notes.InsertRange(0, gen);

            if (!power.Available) notes.Add("powercfg 不可用，降频相关阶段无效。");
            if (!hw.FanControlSupported) notes.Add("本机无风扇调速通道，风扇曲线不会真正生效（降频曲线仍然生效）。");
            // 功率上限走 MSR（powercfg 的 PROCTHROTTLEMAX 在不少笔记本上写进去不生效）：
            // 通道不可用时降频阶段的点全部不可信，必须在结果里说清楚，不能静默给出曲线。
            if (plan.Any(st => st.CapPercent is > 0 and < 100) && !engine.Cpu.EnsureAvailable())
                notes.Add("MSR 通道不可用（" + engine.Cpu.LastError + "）：降频阶段的功率上限没有生效，"
                          + "这些点已按满载处理，降频模型不可信。以管理员身份运行并关闭内存完整性后重试。");

            return new CalibrationResult
            {
                Success = samples.Count > 20 && (model.CapValid || model.FanValid),
                Summary = $"采集 {samples.Count} 个样本，{plan.Count} 个阶段；" +
                          (model.CapValid ? $"降频模型 R²={model.CapR2:F2}；" : "降频模型不足；") +
                          (model.FanValid ? $"散热模型 R²={model.FanR2:F2}" : "散热模型不足"),
                Model = model,
                FanCurve = fan,
                FreqCurve = freq,
                Notes = notes,
                Samples = samples,
                CsvPath = logger.FilePath
            };
        }
        finally
        {
            stress.Stop();
            stress.Dispose();
            logger.Close();
            // 交回引擎正常决策（而不是直接 RestoreFanDefault）：标定完用户应该看到
            // 风扇按自己的曲线继续工作，而不是忽然被交回 BIOS。
            engine.EndCalibration();
            try { power.RestoreOriginal(); } catch { }
            IsRunning = false;
            Message?.Invoke("标定结束，风扇与功率已交回当前配置。");
        }
    }

    private sealed class SteadyResult
    {
        public double Mean { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
    }

    /// <summary>取最后一段窗口的均值做稳态估计（窗口 = 后 45%，至少 5 个样本）。</summary>
    private static SteadyResult? Steady(List<CalibrationSample> list, out bool isSteady)
    {
        isSteady = false;
        var valid = list.Where(s => s.CpuTemp > 0).ToList();
        if (valid.Count < 5) return null;

        int take = Math.Clamp((int)(valid.Count * 0.45), 5, valid.Count);
        var window = valid.TakeLast(take).Select(s => s.CpuTemp).ToList();
        double mean = window.Average();
        double min = window.Min(), max = window.Max();
        isSteady = max - min <= 2.0;
        return new SteadyResult { Mean = mean, Min = min, Max = max };
    }
}
