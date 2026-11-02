namespace ThermalPilot.Core;

/// <summary>自动控制器一次决策的输出。</summary>
public sealed class AutoCpuDecision
{
    public bool Valid { get; init; }
    public double Pl1W { get; init; }
    public double Pl2W { get; init; }
    public double TauS { get; init; }
    public int Epp { get; init; }
    public double REff { get; init; }
    /// <summary>按「当前负载不变、把 PL1 抬到这个值」的**增量**预测温度（更贴近直觉）。</summary>
    public double PredictedC { get; init; }
    /// <summary>稳态绝对预测（室温 + R·PL1），只在 R 可信时给出，用于参数校验。</summary>
    public double PredictedSteadyC { get; init; } = double.NaN;
    public bool RConfident { get; init; }
    public string RQuality { get; init; } = "";
    /// <summary>这次决策受限时，按当前热阻最多还能给多少瓦（温度上限处）。</summary>
    public double PowerCapW { get; init; } = double.NaN;
    public string Reason { get; init; } = "";
    /// <summary>本次决策依据的限制原因（有性能采样时才有效）。</summary>
    public BindingLimit Limit { get; init; } = BindingLimit.Unknown;
    /// <summary>这一拍是"放开"还是"收紧"（0=保持，+1=升功率，-1=降功率）。</summary>
    public int Direction { get; init; }
}

/// <summary>
/// 智能自动 CPU 散热控制（闭环）。
///
/// **关键设计纠偏**：温度只是"约束"，不是"目标"。旧版把温度当 setpoint（压到目标值），
/// 于是出现了语义倒挂——「性能」档为了把温度压到最低，反而把功率预算压到最小，
/// 结果风扇最响、性能最低。正确的关系是：
///
///     T_ss = T_amb + R·P        ← 温度是「功率 × 热阻」的**结果**
///     性能   = f(交付频率)      ← 真正要最大化的东西
///
/// 所以这里改成「性能优先 + 温度上限」的控制律：
///   1) 用 (温度, 功率) 在线估计有效热阻 R = (T − T_amb)/P（慢 EMA），只用于给 PL1 一个起点；
///   2) 读 CPU 交付指标（PerfMetrics），判断**当前是谁在限制变快**：
///        · 温度墙（throttle 位 / 贴住上限）→ 降 PL1（加功率只会更热，无效）
///        · 功率墙（包功率顶在 PL1）      → 只要温度还有余量就**升** PL1（直接换性能）
///        · 频率上限（倍频已顶睿频上限）  → 保持（加功率也上不去）
///        · 未受限 / 轻载                → 保持或缓慢抬升，把余量吃满
///   3) 没有性能采样时退化为纯温度反馈（±3W / 过热 −6W，1℃ 回差），保证老机器也能用；
///   4) PL2 / Time / EPP 按档位给出，与 PL1 独立（PL2 管 burst 多高，τ 管撑多久）。
///
/// 目标温度的含义也随之改变：它是**允许的最高温度（ceiling）**，不是"要压到的值"。
/// 性能档 ceiling 更高（90℃）+ 风扇 100%，因此功率预算最大、交付频率最高；
/// 静音档 ceiling 88℃ 但风扇锁 55%，靠"允许它热"来省噪声，而不是靠削性能。
/// </summary>
public sealed class AutoCpuController
{
    private readonly ThermalResistance _r = new();
    private double _pl1 = double.NaN;
    private DateTime _last = default;

    /// <summary>热阻估计器（窗口最小二乘；只吃准稳态满载样本）。</summary>
    public ThermalResistance Resistance => _r;
    public bool RConfident => _r.Confident;
    public string RQuality => _r.Quality;

    /// <summary>绝对安全上限：任何档位都不允许长期超过（i7-7700HQ 的 TJmax 是 100℃）。</summary>
    public const double SafetyCeilingC = 95.0;

    public double CurrentPl1 => _pl1;

    /// <summary>在线估计的总热阻（K/W）；NaN = 还没估出来。参数校验用它预测稳态温度。</summary>
    public double REstimate => _r.Value;

    /// <summary>把内部 PL1 状态强制对齐到某个值（超温保护后避免用旧值反弹）。</summary>
    public void ForcePl1(double watts) => _pl1 = Math.Clamp(watts, 0, 500);

    public void Reset()
    {
        _r.Reset();
        _pl1 = double.NaN;
        _last = default;
    }

    /// <summary>清空热阻估计（换了硅脂/相变片、改了风扇改装后必须重估）。</summary>
    public void ResetResistance() => _r.Reset();

