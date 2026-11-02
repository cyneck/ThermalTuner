using System;
using System.Collections.Generic;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>
/// 一条待应用的参数变更（用于「待应用 N 项」的清单展示）。
/// </summary>
public sealed class ParamChange
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public ParamTier Tier { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public override string ToString() => $"{Label}：{From} → {To}";
}

public sealed class ApplyOutcome
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<ParamViolation> Violations { get; init; } = Array.Empty<ParamViolation>();
    public int AppliedCount { get; init; }
}

/// <summary>
/// 参数中心：全局配置的**唯一真源**。
///
/// 三层模型（这是本次重构的核心）：
///
///   Draft（草稿）  —— 各个面板编辑的就是它。改动只停留在内存，**不碰硬件**。
///   Committed（已应用）—— 上一次成功应用、当前硬件上真正生效的值。
///   Preview（预览）—— 标定结果 / 自动控制器决策 / MSR 现值，只读派生，永远不进 Diff。
///
/// 为什么必须这么分：
///   · 旧版每个面板各有一套"取向"，顶层下拉还有一个 ProfileCombo，四套变量谁也不管谁，
///     用户改了一处以为生效了，实际生效的是另一处 —— 这就是"体验不到模型"的直接原因。
///   · 旧版没有 Commit 这一步，控件一改就可能被引擎的定时器读到并下发，等于"边拖边写硬件"，
///     没有回退点、没有校验窗口，用户自定义极值确实可能把机器搞坏。
///
/// 现在所有写入硬件的动作都必须经过 <see cref="Apply"/>：
///   Apply = 校验（机理约束）→ 快照 → 写入 Committed → 通知引擎重读配置。
/// 校验有 Block 级违规时直接拒绝，并把原因原样返回给 UI 显示。
/// </summary>
public sealed class ParameterCenter
{
    private readonly object _gate = new();

    public ThermalConfig Committed { get; private set; }
    public ThermalConfig Draft { get; private set; }

    /// <summary>草稿发生变化（UI 需要刷新"待应用"计数）。</summary>
    public event Action? DraftChanged;
    /// <summary>草稿被应用到 Committed（引擎应重读配置）。</summary>
    public event Action<ThermalConfig>? Applied;

    /// <summary>用于机理预测的热阻估计（K/W）；NaN 表示未知，跳过稳态温度预测。</summary>
    public double REstimate { get; set; } = double.NaN;
    /// <summary>热阻估计是否可信（样本数与功率跨度够）。不可信时预测只作警告，不阻断。</summary>
    public bool RConfident { get; set; } = true;
    /// <summary>芯片实际最大睿频倍率（×100MHz）；0 表示未知。</summary>
    public int MaxTurboRatio { get; set; }

    public ParameterCenter(ThermalConfig initial)
    {
        Committed = (initial ?? new ThermalConfig()).Clone();
        ControlChannels.Normalize(Committed);
        // 平台能力只影响"这次能不能下发"，不改用户存的那份配置
        ControlChannels.ApplyCapabilityLimits(Committed);
        Draft = Committed.Clone();
    }

    // ---------------- 草稿编辑 ----------------

    /// <summary>
    /// 以「改一份草稿」的方式编辑，改完自动触发 DraftChanged。
    /// 所有面板都通过它改参数，保证只有一个写入点。
    /// </summary>
    public void Edit(Action<ThermalConfig> mutate, bool normalize = true)
    {
        lock (_gate)
        {
            mutate(Draft);
            if (normalize) ControlChannels.Normalize(Draft);
        }
        DraftChanged?.Invoke();
    }

    /// <summary>丢弃草稿，回到当前已应用的值。</summary>
    public void Discard()
    {
        lock (_gate) Draft = Committed.Clone();
        DraftChanged?.Invoke();
    }

    /// <summary>
    /// 手调即让位：用户手改了 PL1/PL2/τ/EPP（任一）且值确实与已应用不同，
    /// 智能自动必须交出控制权 —— 否则控制器每 3 秒重算会把手调值顶掉，
    /// 表面"已应用"，几秒后又变回去。
    ///
    /// 让位 = AutoCpuMode 关 + EnableCpuTuning 开（手动值必须有通道写进硬件）。
    /// 「是不是手改的」由调用方判定：预设套用 / 读取当前值改到这四项不算手调，不让位。
    /// </summary>
    public bool YieldAutoForManualCpuEdit(bool manualCpuEdit)
    {
        if (!manualCpuEdit) return false;

        ThermalConfig d, c;
        lock (_gate) { d = Draft; c = Committed; }
        if (!d.AutoCpuMode) return false;
        if (d.CpuPl1W == c.CpuPl1W && d.CpuPl2W == c.CpuPl2W
            && d.CpuTauS == c.CpuTauS && d.CpuEpp == c.CpuEpp) return false;

        Edit(x => { x.AutoCpuMode = false; x.EnableCpuTuning = true; });
        return true;
    }

