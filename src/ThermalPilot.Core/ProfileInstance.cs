using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>配置档里的一个"受管参数"定义（可被用户自定义的那一层）。</summary>
public sealed class ProfileParamDef
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public ParamTier Tier { get; init; }
    public Func<ThermalConfig, double> Get { get; init; } = _ => 0;
    public Action<ThermalConfig, double> Set { get; init; } = (_, _) => { };
    /// <summary>机理约束（直接给用户看，不是产品偏好）。</summary>
    public string Bound { get; init; } = "";
    public string Format { get; init; } = "0.#";
    /// <summary>0 表示"不改"（PL1 / PL2 / τ / 睿频倍率）。</summary>
    public bool ZeroMeansUnset { get; init; }
    /// <summary>特殊文本（例如 EPP 的 -1 表示"不改"）。给了就用它，否则按 Format + Unit。</summary>
    public Func<double, string>? FormatText { get; init; }
    /// <summary>
    /// 智能自动模式下由控制器实时接管（PL1/PL2/τ/EPP 每 3 秒都被重算并下发），
    /// 此时用户填的值不参与下发 —— 表格里要标「自动接管」，也不算成自定义。
    /// </summary>
    public bool AutoOverridden { get; init; }

    /// <summary>写成文本：0 = 不改 / 否则 数值 + 单位。</summary>
    public string Text(double v)
    {
        if (FormatText != null) return FormatText(v);
        if (ZeroMeansUnset && Math.Abs(v) < 1e-9) return "不改";
        return v.ToString(Format, CultureInfo.InvariantCulture) + Unit;
    }
}

/// <summary>配置档表格的一行（界面直接绑定）。</summary>
public sealed class ProfileParamRow
{
    public string Label { get; init; } = "";
    public string Source { get; init; } = "";
    public string BaseText { get; init; } = "";
    public string ValueText { get; init; } = "";
    public string BoundText { get; init; } = "";
    /// <summary>与内置基线不同（= 用户的增量）。</summary>
    public bool IsOverride { get; init; }
    /// <summary>模型/硬件给出的基线，用户没有直接修改的权限。</summary>
    public bool IsLocked { get; init; }
    /// <summary>智能自动模式下由控制器接管，当前不使用这里填的值。</summary>
    public bool IsInactive { get; init; }
}

/// <summary>
/// 配置档 = 内置基线（模型给定，只读）+ 用户增量（高级参数的手改，持久化）。
///
/// 这是对「静音-自定义 到底是什么」的回答：
///   · 它**不是**一个新的预设槽位，基线仍然是内置的「静音」；
///   · 它是一份**实例**：Base 记住基线是谁，Delta 记住你改过哪几项、改成多少；
///   · 启动时先用当前版本的内置基线重建，再叠加 Delta —— 所以程序升级后，
///     没被你改过的参数会自动跟随新的基线（比如模型把 PL2 倍率从 1.15 修到 1.20），
///     被你改过的参数仍然保持你的值。这恰恰是"基线 + 增量"相对"整份拷贝"的唯一好处，
///     否则一旦存成整份配置，基线永远升不上去。
///   · 增量与基线一起落盘（settings.json → Profile），重启/换机器口径一致。
///
/// 反过来，**模型自己算出来的量**（安全线 95℃、TJmax 100℃、τ 的 log2 可表示域、
/// PL2/PL1 上限、芯片最大睿频、在线估计的热阻 R）不进入 Delta：它们不是"策略选择"，
/// 而是物理约束，用户没有直接改的权限，表格里以「锁定」呈现。
/// </summary>
public sealed class ProfileInstance
{
    public const int SchemaVersion = 1;

    /// <summary>基线取向名（静音 / 均衡 / 性能）。</summary>
    public string Base { get; set; } = "均衡";
    /// <summary>结构版本，便于将来迁移。</summary>
    public int Schema { get; set; } = SchemaVersion;
    /// <summary>最后一次保存时间。</summary>
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.Now;

    /// <summary>数值型增量：只存"与基线不同"的项，键见 <see cref="ProfileParams.Managed"/>。</summary>
    public Dictionary<string, double> Delta { get; set; } = new();

    /// <summary>曲线增量（键：Fan / Freq），值为紧凑串 "温度:值;..."。</summary>
    public Dictionary<string, string> Curves { get; set; } = new();

    /// <summary>保存时的基线快照（仅供诊断/比对，不参与重建）。</summary>
    public Dictionary<string, double> Baseline { get; set; } = new();

