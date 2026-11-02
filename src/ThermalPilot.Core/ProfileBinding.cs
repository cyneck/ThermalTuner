using System;
using System.Collections.Generic;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>一条「相对预设被手改过」的差异项。</summary>
public sealed class ProfileOverride
{
    public string Label { get; init; } = "";
    public string Preset { get; init; } = "";
    public string Actual { get; init; } = "";

    public override string ToString() => $"{Label} {Preset}→{Actual}";
}

/// <summary>
/// 取向绑定：「基础预设 + 用户自定义增量」。
///
/// 设计意图（回答「静音-自定义 是否是这样的效果」）：
///   · 用户选「静音」= 把预设值一次性写进配置，ProfileName 记成「静音」；
///   · 之后用户在策略页 / 高级页手改任何一项 = 在「静音」之上叠**增量**；
///   · 显示名于是变成「静音 · 自定义」，鼠标悬浮能看到具体哪几项被改、原值是多少。
///
/// 关键实现选择：**不用一个 sticky 布尔标记**，而是每次按「当前值 vs 预设值」现算。
/// 好处：
///   1. 手改后又被改回预设值 → 自动退回「静音」，不会留下假的"自定义"标签；
///   2. 增量是**隐含**在配置里的，不需要额外的持久化字段，重启后照样认得出；
///   3. 「重新套用预设」= 清空增量，语义与显示完全一致。
///
/// 这与「参数中心」的三层模型是配套的：
///   Global（取向）给出基线 → Advanced（手改）给出增量 → 显示层合成「基线 · 自定义」。
/// </summary>
public static class ProfileBinding
{
    public const string Suffix = " · 自定义";

    /// <summary>基础取向（不含自定义后缀）。</summary>
    public static string Base(ThermalConfig? c) => ProfilePreset.Normalize(c?.ProfileName);

    /// <summary>与预设不同的项；空 = 完全是纯预设。</summary>
    public static IReadOnlyList<ProfileOverride> Overrides(ThermalConfig? c)
    {
        if (c == null) return Array.Empty<ProfileOverride>();

        // 唯一事实来源：ProfileInstance.Rows。
        // 旧版这里另写了一套"比哪几项"的逻辑，结果顶栏说「纯预设」、
        // 配置档表格说「3 项自定义」—— 同一份配置两个结论。现在只保留一条判定路径。
        return ProfileInstance.Rows(c)
            .Where(r => r.IsOverride)
            .Select(r => new ProfileOverride { Label = r.Label, Preset = r.BaseText, Actual = r.ValueText })
            .ToList();
    }

    public static bool IsCustom(ThermalConfig? c) => Overrides(c).Count > 0;

    /// <summary>显示名：纯预设是「静音」，手改过是「静音 · 自定义」。</summary>
    public static string DisplayName(ThermalConfig? c) => Base(c) + (IsCustom(c) ? Suffix : "");

    /// <summary>精简摘要，适合放在一行状态里。</summary>
    public static string Short(ThermalConfig? c)
    {
        var o = Overrides(c);
        return o.Count == 0 ? "纯预设" : $"{o.Count} 项自定义：" + string.Join("、", o.Select(x => x.Label));
    }

    /// <summary>多行明细，给 ToolTip 用。</summary>
    public static string Describe(ThermalConfig? c)
    {
        string name = Base(c);
        var o = Overrides(c);
        string autoNote = c?.AutoCpuMode == true
            ? Environment.NewLine + "（智能自动模式下 PL1 / PL2 / τ / EPP 由控制器每 3 秒重算并下发，高级页手填的这几项不参与下发，配置档表格里标为「自动接管」，不计入自定义）"
            : "";

        if (o.Count == 0)
            return $"「{name}」纯预设：温度上限 / 风扇上限 / EPP / τ / PL2 / 曲线 全部与预设一致。" + autoNote;

        return $"基础预设「{name}」，其中 {o.Count} 项被你改过：" + Environment.NewLine
             + string.Join(Environment.NewLine, o.Select(x => $"· {x.Label}：预设 {x.Preset} → 现在 {x.Actual}"))
             + Environment.NewLine + "点「回到预设」可清空这些增量。" + autoNote;
    }

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 纯预设 → 不显示自定义
        var c = new ThermalConfig();
        ProfilePreset.Apply(c, "静音");
        if (IsCustom(c)) errors.Add("刚套用「静音」就报自定义：" + Short(c));
        if (DisplayName(c) != "静音") errors.Add($"纯预设显示名为「{DisplayName(c)}」，应为「静音」");

        // A2) 手动模式下自己填了 PL1（预设里是"不改"）→ 必须算自定义，
        //     否则用户会以为"我设的 30W 没被记住"。
        var p1 = new ThermalConfig { CpuPl1W = 30 };
        ProfilePreset.Apply(p1, "静音");
        if (!IsCustom(p1)) errors.Add("手动模式下 PL1=30W 没被算成自定义");

        // B) 手改一项 → 恰好 1 项增量，显示名带后缀
        c.AutoTargetC = 84;
        var o = Overrides(c);
        if (o.Count != 1) errors.Add($"只改了温度上限却报 {o.Count} 项增量");
        if (DisplayName(c) != "静音" + Suffix) errors.Add("手改后显示名未带自定义后缀");
        if (o.Count == 1 && o[0].Label != "温度上限") errors.Add("增量项标签错了：" + o[0].Label);

        // C) 改回预设值 → 自动退回纯预设（不靠 sticky 标记）
        c.AutoTargetC = 88;
        if (IsCustom(c)) errors.Add("改回预设值后仍报自定义（sticky 标记没清掉）");

        // D) 换基础取向后重新判定：同一个 90℃ 在静音档算增量、在性能档算贴合
        c.AutoTargetC = 90;                       // 静音预设 88 → 是增量
        if (!Overrides(c).Any(x => x.Label == "温度上限"))
            errors.Add("静音档把上限改成 90℃ 却没报增量");
        c.ProfileName = "性能";                   // 性能档预设就是 90℃ → 不再是增量
        if (Overrides(c).Any(x => x.Label == "温度上限"))
            errors.Add("切到性能档后 90℃ 仍被当成增量（比较基准没跟着取向走）");

        // E) 明细文案要能说出「预设多少 → 现在多少」
        c.AutoTargetC = 92;
        string desc = Describe(c);
        if (!desc.Contains("90") || !desc.Contains("92"))
            errors.Add("明细文案缺少预设值或当前值：" + desc.Replace(Environment.NewLine, " / "));

        // F) 智能自动模式：手填的 EPP/τ/PL2 不参与下发，就不该算自定义
        var a = new ThermalConfig { AutoCpuMode = true };
        ProfilePreset.Apply(a, "静音");
        a.CpuEpp = 128; a.CpuTauS = 32; a.CpuPl1W = 40; a.CpuPl2W = 60;
        if (Overrides(a).Any(x => x.Label is "EPP" or "τ" or "PL2 倍率"))
            errors.Add("自动模式下把不生效的 EPP/τ/PL2 算进了自定义");
        a.AutoTargetC = 84;               // 这个是真的生效，必须报
        if (!IsCustom(a)) errors.Add("自动模式下改了温度上限却没报自定义");

        return errors;
    }
}