    // ---------------- 差异 / 校验 ----------------

    private static readonly (string Key, ParamTier Tier, string Label, Func<ThermalConfig, string> Get)[] Tracked =
    {
        ("Profile",  ParamTier.Global,   "取向",         c => c.ProfileName),
        ("Channel",  ParamTier.Global,   "控制通道",      c => ControlChannels.Label(c.ControlChannel)),
        ("Tceiling", ParamTier.Global,   "温度上限",      c => $"{c.AutoTargetC:0.#}℃"),
        ("Tambient", ParamTier.Global,   "室温",         c => $"{c.AmbientC:0.#}℃"),
        ("FanMax",   ParamTier.Global,   "风扇上限",      c => $"{c.FanMaxPct:0}%"),
        ("FanMin",   ParamTier.Advanced, "风扇下限",      c => $"{c.FanMinPct:0}%"),
        ("Auto",     ParamTier.Global,   "智能自动",      c => c.AutoCpuMode ? "开" : "关"),
        ("DryRun",   ParamTier.Advanced, "预览模式",      c => c.AutoCpuDryRun ? "开（不写 MSR）" : "关"),
        // 平台功率也要进 Diff：改了「设计功耗」会改变 PL1/PL2 的允许区间，属于真实参数变更，
        // 不进 Diff 的话「待应用」条会说"没有变更"，用户点了应用也不知道到底动没动。
        ("CpuDesign", ParamTier.Advanced, "CPU 设计功耗", c => c.CpuDesignW > 0 ? $"{c.CpuDesignW:0}W" : "自动"),
        ("GpuDesign", ParamTier.Advanced, "独显设计功耗", c => c.GpuDesignW > 0 ? $"{c.GpuDesignW:0}W" : "自动"),
        ("Pl1",      ParamTier.Advanced, "PL1",         c => c.CpuPl1W > 0 ? $"{c.CpuPl1W}W" : "不改"),
        ("Pl2",      ParamTier.Advanced, "PL2",         c => c.CpuPl2W > 0 ? $"{c.CpuPl2W}W" : "不改"),
        ("Tau",      ParamTier.Advanced, "τ",           c => c.CpuTauS > 0 ? $"{c.CpuTauS}s" : "不改"),
        ("Epp",      ParamTier.Advanced, "EPP",         c => c.CpuEpp >= 0 ? c.CpuEpp.ToString() : "不改"),
        ("Turbo",    ParamTier.Advanced, "睿频上限",      c => c.CpuTurboRatio > 0 ? $"{c.CpuTurboRatio}×100MHz" : "不改"),
        ("Tune",     ParamTier.Advanced, "手工 CPU 调控",  c => c.EnableCpuTuning ? "开" : "关"),
        ("Uv",       ParamTier.Advanced, "降压",         c => c.EnableUndervolt ? $"{c.UndervoltMv}mV" : "关"),
        ("FanCtl",   ParamTier.Advanced, "风扇调速",      c => c.EnableFanControl ? "开" : "关"),
        ("Critical", ParamTier.Advanced, "安全线",        c => $"{c.CriticalTempC:0}℃"),
        ("Policy",   ParamTier.Advanced, "实测策略模型",   c => c.UsePolicyModel ? "开" : "关"),
        ("Log",      ParamTier.Advanced, "采样日志",      c => c.EnableLogging ? "开" : "关"),
    };

    public IReadOnlyList<ParamChange> Diff()
    {
        ThermalConfig a, b;
        lock (_gate) { a = Committed; b = Draft; }

        var list = new List<ParamChange>();
        foreach (var t in Tracked)
        {
            string from = t.Get(a), to = t.Get(b);
            if (!string.Equals(from, to, StringComparison.Ordinal))
                list.Add(new ParamChange { Key = t.Key, Label = t.Label, Tier = t.Tier, From = from, To = to });
        }
        return list;
    }

    public int PendingCount => Diff().Count;
    public bool HasPending => PendingCount > 0;