    public int Count => Delta.Count + Curves.Count;
    public bool IsCustom => Count > 0;

    public string DisplayName
    {
        get
        {
            string b = ProfilePreset.Normalize(Base);
            return b + (IsCustom ? ProfileBinding.Suffix : "");
        }
    }

    // ---------------- 采集 / 重建 ----------------

    /// <summary>从一份完整配置里抽出「基线 + 增量」。</summary>
    public static ProfileInstance Capture(ThermalConfig cfg)
    {
        var inst = new ProfileInstance { Base = ProfilePreset.Normalize(cfg?.ProfileName), SavedUtc = DateTimeOffset.Now };
        if (cfg == null) return inst;

        var b = ProfileBaseline.Build(inst.Base);
        foreach (var d in ProfileParams.Managed)
        {
            double baseVal = d.Get(b), cur = d.Get(cfg);
            inst.Baseline[d.Key] = Math.Round(baseVal, 2);
            if (IsInactive(d, cfg)) continue;                 // 自动接管项不进增量
            if (Math.Abs(cur - baseVal) > 0.01) inst.Delta[d.Key] = Math.Round(cur, 2);
        }

        string fan = CurveText(cfg.FanCurve), fanBase = CurveText(b.FanCurve);
        if (!string.Equals(fan, fanBase, StringComparison.Ordinal)) inst.Curves["Fan"] = fan;
        string freq = CurveText(cfg.FreqCurve), freqBase = CurveText(b.FreqCurve);
        if (!string.Equals(freq, freqBase, StringComparison.Ordinal)) inst.Curves["Freq"] = freq;

        return inst;
    }

    /// <summary>把「基线 + 增量」写回一份配置。只覆盖受管参数，其它字段（通道、日志、降压开关…）不动。</summary>
    public void ApplyTo(ThermalConfig cfg)
    {
        if (cfg == null) return;

        string b = ProfilePreset.Normalize(Base);
        var baseCfg = ProfileBaseline.Build(b);

        cfg.ProfileName = b;
        cfg.AutoCpuProfile = ProfilePreset.AutoProfile(b);
        cfg.FanCurve = baseCfg.FanCurve.Clone();
        cfg.FreqCurve = baseCfg.FreqCurve.Clone();

        foreach (var d in ProfileParams.Managed)
        {
            d.Set(cfg, d.Get(baseCfg));
            if (Delta.TryGetValue(d.Key, out double v) && !double.IsNaN(v)) d.Set(cfg, v);
        }

        foreach (var (k, text) in Curves)
        {
            var c = ParseCurve(text);
            if (c == null || c.Points.Count < 2) continue;
            if (string.Equals(k, "Fan", StringComparison.OrdinalIgnoreCase)) cfg.FanCurve = c;
            else if (string.Equals(k, "Freq", StringComparison.OrdinalIgnoreCase)) cfg.FreqCurve = c;
        }

        cfg.FanCurve.Normalize();
        cfg.FreqCurve.Normalize();
        ControlChannels.Normalize(cfg);
    }

    /// <summary>把受管参数恢复成内置基线（清空增量），返回清掉的项数。</summary>
    public static int ResetToBaseline(ThermalConfig cfg, string? profileName = null)
    {
        if (cfg == null) return 0;
        string name = ProfilePreset.Normalize(profileName ?? cfg.ProfileName);
        var b = ProfileBaseline.Build(name);

        cfg.ProfileName = name;
        cfg.AutoCpuProfile = ProfilePreset.AutoProfile(name);
        foreach (var d in ProfileParams.Managed) d.Set(cfg, d.Get(b));
        cfg.FanCurve = b.FanCurve.Clone();
        cfg.FreqCurve = b.FreqCurve.Clone();
        ControlChannels.Normalize(cfg);
        return 0;
    }

    /// <summary>
    /// 启动时的对账：
    ///   · 老版本没有 Profile 字段 → 从当前配置里抽一份（保留用户已调好的值）；
    ///   · 有 Profile → 用当前版本的内置基线重建 + 叠加增量（基线可升级）。
    /// </summary>
    public static ProfileInstance Reconcile(ProfileInstance? saved, ThermalConfig cfg, out bool changed)
    {
        changed = false;
        if (cfg == null) return saved ?? new ProfileInstance();

        if (saved == null)
        {
            changed = true;
            return Capture(cfg);
        }

        string b = ProfilePreset.Normalize(saved.Base);
        string before = Fingerprint(cfg);
        bool schemaOld = saved.Schema != SchemaVersion;
        saved.Schema = SchemaVersion;
        saved.ApplyTo(cfg);
        // 增量随基线刷新：基线里已经等于当前值的项，不再记成"自定义"
        var fresh = Capture(cfg);
        fresh.Base = b;
        changed = schemaOld || Fingerprint(cfg) != before;
        return fresh;
    }