    /// <summary>
    /// 档位 → 允许的最高温度（ceiling），直接决定功率预算 (ceiling − T_amb)/R 的上界。
    ///
    /// 静音/平衡共用 88℃：两者用「风扇 + EPP + PL2/τ」区分，不拿温度上限区分。
    /// 这样"温度偏好"和"功率预算"彻底解耦——静音档不会因为想安静就顺带把性能削掉。
    /// 性能档放宽到 90℃ 且风扇 100%（R2 最小），所以 P_budget 最高，是真正"性能"档。
    /// 上限单调不减是硬性不变量（SelfTest 会校验 P_budget 单调）。
    /// </summary>
    public static double TargetCForProfile(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "performance" or "性能" => 90.0,
        _ => 88.0
    };

    /// <summary>档位 → 风扇上限%，与 TargetC 配对（声学预算花在这里）。</summary>
    public static int FanMaxForProfile(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "silent" or "静音" => 55,
        "performance" or "性能" => 100,
        _ => 75
    };

    /// <summary>档位 → EPP（0=性能 … 255=节能）。</summary>
    public static int EppForProfile(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "silent" or "静音" => 200,
        "performance" or "性能" => 0,
        _ => 128
    };

    /// <summary>档位 → PL2 相对 PL1 的倍率（burst 冲多高）。</summary>
    public static double Pl2Ratio(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "silent" or "静音" => 1.15,
        "performance" or "性能" => 1.35,
        _ => 1.25
    };

    /// <summary>
    /// 档位 → Time/τ 秒（burst 撑多久）。Intel 该字段是 log2 编码，只有 1/2/4/8/16/32/64 可精确表示，
    /// 所以这里直接用 2 的幂，避免「填 20 实际写 16」的显示不一致。
    /// </summary>
    public static double TauFor(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "silent" or "静音" => 8,
        "performance" or "性能" => 32,
        _ => 16
    };