    public IReadOnlyList<ParamViolation> Validate()
    {
        ThermalConfig d;
        lock (_gate) d = Draft;
        return ParamConstraints.Validate(d, REstimate, MaxTurboRatio, CpuCapabilities.Current, RConfident);
    }

    // ---------------- 应用 ----------------

    /// <summary>
    /// 应用草稿。三步：校验 → 提交 → 通知。
    /// 有 Block 级违规时**不提交、不写硬件**，把违规原样返回。
    /// </summary>
    public ApplyOutcome Apply()
    {
        ThermalConfig d;
        lock (_gate) d = Draft;

        var violations = ParamConstraints.Validate(d, REstimate, MaxTurboRatio, CpuCapabilities.Current, RConfident);
        var blocks = violations.Where(x => x.Severity == ParamSeverity.Block).ToList();
        if (blocks.Count > 0)
        {
            return new ApplyOutcome
            {
                Ok = false,
                Violations = violations,
                Message = "已拒绝应用：" + string.Join("；", blocks.Select(x => $"{x.Label} {x.Message}"))
            };
        }

        int n;
        ThermalConfig snapshot;
        lock (_gate)
        {
            n = 0;
            foreach (var t in Tracked)
                if (!string.Equals(t.Get(Committed), t.Get(Draft), StringComparison.Ordinal)) n++;
            Committed = Draft.Clone();
            ControlChannels.Normalize(Committed);
            ControlChannels.ApplyCapabilityLimits(Committed);
            Draft = Committed.Clone();
            snapshot = Committed.Clone();
        }

        DraftChanged?.Invoke();
        Applied?.Invoke(snapshot);

        return new ApplyOutcome
        {
            Ok = true,
            AppliedCount = n,
            Violations = violations,
            Message = n == 0 ? "没有变更（草稿与已应用一致）" : $"已应用 {n} 项变更"
        };
    }

    /// <summary>不经过草稿，直接把一份配置设为已应用（用于「还原默认」「载入预设」）。</summary>
    public void ForceCommit(ThermalConfig cfg)
    {
        lock (_gate)
        {
            Committed = (cfg ?? new ThermalConfig()).Clone();
            ControlChannels.Normalize(Committed);
            ControlChannels.ApplyCapabilityLimits(Committed);
            Draft = Committed.Clone();
        }
        DraftChanged?.Invoke();
        Applied?.Invoke(Committed.Clone());
    }

    // ---------------- 自检 ----------------

    public static List<string> SelfTest()
    {
        var errors = new List<string>();
        var c = new ThermalConfig
        {
            AutoCpuMode = true, CpuPl1W = 30, CpuPl2W = 38, AutoTargetC = 88, AmbientC = 24,
            FanMaxPct = 75, CriticalTempC = 95
        };
        var pc = new ParameterCenter(c) { REstimate = 1.8 };

        // A) 初始无待应用
        if (pc.HasPending) errors.Add("新建参数中心不应有待应用变更");

        // B) 改一处 → 恰好 1 项待应用
        pc.Edit(x => x.AutoTargetC = 90);
        if (pc.PendingCount != 1) errors.Add($"改动 1 项后待应用为 {pc.PendingCount}，应为 1");

        // C) 改回原值 → 待应用归零（Diff 基于值，不是基于"点过没点过"）
        pc.Edit(x => x.AutoTargetC = 88);
        if (pc.HasPending) errors.Add("改回原值后仍有待应用变更");

        // D) Block 级违规必须拒绝应用，且 Committed 不变
        pc.Edit(x => x.AutoTargetC = 99);
        var r = pc.Apply();
        if (r.Ok) errors.Add("温度上限 99℃ 竟然应用成功");
        if (Math.Abs(pc.Committed.AutoTargetC - 88) > 0.01) errors.Add("被拒绝后 Committed 被污染");

        // E) 合法改动必须应用成功，并且 Draft 与 Committed 同步
        pc.Discard();
        pc.Edit(x => x.CpuPl1W = 32);
        var r2 = pc.Apply();
        if (!r2.Ok) errors.Add("合法改动应用失败：" + r2.Message);
        if (pc.HasPending) errors.Add("应用后仍有待应用变更");

        // F) 丢弃草稿要真的回退
        pc.Edit(x => x.AmbientC = 35);
        pc.Discard();
        if (Math.Abs(pc.Draft.AmbientC - 24) > 0.01) errors.Add("Discard 未回退草稿");

        // G) 通道互斥：切到 powercfg 必须关掉 MSR 通道
        pc.Edit(x => x.ControlChannel = "powercfg");
        if (pc.Draft.AutoCpuMode) errors.Add("powercfg 通道下 AutoCpuMode 仍为 true（两套控制器会打架）");
        if (pc.Draft.EnableCpuTuning) errors.Add("powercfg 通道下 EnableCpuTuning 仍为 true");
        pc.Edit(x => x.ControlChannel = "msr");
        if (pc.Draft.EnableFrequencyControl) errors.Add("msr 通道下 EnableFrequencyControl 仍为 true");

        // H) 手调即让位：手动改 PL1/PL2/τ/EPP 时智能自动必须关掉、手工调控必须打开，
        //    否则控制器每 3 秒重算会把手调值顶掉（"应用了又被改回去"）。
        pc.Edit(x => { x.AutoCpuMode = true; x.EnableCpuTuning = false; });
        pc.Edit(x => x.CpuPl1W = 35);
        if (!pc.YieldAutoForManualCpuEdit(manualCpuEdit: true)) errors.Add("手调 PL1 竟然没让位");
        if (pc.Draft.AutoCpuMode) errors.Add("让位后 AutoCpuMode 仍为 true");
        if (!pc.Draft.EnableCpuTuning) errors.Add("让位后 EnableCpuTuning 未打开（手动值没通道写硬件）");
        // 预设改到这四项不算手调：不让位
        pc.Discard();
        pc.Edit(x => x.CpuPl2W = 42);
        if (pc.YieldAutoForManualCpuEdit(manualCpuEdit: false)) errors.Add("非手调也让位了");
        if (!pc.Draft.AutoCpuMode) errors.Add("非手调却把 AutoCpuMode 关了");

        return errors;
    }
}