    /// <summary>受管参数的指纹，用来判断"基线重建后配置是否真的变了"。</summary>
    private static string Fingerprint(ThermalConfig c) =>
        string.Join("|", ProfileParams.Managed.Select(d => d.Get(c).ToString("0.###", CultureInfo.InvariantCulture)))
        + "|" + CurveText(c.FanCurve) + "|" + CurveText(c.FreqCurve);

    // ---------------- 展示 ----------------

    /// <summary>
    /// 生成「基线 / 当前值 / 约束 / 来源」的完整表格。
    /// 这就是"哪些参数是启动的、数值多少、约束多少"的答案，而不是散落在各个面板里。
    /// </summary>
    public static IReadOnlyList<ProfileParamRow> Rows(ThermalConfig? cfg, double rEstimate = double.NaN, int chipTurbo = 0,
                                                      CpuCapabilities? caps = null)
    {
        var rows = new List<ProfileParamRow>();
        if (cfg == null) return rows;

        var baseCfg = ProfileBaseline.Build(cfg.ProfileName);

        foreach (var d in ProfileParams.Managed)
        {
            double baseVal = d.Get(baseCfg), cur = d.Get(cfg);
            bool inactive = IsInactive(d, cfg);
            bool ov = !inactive && Math.Abs(cur - baseVal) > 0.01;

            // 平台层面就没有的旋钮：显式标出来，并且不算成"自定义"。
            // 否则 AMD 机器上一份从 Intel 带过来的配置会显示 3 项自定义，用户改也改不掉。
            var cap = caps?.For(d.Key);
            bool unsupported = cap?.Hard == true;
            rows.Add(new ProfileParamRow
            {
                Label = d.Label,
                Source = unsupported ? "✗ 本机不支持" : (inactive ? "自动接管" : (ov ? "● 自定义" : "基线")),
                BaseText = d.Text(baseVal),
                ValueText = d.Text(cur),
                BoundText = unsupported ? d.Bound + "　｜　" + cap!.Detail : d.Bound,
                IsOverride = ov && !unsupported,
                IsInactive = inactive || unsupported
            });
        }

        // 曲线单独两行：点集相同才算"基线"
        rows.Add(CurveRow("风扇曲线", cfg.FanCurve, baseCfg.FanCurve, "点数 ≥2，温度单调递增；线性插值"));
        rows.Add(CurveRow("降频曲线", cfg.FreqCurve, baseCfg.FreqCurve, "点数 ≥2，温度单调递增；线性插值"));

        // —— 锁定项：模型/硬件给的基线，用户没有直接修改权限 ——
        rows.Add(new ProfileParamRow
        {
            Label = "安全线（超温强制降档）",
            Source = "🔒 锁定",
            BaseText = $"{ParamConstraints.SafetyCeilingC:0}℃",
            ValueText = $"{cfg.CriticalTempC:0}℃",
            BoundText = "TJmax 100℃ − 5℃ 余量；只允许下调，不允许上调",
            IsLocked = true
        });
        rows.Add(new ProfileParamRow
        {
            Label = "TJmax（结温上限）",
            Source = "🔒 锁定",
            BaseText = $"{ParamConstraints.TjMaxC:0}℃",
            ValueText = $"{ParamConstraints.TjMaxC:0}℃",
            BoundText = "芯片规格值，任何参数都不得逼近",
            IsLocked = true
        });
        rows.Add(new ProfileParamRow
        {
            Label = "τ 可表示域",
            Source = "🔒 锁定",
            BaseText = "2 的幂",
            ValueText = cfg.CpuTauS > 0 ? $"{cfg.CpuTauS}s" : "不改",
            BoundText = "MSR 0x610 的 time 字段是 log2 编码，只有 1/2/4/8/16/32/64s 可精确表示",
            IsLocked = true
        });
        rows.Add(new ProfileParamRow
        {
            Label = "PL2/PL1 倍率上限",
            Source = "🔒 锁定",
            BaseText = $"{ParamConstraints.Pl2OverPl1Max:0.00}×",
            ValueText = cfg.CpuPl1W > 0 && cfg.CpuPl2W > 0
                ? $"{cfg.CpuPl2W / (double)cfg.CpuPl1W:0.00}×"
                : "—",
            BoundText = "Intel 建议 ≤1.25×；2.0× 是 τ 窗口内不失温的硬上限",
            IsLocked = true
        });
        rows.Add(new ProfileParamRow
        {
            Label = "睿频上限（芯片）",
            Source = "🔒 锁定",
            BaseText = chipTurbo > 0 ? $"{chipTurbo}×100MHz" : "待读取",
            ValueText = cfg.CpuTurboRatio > 0 ? $"{cfg.CpuTurboRatio}×100MHz" : "不改",
            BoundText = "0x1AD 芯片原值；超过会被硬件静默截断",
            IsLocked = true
        });
        rows.Add(new ProfileParamRow
        {
            Label = "热阻 R（在线估计）",
            Source = "🔒 锁定",
            BaseText = "测量值",
            ValueText = !double.IsNaN(rEstimate) && rEstimate > 0.05 ? $"{rEstimate:0.00} K/W" : "待估计",
            BoundText = "功率预算 = (温度上限 − 室温) / R；R 由程序在线估计，不可手填",
            IsLocked = true
        });

        return rows;
    }