    /// <summary>
    /// 根据当前温度/功耗/交付指标求下一步旋钮。minW/maxW 为 PL1 的物理边界（默认 10~80W）。
    /// ceilingC 是「允许的最高温度」，会被夹在 SafetyCeilingC 以内。
    /// perf 为 null 或无效时退化为纯温度反馈。
    /// </summary>
    public AutoCpuDecision Update(
        double tempC, double powerW, double ceilingC, double ambientC,
        string profile, double minW = 10, double maxW = 0, PerfSample? perf = null,
        double loadPct = double.NaN, double dTdt = double.NaN)
    {
        // maxW = 0 表示"用本机 CPU 设计功耗"（PlatformPower 探测值或用户手填）。
        // 不能写成默认参数常量：那是编译期值，换一台机器就错了。
        if (maxW <= 0) maxW = PlatformPower.CpuDesignW;
        var now = DateTime.UtcNow;
        double dt = _last == default ? 3.0 : Math.Clamp((now - _last).TotalSeconds, 0.5, 10.0);
        _last = now;

        ceilingC = Math.Clamp(ceilingC, 50, SafetyCeilingC);

        // 1) 在线热阻估计：只吃"准稳态 + 有负载"的样本，用 ΔT/ΔP 解斜率。
        //    旧版对每一拍做 (T−T_amb)/P 的 EMA，空闲样本（6W/41℃）算出 2.7~3.3 K/W，
        //    把真实值 1.2 顶掉，于是"预测 238℃"。详见 ThermalResistance 的注释。
        _r.TryAdd(powerW, tempC, loadPct, ambientC, dTdt, out _);
        bool rKnown = !double.IsNaN(_r.Value);
        double r = rKnown ? _r.Value : double.NaN;

        // 2) 前馈只用于给初始 PL1。R 还没估出来时不猜功率，先给保守值让反馈去收敛。
        if (double.IsNaN(_pl1))
            _pl1 = rKnown ? Math.Clamp((ceilingC - ambientC) / r, minW, maxW) : Math.Clamp(35.0, minW, maxW);

        // 3) 决策：有交付指标 → 按「谁在限制」调；没有 → 纯温度反馈
        int dir;
        string reason;
        var limit = BindingLimit.Unknown;

        if (perf is { Valid: true })
        {
            limit = perf.Limit;
            double headroom = ceilingC - tempC;      // >0 表示温度还有余量

            switch (perf.Limit)
            {
                case BindingLimit.Thermal:
                    // 已经撞温度墙：加功率只会更热，必须让
                    dir = -1;
                    reason = $"温度墙→降 PL1（{perf.LimitText}）";
                    break;

                case BindingLimit.Power:
                    // 功率墙且温度还有余量 → PL1 是唯一在卡性能的东西，放开它
                    dir = headroom > 2.0 ? +1 : 0;
                    reason = dir > 0
                        ? $"功率墙且余温 {headroom:F0}℃→升 PL1 换性能"
                        : $"功率墙但已贴温度上限→保持";
                    break;

                case BindingLimit.FrequencyCap:
                    dir = 0;
                    reason = $"频率已顶睿频上限 {perf.TurboLimitMhz:F0}MHz→加功率无效，保持";
                    break;

                case BindingLimit.Idle:
                    dir = 0;
                    reason = "轻载，需求低于任何限制→保持";
                    break;

                default:
                    // 未受限：温度还有明显余量就小幅抬升，把余量吃满；否则保持
                    dir = headroom > 4.0 ? +1 : 0;
                    reason = dir > 0 ? $"未受限且余温 {headroom:F0}℃→小幅抬升" : "未受限，已在最佳点";
                    break;
            }
        }
        else
        {
            // 退化路径：只看温度（1℃ 回差）
            double err = tempC - ceilingC;
            bool light = !double.IsNaN(loadPct) && loadPct < 30;
            if (err > 1.0) { dir = -1; reason = "偏热→降 PL1"; }
            else if (err < -3.0 && light)
            {
                // 轻载时"温度低"不是"该加功率"的证据：CPU 根本没在吃功率，
                // 抬 PL1 只会在下一次突发时留下一个过高的上限（旧版就是这样一路抬到 80W）。
                dir = 0; reason = "轻载偏冷，不据此抬 PL1";
            }
            else if (err < -3.0) { dir = +1; reason = "偏冷→升 PL1"; }
            else { dir = 0; reason = "接近上限"; }
        }

        // 4) 限速执行：升 3W/次（保守），降 6W/次（保命快）。温差大时允许按 dt 放大。
        if (dir != 0)
        {
            double step = dir > 0 ? 3.0 : -6.0;
            double desired = Math.Clamp(_pl1 + step * Math.Clamp(dt / 3.0, 0.5, 2.0), minW, maxW);
            _pl1 = Math.Clamp(desired, _pl1 - 6.0, _pl1 + 3.0);
        }

        // 5) 温度可行性门 —— 本次修正的核心。
        //
        //    旧版：只要 headroom = ceiling − temp > 4℃ 就 +3W。冷机/轻载时 headroom 有 40℃，
        //    于是一路抬到 maxW，完全不问"这个功率配这个热阻，稳态会到多少度"。
        //    正确做法：把功率换算成温升再判断：
        //        T_ss(PL1) = T_now + R · (PL1 − P_now)
        //    高出上限就把 PL1 收回到"刚好贴住上限"的那个功率：
        //        P_cap = P_now + (ceiling − margin − T_now) / R
        //
        //    注意这里用**增量**而不是 T_amb + R·PL1：绝对式依赖截距（室温+固定偏置），
        //    偏置 4℃ 在低功率下就会被放大成"预测 238℃"这种荒谬数；增量式只用斜率，
        //    与当前实测温度对齐，误差可控。
        double pCap = double.NaN;
        if (rKnown && powerW > 1)
        {
            double margin = 1.0;
            double headroom = ceilingC - tempC - margin;
            pCap = powerW + Math.Max(0, headroom) / r;
            pCap = Math.Clamp(pCap, minW, maxW);

            double predAtPl1 = tempC + r * (_pl1 - powerW);
            if (predAtPl1 > ceilingC - margin && _pl1 > pCap)
            {
                double before = _pl1;
                _pl1 = pCap;
                reason += $"；按 R={r:F2} 限到 {_pl1:F0}W（原 {before:F0}W，再高稳态约 {predAtPl1:F0}℃，超上限 {ceilingC:F0}℃）";
            }
        }

        // PL2 也受供电上限约束（本机 BIOS 出厂 PL2=56.25W）
        double pl2 = Math.Clamp(_pl1 * Pl2Ratio(profile), _pl1, ParamConstraints.PlatformPl2MaxW);
        double tau = TauFor(profile);
        int epp = EppForProfile(profile);

        // 预测：增量式（当前负载下把 PL1 抬到这里）＋ 稳态绝对式（仅供参考，需 R 可信）
        double pred = rKnown && powerW > 1 ? tempC + r * (_pl1 - powerW) : tempC;
        double predSteady = rKnown ? ambientC + r * _pl1 : double.NaN;

        string reasonText = reason;
        if (rKnown)
            reasonText += $"（R={r:F2} K/W{( _r.Confident ? "" : " 参考")}）";
        else if (!string.IsNullOrEmpty(_r.LastReject))
            reasonText += $"（热阻待估：{_r.LastReject}）";

        return new AutoCpuDecision
        {
            Valid = true,
            Pl1W = Math.Round(_pl1, 1),
            Pl2W = Math.Round(pl2, 1),
            TauS = tau,
            Epp = epp,
            REff = rKnown ? Math.Round(r, 3) : double.NaN,
            RConfident = _r.Confident,
            RQuality = _r.Quality,
            PredictedC = Math.Round(pred, 1),
            PredictedSteadyC = predSteady,
            PowerCapW = pCap,
            Reason = reasonText,
            Limit = limit,
            Direction = dir
        };
    }