/// <summary>
/// 取向预设：把「取向」这一个全局变量，一次性展开成一组派生参数。
///
/// 这是"预设"和"手改"的正确关系：
///   · 选取向 = 套用一组推荐值（写进草稿，用户能看见每一项变了什么）
///   · 之后手改任何一项 = 覆盖它，不会再被重新套用
/// 旧版的问题是"取向"在三个地方各有一份，选了也不告诉你派生了什么。
///
/// 语义再次强调：温度是**上限（ceiling）**不是 setpoint。性能档之所以上限最高（90℃），
/// 是因为它风扇最猛（R2 最小），只有上限高 + 满转速，功率预算 (上限−室温)/R 才最大。
/// </summary>
public static class ProfilePreset
{
    public static readonly string[] All = { "静音", "均衡", "性能" };

    public static string Normalize(string? name) => name switch
    {
        "静音" or "silent" => "静音",
        "性能" or "performance" => "性能",
        _ => "均衡"
    };

    public static string AutoProfile(string? name) => Normalize(name) switch
    {
        "静音" => "silent",
        "性能" => "performance",
        _ => "balanced"
    };

    /// <summary>取向 → 各项推荐值（温度上限 / 风扇上限 / EPP / PL2 倍率 / τ）。</summary>
    public static (double TceilingC, double FanMaxPct, int Epp, double Pl2Ratio, int TauS) Values(string? name)
        => Normalize(name) switch
        {
            "静音" => (88, 55, 200, 1.15, 8),
            "性能" => (90, 100, 0, 1.35, 32),
            _ => (88, 75, 128, 1.25, 16)
        };

    /// <summary>把取向套用到配置上（只改派生值，不动 PL1 这类用户自己的主控量）。</summary>
    public static void Apply(ThermalConfig c, string? name)
    {
        if (c is null) return;
        string n = Normalize(name);
        var v = Values(n);
        // 风机的"性格"也属于预设：静音降速慢、回差大，性能降速快、回差小。
        // 不在这里一起写，用户点「回到预设」后 Hyst/斜率 还停在旧值，
        // 配置档会立刻显示成一个消不掉的「自定义」。
        var character = Presets.Create(n);

        c.ProfileName = n;
        c.AutoCpuProfile = AutoProfile(n);
        c.FanCurve = Presets.Fan(n);
        c.FreqCurve = Presets.Freq(n);

        c.FanHysteresisC = character.FanHysteresisC;
        c.FanRampUpPctPerSec = character.FanRampUpPctPerSec;
        c.FanRampDownPctPerSec = character.FanRampDownPctPerSec;
        c.FanMinPct = character.FanMinPct;
        c.TargetTempC = character.TargetTempC;

        c.AutoTargetC = v.TceilingC;
        c.FanMaxPct = v.FanMaxPct;
        c.CpuEpp = v.Epp;
        c.CpuTauS = v.TauS;
        if (c.CpuPl1W > 0)
            c.CpuPl2W = (int)Math.Round(c.CpuPl1W * v.Pl2Ratio);
    }

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 预设套用后必须自洽（不产生阻断级违规）
        foreach (var n in All)
        {
            var c = new ThermalConfig { AutoCpuMode = true };
            Apply(c, n);
            var v = ParamConstraints.Validate(c, 1.8);
            if (ParamConstraints.HasBlocking(v))
                errors.Add($"预设「{n}」自带阻断级违规：" + string.Join("；", v.Where(x => x.Severity == ParamSeverity.Block)));
        }