    /// <summary>这一项当前是否被自动控制器接管（填了也不参与下发，因此不算自定义）。</summary>
    internal static bool IsInactive(ProfileParamDef d, ThermalConfig cfg) =>
        d.AutoOverridden && cfg.AutoCpuMode && string.Equals(cfg.ControlChannel, ControlChannels.Msr,
            StringComparison.OrdinalIgnoreCase);

    private static ProfileParamRow CurveRow(string label, Curve? cur, Curve? bas, string bound)    {
        string a = CurveText(cur), b = CurveText(bas);
        bool ov = !string.Equals(a, b, StringComparison.Ordinal);
        return new ProfileParamRow
        {
            Label = label,
            Source = ov ? "● 自定义" : "基线",
            BaseText = $"{bas?.Points.Count ?? 0} 点 · 预设",
            ValueText = ov ? $"{cur?.Points.Count ?? 0} 点 · 手改" : $"{cur?.Points.Count ?? 0} 点 · 预设",
            BoundText = bound,
            IsOverride = ov
        };
    }

    /// <summary>一行摘要：给顶栏 chip / 状态栏用。</summary>
    public string Summary() => IsCustom
        ? $"{DisplayName}（{Delta.Count} 项参数 + {Curves.Count} 条曲线）"
        : $"{DisplayName}（全部为内置基线）";

    /// <summary>多行明细：给悬浮提示用。</summary>
    public string Describe(ThermalConfig? cfg = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"配置档：{DisplayName}");
        sb.AppendLine($"内置基线：{ProfilePreset.Normalize(Base)}（由模型给定，不可直接改）");
        sb.AppendLine($"增量：{(IsCustom ? Count + " 项" : "无")}　保存于 {SavedUtc.LocalDateTime:yyyy-MM-dd HH:mm}");

        if (cfg != null)
        {
            var rows = Rows(cfg).Where(r => r.IsOverride).ToList();
            if (rows.Count > 0)
            {
                sb.AppendLine("被改过的项：");
                foreach (var r in rows) sb.AppendLine($"· {r.Label}：{r.BaseText} → {r.ValueText}");
            }
        }