    /// <summary>离线自检：收敛性 + 三种限制情形的决策方向是否正确。</summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();
        var c = new AutoCpuController();

        // A) 纯温度反馈（无指标）：R=2.0 的断链机，ceiling 86℃
        double ambient = 24, r = 2.0, ceiling = 86;
        double pl1 = 35;
        AutoCpuDecision? last = null;
        for (int i = 0; i < 60; i++)
        {
            double temp = ambient + r * pl1;
            last = c.Update(temp, pl1, ceiling, ambient, "balanced", 10, 80);
            pl1 = last.Pl1W;
        }
        if (last == null) { errors.Add("无决策输出"); return errors; }
        if (Math.Abs(last.PredictedC - ceiling) > 3.0)
            errors.Add($"收敛失败：预测 {last.PredictedC}℃，上限 {ceiling}℃");
        // 闭环收敛后功率几乎不变（35→31W，跨度 4W < 8W），拿不到"斜率激励"，
        // 这时只能用带固定偏置的绝对式粗估，允许 25% 误差；真正精确的估计需要功率变化，
        // 由 ThermalResistance.SelfTest 的 B 组（ΔP=30W）负责验证。
        if (Math.Abs(last.REff - r) > r * 0.25)
            errors.Add($"热阻粗估偏差过大：{last.REff} vs {r}（无激励时允许 25%）");
        if (last.RConfident)
            errors.Add("没有任何功率变化却把热阻标成「可信」");

        // B) 功率墙 + 温度有余量 → 必须升 PL1
        var c2 = new AutoCpuController();
        c2.ForcePl1(30);
        var d2 = c2.Update(70, 30, 90, 24, "performance", 10, 80,
            new PerfSample { Valid = true, Limit = BindingLimit.Power, LimitText = "功率墙", PackagePowerW = 30 });
        if (d2.Direction <= 0) errors.Add($"功率墙+余温时未升功率（dir={d2.Direction}）");
        if (d2.Pl1W <= 30) errors.Add($"功率墙+余温时 PL1 未提高：{d2.Pl1W}");

        // C) 温度墙 → 必须降 PL1，且不能因为"温度还在上限内"而升
        var c3 = new AutoCpuController();
        c3.ForcePl1(40);
        var d3 = c3.Update(91, 40, 90, 24, "performance", 10, 80,
            new PerfSample { Valid = true, Limit = BindingLimit.Thermal, LimitText = "温度墙", ThermalThrottle = true });
        if (d3.Direction >= 0) errors.Add($"温度墙时未降功率（dir={d3.Direction}）");
        if (d3.Pl1W >= 40) errors.Add($"温度墙时 PL1 未下降：{d3.Pl1W}");

        // D) 频率已达睿频上限 → 保持，不要浪费功率
        var c4 = new AutoCpuController();
        c4.ForcePl1(45);
        var d4 = c4.Update(80, 45, 90, 24, "performance", 10, 80,
            new PerfSample { Valid = true, Limit = BindingLimit.FrequencyCap, TurboLimitMhz = 3400, CurrentMhz = 3400 });
        if (d4.Direction != 0) errors.Add($"频率顶上限时不该调功率（dir={d4.Direction}）");
        if (Math.Abs(d4.Pl1W - 45) > 0.01) errors.Add($"频率顶上限时 PL1 变了：{d4.Pl1W}");

        // E) 语义单调性：性能档的**功率预算**必须最高（这才是"性能"的含义）。
        //    温度上限本身不是单调的（静音 88 > 平衡 86），因为它和风扇上限配对使用；
        //    真正要守住的不变量是 P_budget 随档位单调递增。
        double Budget(string profile)
        {
            const double r1 = 0.25, r2Hi = 0.50, r2Lo = 0.20, amb = 24;
            double d = FanMaxForProfile(profile) / 100.0;
            double r2 = r2Hi - (r2Hi - r2Lo) * d;
            return (TargetCForProfile(profile) - amb) / (r1 + r2);
        }
        double bs = Budget("silent"), bb = Budget("balanced"), bp = Budget("performance");
        if (!(bs < bb && bb < bp))
            errors.Add($"档位功率预算非单调（性能档应最高）：静音 {bs:F1}W / 平衡 {bb:F1}W / 性能 {bp:F1}W");

        return errors;
    }
}