        // B) 功率预算必须随档位单调递增（这才是"性能档真的更快"的机械保证）
        double Prev(double t, double fan) => (t - 24) / (0.3 + 0.48 - 0.18 * (fan / 100.0));
        double ps = 0, pb = 0, pp = 0;
        foreach (var n in All)
        {
            var v = Values(n);
            double budget = Prev(v.TceilingC, v.FanMaxPct);
            if (n == "静音") ps = budget;
            else if (n == "均衡") pb = budget;
            else pp = budget;
        }
        if (!(ps < pb && pb < pp))
            errors.Add($"档位功率预算非单调递增：静音 {ps:0.0} / 均衡 {pb:0.0} / 性能 {pp:0.0}");

        // C) 取向名归一化不能丢档
        if (Normalize("performance") != "性能") errors.Add("performance 未归一化到「性能」");
        if (Normalize(null) != "均衡") errors.Add("null 未兜底到「均衡」");

        return errors;
    }
}

/// <summary>
/// 控制通道互斥策略。
///
/// 背景：旧版同时有三套东西在写 CPU 频率/功率 ——
///   ① Windows 电源计划 PROCTHROTTLEMAX（EnableFrequencyControl）
///   ② MSR 精细调控 PL1/PL2/EPP（EnableCpuTuning / AutoCpuMode）
///   ③ 实测策略模型查表（UsePolicyModel）
/// 三套各算各的，谁最后写谁生效，于是出现"温度升了、功率升了、频率反而降了"。
/// 现在强制互斥：同一时刻只有一个通道拥有 CPU 频率的控制权。
/// </summary>
public static class ControlChannels
{
    public const string Msr = "msr";
    public const string PowerCfg = "powercfg";
    public const string Policy = "policy";

    public static string Label(string? ch) => (ch ?? Msr).ToLowerInvariant() switch
    {
        PowerCfg => "电源计划限频",
        Policy => "实测策略模型",
        _ => "MSR 精细调控"
    };

    /// <summary>把配置归一化到"只有一个 CPU 控制通道"的状态。</summary>
    public static void Normalize(ThermalConfig c)
    {
        if (c is null) return;
        string ch = (c.ControlChannel ?? "").ToLowerInvariant();
        if (ch is not (Msr or PowerCfg or Policy)) ch = Msr;
        c.ControlChannel = ch;

        switch (ch)
        {
            case PowerCfg:
                c.EnableFrequencyControl = true;
                c.EnableCpuTuning = false;
                c.AutoCpuMode = false;
                c.UsePolicyModel = false;
                break;
            case Policy:
                c.EnableFrequencyControl = false;
                c.EnableCpuTuning = false;
                c.AutoCpuMode = false;
                c.UsePolicyModel = true;
                break;
            default: // msr
                c.EnableFrequencyControl = false;
                c.UsePolicyModel = false;
                // 自动 / 手工二选一由 UI 保证，这里允许 AutoCpuMode 与 EnableCpuTuning 互斥
                if (c.AutoCpuMode) c.EnableCpuTuning = false;
                break;
        }
    }

    /// <summary>
    /// 平台能力清洗：本机根本没有的旋钮，值清掉、通道退回电源计划。
    ///
    /// **不能放进 <see cref="Normalize"/>**：归一化会被"从配置档重建基线"这种纯数据路径复用，
    /// 那时机器上还没探测过能力（甚至是在为另一台机器准备配置），把运行时能力混进去，
    /// 会把用户的 EPP/τ 当成"非法值"直接抹掉并落盘 —— v1.9 开发时就踩过这个坑。
    /// 所以清洗只发生在"即将写硬件"的边界：构造、应用、强制提交、引擎启动。
    /// 只清 Unsupported，不动 NeedsPrerequisite（提权后还想生效）。
    /// </summary>
    public static void ApplyCapabilityLimits(ThermalConfig c) => CpuCapabilities.Current.Sanitize(c);
}