        sb.Append("已持久化到 settings.json；重启后仍是这套值。");
        return sb.ToString();
    }

    // ---------------- 序列化辅助 ----------------

    internal static string CurveText(Curve? c) =>
        c == null ? "" : string.Join(";", c.Points.Select(p =>
            p.Temperature.ToString("0.#", CultureInfo.InvariantCulture) + ":" +
            p.Value.ToString("0.#", CultureInfo.InvariantCulture)));

    internal static Curve? ParseCurve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var c = new Curve();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) continue;
            if (!double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            c.Points.Add(new CurvePoint(t, v));
        }
        c.Normalize();
        return c;
    }

    // ---------------- 自检 ----------------

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 纯预设 → 无增量
        var c = new ThermalConfig();
        ProfilePreset.Apply(c, "静音");
        var inst = Capture(c);
        if (inst.IsCustom) errors.Add("纯「静音」预设被抽出了增量：" + string.Join(",", inst.Delta.Keys));
        if (inst.DisplayName != "静音") errors.Add("纯预设的配置档名应为「静音」，实际 " + inst.DisplayName);

        // B) 手改一项 → 恰好一条增量，且能被记录
        c.AutoTargetC = 84;
        inst = Capture(c);
        if (inst.Count != 1) errors.Add($"只改温度上限却记了 {inst.Count} 项增量");
        if (!inst.Delta.ContainsKey("Tceiling")) errors.Add("温度上限的增量没有落进 Delta");
        if (inst.DisplayName != "静音" + ProfileBinding.Suffix) errors.Add("增量配置档名未带自定义后缀：" + inst.DisplayName);

        // C) 重建：基线自动补齐，增量保留
        var rebuilt = new ThermalConfig();
        inst.ApplyTo(rebuilt);
        if (Math.Abs(rebuilt.AutoTargetC - 84) > 0.01) errors.Add("重建后温度上限没保留增量");
        if (Math.Abs(rebuilt.FanMaxPct - 55) > 0.01) errors.Add($"重建后风扇上限应回到「静音」基线 55%，实际 {rebuilt.FanMaxPct}");

        // D) 基线可升级：增量里没有的项，重建时跟随当前内置基线
        inst.Delta.Remove("Tceiling");
        var upgraded = new ThermalConfig { FanMaxPct = 12 };   // 脏值，重建时应被基线覆盖
        inst.ApplyTo(upgraded);
        if (Math.Abs(upgraded.FanMaxPct - 55) > 0.01) errors.Add("重建未用基线覆盖脏值");

        // E) 往返一致：受管参数全部对齐
        var src = new ThermalConfig();
        ProfilePreset.Apply(src, "性能");
        src.CpuPl1W = 32; src.CpuPl2W = 40; src.CpuTauS = 16; src.CpuEpp = 96;
        src.FanMinPct = 25; src.FanHysteresisC = 5; src.CpuTurboRatio = 33;
        src.FanCurve = new Curve { Points = { new CurvePoint(40, 0), new CurvePoint(70, 55), new CurvePoint(95, 100) } };
        var cap = Capture(src);
        var dst = new ThermalConfig();
        cap.ApplyTo(dst);
        foreach (var d in ProfileParams.Managed)
            if (Math.Abs(d.Get(src) - d.Get(dst)) > 0.01)
                errors.Add($"参数 {d.Label} 往返不一致：{d.Get(src)} → {d.Get(dst)}");
        if (CurveText(src.FanCurve) != CurveText(dst.FanCurve)) errors.Add("风扇曲线往返不一致");

        // F) 恢复基线：增量清空、值回到预设
        ResetToBaseline(dst, "性能");
        if (Math.Abs(dst.AutoTargetC - 90) > 0.01) errors.Add("恢复基线后温度上限不是性能档的 90℃");
        if (Math.Abs(dst.CpuPl1W) > 0.01) errors.Add("恢复基线后 PL1 没被清掉");

        // G) 表格：锁定项必须存在，改过的项必须标自定义
        var rows = Rows(src);
        if (!rows.Any(r => r.IsLocked)) errors.Add("配置档表格里没有锁定项（模型基线不可见）");
        if (!rows.Any(r => r.Label == "EPP 能效偏好" && r.IsOverride)) errors.Add("表格没标出被改过的 EPP");
        if (!rows.Any(r => r.Label == "PL1 持续功率" && r.IsOverride)) errors.Add("表格没标出被改过的 PL1");

        // H) 序列化往返：曲线串可解析回原点集
        var back = ParseCurve(CurveText(src.FanCurve));
        if (back == null || CurveText(back) != CurveText(src.FanCurve)) errors.Add("曲线序列化往返不一致");

        return errors;
    }
}

/// <summary>内置基线：某个取向在**当前版本**下的推荐值集合。升级程序后自动更新。</summary>
public static class ProfileBaseline
{
    public static ThermalConfig Build(string? name)
    {
        string n = ProfilePreset.Normalize(name);
        var b = Presets.Create(n);
        ProfilePreset.Apply(b, n);         // 温度上限 / 风扇上限 / EPP / τ / PL2 倍率 / 曲线
        b.FanMinPct = 20;
        b.CriticalTempC = ParamConstraints.SafetyCeilingC;
        return b;
    }
}

/// <summary>
/// 受管参数表：配置档里"用户可自定义"的那一层。
///
/// 只有列在这里的参数才会进入增量、才会被持久化、才会在升级时跟随基线。
/// 没列进来的（日志开关、降压开关、控制通道…）属于程序行为，不参与配置档语义。
/// </summary>
public static class ProfileParams
{
    public static readonly IReadOnlyList<ProfileParamDef> Managed = new List<ProfileParamDef>
    {
        new()
        {
            Key = "Tceiling", Label = "温度上限", Unit = "℃", Tier = ParamTier.Global,
            Get = c => c.AutoTargetC, Set = (c, v) => c.AutoTargetC = v,
            Bound = "70–95℃（TJmax 100 留 5℃）；它同时是功率预算的分子"
        },
        new()
        {
            Key = "FanMax", Label = "风扇上限", Unit = "%", Tier = ParamTier.Global,
            Get = c => c.FanMaxPct, Set = (c, v) => c.FanMaxPct = v,
            Bound = "30–100%；低于 30% 满载时 R2 急剧上升"
        },
        new()
        {
            Key = "FanMin", Label = "风扇下限", Unit = "%", Tier = ParamTier.Advanced,
            Get = c => c.FanMinPct, Set = (c, v) => c.FanMinPct = v,
            Bound = "0–100%，且必须 ≤ 风扇上限"
        },
        new()
        {
            Key = "Hyst", Label = "风扇回差", Unit = "℃", Tier = ParamTier.Advanced,
            Get = c => c.FanHysteresisC, Set = (c, v) => c.FanHysteresisC = v,
            Bound = "0.5–10℃；太小会在临界点来回抖"
        },
        new()
        {
            Key = "RampUp", Label = "升速斜率", Unit = "%/s", Tier = ParamTier.Advanced,
            Get = c => c.FanRampUpPctPerSec, Set = (c, v) => c.FanRampUpPctPerSec = v,
            Bound = "5–200 %/s"
        },
        new()
        {
            Key = "RampDown", Label = "降速斜率", Unit = "%/s", Tier = ParamTier.Advanced,
            Get = c => c.FanRampDownPctPerSec, Set = (c, v) => c.FanRampDownPctPerSec = v,
            Bound = "1–100 %/s；降快会有「直升机」感"
        },
        new()
        {
            Key = "Pl1", Label = "PL1 持续功率", Unit = "W", Tier = ParamTier.Advanced, ZeroMeansUnset = true,
            AutoOverridden = true,
            Get = c => c.CpuPl1W, Set = (c, v) => c.CpuPl1W = (int)Math.Round(v),
            Bound = $"5–{PlatformPower.CpuDesignW:F0}W（本机 CPU 设计功耗，{PlatformPower.Cpu.Badge}）；"
                  + "稳态温度 = 室温 + R·PL1，这就是温度的来源"
        },
        new()
        {
            Key = "Pl2", Label = "PL2 突增功率", Unit = "W", Tier = ParamTier.Advanced, ZeroMeansUnset = true,
            AutoOverridden = true,
            Get = c => c.CpuPl2W, Set = (c, v) => c.CpuPl2W = (int)Math.Round(v),
            Bound = "必须 ≥ PL1，且 ≤ 2×PL1"
        },
        new()
        {
            Key = "Tau", Label = "τ 突增时长", Unit = "s", Tier = ParamTier.Advanced, ZeroMeansUnset = true,
            AutoOverridden = true,
            Get = c => c.CpuTauS, Set = (c, v) => c.CpuTauS = (int)Math.Round(v),
            Bound = "1/2/4/8/16/32/64s（0x610 的 log2 编码）"
        },
        new()
        {
            Key = "Epp", Label = "EPP 能效偏好", Unit = "", Tier = ParamTier.Advanced,
            AutoOverridden = true,
            Get = c => c.CpuEpp, Set = (c, v) => c.CpuEpp = (int)Math.Round(v),
            Bound = "0 最积极升频 – 255 最省电（8 位字段）",
            FormatText = v => v < 0 ? "不改" : v.ToString("0", CultureInfo.InvariantCulture)
        },
        new()
        {
            Key = "Turbo", Label = "睿频倍率上限", Unit = "×100MHz", Tier = ParamTier.Advanced, ZeroMeansUnset = true,
            Get = c => c.CpuTurboRatio, Set = (c, v) => c.CpuTurboRatio = (int)Math.Round(v),
            Bound = "8–芯片最大睿频（0x1AD 原值）"
        }
    };
}
