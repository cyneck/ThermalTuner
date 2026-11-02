using System;
using System.Collections.Generic;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>
/// 参数分级。设计目标：让「全局参数 / 局部预览 / 最终应用」在代码层就能区分，
/// 而不是散落在各个面板的控件里 —— 同一个语义（比如"取向"）只允许有一处定义。
/// </summary>
public enum ParamTier
{
    /// <summary>全局参数：全程序唯一一份，只在「策略」页修改，持久化到配置文件。</summary>
    Global = 0,
    /// <summary>高级参数：同样会写进硬件，但属于"知道自己在干什么"才动的那一层，有硬约束。</summary>
    Advanced = 1,
    /// <summary>预览量：只读派生值（标定结果、自动控制决策、硬件现值），永远不参与 Diff。</summary>
    Preview = 2
}

public enum ParamSeverity
{
    /// <summary>阻断：违反机理约束，禁止应用（可能烧硬件或让寄存器进入未定义行为）。</summary>
    Block = 0,
    /// <summary>警告：允许应用，但会超出预期（例如会撞温度墙导致降频）。</summary>
    Warn = 1,
    /// <summary>提示：不影响应用，只是解释当前值从哪来。</summary>
    Info = 2
}

/// <summary>参数规格：范围 + 单位 + 为什么是这个范围（机理，直接给用户看）。</summary>
public sealed class ParamSpec
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public ParamTier Tier { get; init; } = ParamTier.Global;
    public double Min { get; init; }
    public double Max { get; init; }
    /// <summary>离散可选值；为空表示连续。τ 只能取 2 的幂就靠这个表达。</summary>
    public double[]? Allowed { get; init; }
    /// <summary>机理说明：这条边界从哪来，为什么不能越界。</summary>
    public string Why { get; init; } = "";
}

public sealed class ParamViolation
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public ParamSeverity Severity { get; init; }
    public string Message { get; init; } = "";
    public override string ToString() => $"{Label}：{Message}";
}

/// <summary>
/// 参数机理约束表 + 校验器。
///
/// 分两类：
///   1) 单参数范围（<see cref="Specs"/>）——"这个值物理上只能这么大"，例如 EPP 是 8 位、
///      τ 在 MSR 0x610 里是 log2 编码只能取 2 的幂、TJmax 100℃ 所以上限卡 95。
///   2) 交叉约束（<see cref="Validate"/>）——"这两个值放在一起会出事"，例如 PL2 &lt; PL1
///      会让 0x610 的 pl2 字段失去意义、温度上限低于室温+15 会让功率预算为负。
///
/// 关键原则：**硬约束来自硬件机理，不是产品偏好**。所以"静音档风扇只给 55%"不是约束，
/// 而"风扇上限不能低于风扇下限""PL1 不能超过平台持续功耗能力"才是。
/// </summary>
public static class ParamConstraints
{
    /// <summary>i7-7700HQ（及同代 HQ）的 TJmax；留 5℃ 余量作为任何参数的绝对上限。</summary>
    public const double TjMaxC = 100.0;
    public const double SafetyCeilingC = 95.0;

    // 说明：PL1/PL2/GPU 的上限**不是常量**，而是这台机器的电气能力，由 PlatformPower 探测
    // （MSR 0x614 thermal_spec_power / NVML / 型号表 / 用户手填）。这里只保留属性转发，
    // 让校验与界面永远读"当前生效值"，不要在任何地方再写死 45/56/75。
    // 本机参考值：i7-7700HQ 45W、BIOS 出厂 PL2 56.25W、GTX 1050 Ti 75W。
    /// <summary>PL1 可用上限 = CPU 设计功耗（探测值或用户手填）。</summary>
    public static double PlatformPl1MaxW => PlatformPower.CpuDesignW;
    /// <summary>PL2 可用上限（BIOS 实际 PL2 或 1.25×设计功耗）。</summary>
    public static double PlatformPl2MaxW => PlatformPower.Pl2CeilingW;
    /// <summary>独显设计功耗；未知时返回 NaN。</summary>
    public static double PlatformGpuMaxW => PlatformPower.GpuDesignW;
    /// <summary>PL2 相对 PL1 的机理上限：Intel 建议 PL2 ≤ 1.25×PL1，给到 2.0 已是极限。</summary>
    public const double Pl2OverPl1Max = 2.0;
    /// <summary>0x610 的 time 字段是 log2 编码，最大 64s（字段 0x7F 的实用上限）。</summary>
    public const double TauMaxS = 64.0;
    public static readonly double[] TauAllowed = { 1, 2, 4, 8, 16, 32, 64 };

    /// <summary>降压安全边界：低于 -150mV 大概率在负载切换时蓝屏，属于"用户自定义会烧/炸机"的典型。</summary>
    public const double UndervoltMinMv = -150.0;

    private static IReadOnlyList<ParamSpec>? _specs;
    private static double _specsCpu = double.NaN, _specsPl2 = double.NaN;

    /// <summary>
    /// 参数规格表。Pl1/Pl2 的 Max 随**本机平台功率**变化，所以每次访问都校验一次缓存键：
    /// 用户改了"设计功耗"之后，PL1 的允许区间必须立刻跟着变，不能停留在旧值上。
    /// </summary>
    public static IReadOnlyList<ParamSpec> Specs
    {
        get
        {
            if (_specs == null
                || Math.Abs(_specsCpu - PlatformPower.CpuDesignW) > 0.01
                || Math.Abs(_specsPl2 - PlatformPower.Pl2CeilingW) > 0.01)
            {
                _specs = BuildSpecs();
                _specsCpu = PlatformPower.CpuDesignW;
                _specsPl2 = PlatformPower.Pl2CeilingW;
            }
            return _specs;
        }
    }

    private static List<ParamSpec> BuildSpecs() => new List<ParamSpec>
    {
        new()
        {
            Key = "Tceiling", Label = "温度上限", Unit = "℃", Tier = ParamTier.Global,
            Min = 70, Max = SafetyCeilingC,
            Why = "TJmax=100℃，留 5℃ 余量；低于 70℃ 时功率预算过小，等于主动砍性能。"
        },
        new()
        {
            Key = "Tambient", Label = "室温", Unit = "℃", Tier = ParamTier.Global,
            Min = 5, Max = 40,
            Why = "本机无环境温度传感器，只能用户输入；它直接决定功率预算 (上限−室温)/R 的分子。"
        },
        new()
        {
            Key = "FanMax", Label = "风扇上限", Unit = "%", Tier = ParamTier.Global,
            Min = 30, Max = 100,
            Why = "低于 30% 时满载散热量出不去（R2 急剧上升），会直接撞温度墙。"
        },
        new()
        {
            Key = "CpuDesign", Label = "CPU 设计功耗", Unit = "W", Tier = ParamTier.Advanced,
            Min = 0, Max = PlatformPower.AbsoluteCpuMaxW,
            Why = "PL1 的可用上限由它决定。0 = 自动（MSR 0x614 thermal_spec_power）；手填只在这台机器的"
                + "电气能力与程序探测不一致时才需要（换主板、cTDP 改装、探测失败）。"
        },
        new()
        {
            Key = "GpuDesign", Label = "独显设计功耗", Unit = "W", Tier = ParamTier.Advanced,
            Min = 0, Max = PlatformPower.AbsoluteGpuMaxW,
            Why = "只用于整机热预算：CPU 与 GPU 共用热管，GPU 满载时 CPU 的可持续功率要被它挤占。"
                + "0 = 自动（NVML / 型号表）。"
        },
        new()
        {
            Key = "Pl1", Label = "PL1 持续功耗", Unit = "W", Tier = ParamTier.Advanced,
            Min = 5, Max = PlatformPl1MaxW,
            Why = "0x610 的 PL1 字段单位 1/8W；上限取本机 CPU 设计功耗 —— 超过它只会变成纯发热，不增加有效频率。"
        },
        new()
        {
            Key = "Pl2", Label = "PL2 突增功耗", Unit = "W", Tier = ParamTier.Advanced,
            Min = 5, Max = PlatformPl2MaxW,
            Why = "PL2 是短时上限，必须 ≥ PL1；同时受供电上限约束（取 BIOS 实际 PL2 或 1.25×CPU 设计功耗）"
                + " —— 即使只撑几秒，超过它也只是让 VRM/电池供电路径贴着极限跑，不会换来更高频率。"
        },
        new()
        {
            Key = "Tau", Label = "τ 突增时长", Unit = "s", Tier = ParamTier.Advanced,
            Min = 1, Max = TauMaxS, Allowed = TauAllowed,
            Why = "MSR 0x610 的 time 字段是 log2 编码，只能精确表示 2 的幂；写 20s 实际会变成 16s。"
        },
        new()
        {
            Key = "Epp", Label = "EPP 能效偏好", Unit = "", Tier = ParamTier.Advanced,
            Min = 0, Max = 255,
            Why = "MSR 0x773 的 EPP 是 8 位字段：0=最偏性能（升频最快），255=最偏节能。"
        },
        new()
        {
            Key = "Turbo", Label = "睿频倍率上限", Unit = "×100MHz", Tier = ParamTier.Advanced,
            Min = 8, Max = 40,
            Why = "不能超过芯片自身最大睿频（0x1AD 原值），否则写进去被硬件静默截断。"
        },
        new()
        {
            Key = "Undervolt", Label = "降压", Unit = "mV", Tier = ParamTier.Advanced,
            Min = UndervoltMinMv, Max = 0,
            Why = "再深的偏移在负载瞬变时容易不稳（蓝屏/重启），属于「改了就炸」的边界。"
        },
        new()
        {
            Key = "FanMin", Label = "风扇下限", Unit = "%", Tier = ParamTier.Advanced,
            Min = 0, Max = 100,
            Why = "必须 ≤ 风扇上限，否则曲线两端矛盾，输出会在两个值之间来回跳。"
        },
        new()
        {
            Key = "EcInterval", Label = "写 EC 间隔", Unit = "ms", Tier = ParamTier.Advanced,
            Min = 1000, Max = 10000,
            Why = "EC 是一条慢速单总线，电池电量/插电状态也由它上报；写太快会让 ACPI EC 驱动超时（"
                + "表现为检测不到电池），写太慢（>10s）风扇又跟不上功率突增"
        },
        new()
        {
            Key = "Critical", Label = "安全线（超温强制降档）", Unit = "℃", Tier = ParamTier.Advanced,
            Min = 80, Max = SafetyCeilingC,
            Why = "模型基线：TJmax 100℃ 留 5℃ 余量，只允许往下调、不允许上调；这条线一到就无条件把 PL1 压到最低，压过一切策略。"
        }
    };

    public static ParamSpec? Find(string key) => Specs.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// 校验一份配置。返回空列表表示可以安全应用。
    /// </summary>
    /// <param name="cfg">待校验配置（草稿）</param>
    /// <param name="rEstimate">当前估计的总热阻 K/W；用于预测稳态温度，拿不到就传 NaN 跳过该项</param>
    /// <param name="maxTurboRatio">
    /// 芯片实际最大睿频倍率（×100MHz）。能读到 0x1AD 就传真实值，读不到传 0 表示不校验。
    /// </param>
    /// <param name="caps">
    /// 平台能力（可选）。传了就额外检查"这台机器上有没有这个旋钮"：
    /// 不支持的项给 Info 级提示（不是 Block —— 参数会被清洗掉、不会写进硬件，
    /// 用 Block 反而会让用户无法应用其它合法参数）。
    /// </param>
    public static List<ParamViolation> Validate(ThermalConfig cfg, double rEstimate, int maxTurboRatio = 0,
                                                CpuCapabilities? caps = null, bool rConfident = true)
    {
        var v = new List<ParamViolation>();
        if (cfg is null) return v;

        void Block(string key, string msg) => v.Add(new ParamViolation
        { Key = key, Label = Find(key)?.Label ?? key, Severity = ParamSeverity.Block, Message = msg });

        void Warn(string key, string msg) => v.Add(new ParamViolation
        { Key = key, Label = Find(key)?.Label ?? key, Severity = ParamSeverity.Warn, Message = msg });

        // —— 1) 单参数范围（机理表）——
        void Range(string key, double value)        {
            var spec = Find(key);
            if (spec is null) return;
            if (double.IsNaN(value)) { Block(key, "不是有效数字"); return; }

            // 先判绝对范围：离散表只负责"能不能精确表示"，不能替代上下限检查
            if (value < spec.Min - 1e-6 || value > spec.Max + 1e-6)
            {
                Block(key, $"{value:0.##}{spec.Unit} 超出机理范围 {spec.Min:0.##}~{spec.Max:0.##}{spec.Unit}（{spec.Why}）");
                return;
            }

            if (spec.Allowed is { Length: > 0 })
            {
                if (!spec.Allowed.Any(a => Math.Abs(a - value) < 1e-6))
                {
                    double nearest = spec.Allowed.OrderBy(a => Math.Abs(a - value)).First();
                    Warn(key, $"{value:0.##}{spec.Unit} 无法精确表示，硬件会落在 {nearest:0}{spec.Unit}（{spec.Why}）");
                }
                return;
            }
        }

        Range("Tceiling", cfg.AutoTargetC);
        Range("Tambient", cfg.AmbientC);
        Range("FanMax", cfg.FanMaxPct);
        Range("FanMin", cfg.FanMinPct);
        Range("Critical", cfg.CriticalTempC);
        Range("Undervolt", cfg.UndervoltMv);
        Range("EcInterval", cfg.FanWriteMinIntervalMs);
        if (cfg.CpuDesignW > 0) Range("CpuDesign", cfg.CpuDesignW);
        if (cfg.GpuDesignW > 0) Range("GpuDesign", cfg.GpuDesignW);

        bool cpuOn = cfg.AutoCpuMode || cfg.EnableCpuTuning;
        if (cpuOn)
        {
            // PL1=0 在配置里表示"不改"，只有显式给了值才校验
            if (cfg.CpuPl1W > 0) Range("Pl1", cfg.CpuPl1W);
            if (cfg.CpuPl2W > 0) Range("Pl2", cfg.CpuPl2W);
            if (cfg.CpuTauS > 0) Range("Tau", cfg.CpuTauS);
            // -1 是「不改」的唯一哨兵值；其它负数都是填错了（例如 -5 会被当成 255 写进 8 位字段）
            if (cfg.CpuEpp >= 0) Range("Epp", cfg.CpuEpp);
            else if (cfg.CpuEpp != -1) Block("Epp", $"EPP={cfg.CpuEpp} 无意义：只有 -1 表示「不改」，"
                                                   + "其余取值必须在 0~255 之间");
            if (cfg.CpuTurboRatio > 0) Range("Turbo", cfg.CpuTurboRatio);
        }

        // —— 1b) 平台功率：手填值与探测值偏离过大时提醒（不阻断，cTDP 改装是真实存在的）——
        if (PlatformPower.Sanity(cfg.CpuDesignW, cfg.GpuDesignW) is { } powerNote)
            Warn("CpuDesign", powerNote);

        // —— 2) 交叉约束：放在一起会出事 ——

        // 2.1 温度上限必须显著高于室温，否则功率预算为负
        if (cfg.AutoTargetC - cfg.AmbientC < 15)
            Block("Tceiling", $"温度上限 {cfg.AutoTargetC:0}℃ 与室温 {cfg.AmbientC:0}℃ 只差 "
                + $"{cfg.AutoTargetC - cfg.AmbientC:0}℃，功率预算 (上限−室温)/R 会趋近于 0，无法运行；至少留 15℃");

        // 2.2 安全线不能高于温度上限，否则"约束"互相矛盾
        if (cfg.CriticalTempC <= cfg.AutoTargetC)
            Warn("Critical", $"安全线 {cfg.CriticalTempC:0}℃ 不高于温度上限 {cfg.AutoTargetC:0}℃，"
                + "等于一开始就触发强制降档，PL1 会被钉在最低值");

        // 2.3 风扇下限 ≤ 上限
        if (cfg.FanMinPct > cfg.FanMaxPct)
            Block("FanMin", $"风扇下限 {cfg.FanMinPct:0}% 高于上限 {cfg.FanMaxPct:0}%，曲线两端矛盾");

        // 2.4 PL2 ≥ PL1（0x610 语义要求）
        if (cpuOn && cfg.CpuPl1W > 0 && cfg.CpuPl2W > 0)
        {
            if (cfg.CpuPl2W < cfg.CpuPl1W)
                Block("Pl2", $"PL2 {cfg.CpuPl2W}W 低于 PL1 {cfg.CpuPl1W}W；0x610 里 PL2 是「突增上限」，"
                    + "低于持续上限时字段失去意义，实际行为不可预测");
            else if (cfg.CpuPl2W > cfg.CpuPl1W * Pl2OverPl1Max)
                Block("Pl2", $"PL2 {cfg.CpuPl2W}W 超过 2×PL1（{cfg.CpuPl1W * Pl2OverPl1Max:0}W）；"
                    + "τ 窗口内温升会失控，撞温度墙反而更慢");
        }

        // 2.5 睿频倍率不能超过芯片本身
        if (cpuOn && cfg.CpuTurboRatio > 0 && maxTurboRatio > 0 && cfg.CpuTurboRatio > maxTurboRatio)
            Warn("Turbo", $"{cfg.CpuTurboRatio}×100MHz 高于芯片最大睿频 {maxTurboRatio}×100MHz，"
                + "会被硬件静默截断（不报错，但也不生效）");

        // 2.6 降压 + 节能 EPP 同时拉满：容易在低负载电压下不稳
        if (cfg.EnableUndervolt && cfg.UndervoltMv <= -120 && cfg.CpuEpp >= 200)
            Warn("Undervolt", $"降压 {cfg.UndervoltMv}mV 且 EPP={cfg.CpuEpp}（强节能），"
                + "低负载电压会非常低，可能出现空闲蓝屏；建议 EPP ≤ 160 或降压收回到 −100mV 以内");

        // 2.7 机理预测：PL1 会不会把温度顶穿上限
        if (cpuOn && cfg.CpuPl1W > 0 && !double.IsNaN(rEstimate) && rEstimate > 0.05)
        {
            double predicted = cfg.AmbientC + rEstimate * cfg.CpuPl1W;
            // 热阻只有"满载准稳态"样本才可信。样本不足时这条预测会把 PL1 误杀（旧版用空闲样本
            // 算出 R=2.7，于是任何 >26W 的 PL1 都被判"预测 96℃"），所以降级为警告而不是阻断。
            if (predicted > SafetyCeilingC && rConfident)
                Block("Pl1", $"按当前热阻 R≈{rEstimate:0.00}K/W，PL1 {cfg.CpuPl1W}W 的稳态温度约 "
                    + $"{predicted:0}℃，已超过安全线 {SafetyCeilingC:0}℃；硬件会自己降频，你付出的只是更高温度");
            else if (predicted > SafetyCeilingC)
                Warn("Pl1", $"按参考热阻 R≈{rEstimate:0.00}K/W（样本不足，仅供参考），PL1 {cfg.CpuPl1W}W 的"
                    + $"稳态温度约 {predicted:0}℃，可能超过安全线；建议先跑一次满载标定再定这个值");
            else if (predicted > cfg.AutoTargetC)
                Warn("Pl1", $"按 R≈{rEstimate:0.00}K/W，PL1 {cfg.CpuPl1W}W 的稳态温度约 {predicted:0}℃，"
                    + $"会高于温度上限 {cfg.AutoTargetC:0}℃——功率墙和温度墙会互相打架，实际频率会来回抖");
        }

        // —— 3) 平台能力：这台机器上"有没有这个旋钮" ——
        // 注意这是 Info 而不是 Block：参数会在应用前被 CpuCapabilities.Sanitize 清掉，
        // 不会写进硬件。用 Block 会让整份配置（含合法的风扇/温度参数）都点不了应用。
        if (caps != null)
        {
            foreach (var name in caps.UnsupportedInUse(cfg))
            {
                var item = caps.For(name switch
                {
                    "PL1/PL2/τ" => "Pl",
                    "EPP" => "Epp",
                    "睿频上限" => "Turbo",
                    "降压" => "Uv",
                    _ => "Pl"
                });
                v.Add(new ParamViolation
                {
                    Key = item?.Key ?? "Pl",
                    Label = item?.Label ?? name,
                    Severity = ParamSeverity.Info,
                    Message = $"本机不支持（{item?.Detail}）—— 应用时会被忽略，不会写入硬件。"
                              + (string.IsNullOrEmpty(item?.Advice) ? "" : " " + item!.Advice)
                });
            }
            // 平台支持但当前拿不到权限：只在用户真的要用时才提醒一次（否则每次校验都刷四条噪声）
            bool usePl = cfg.AutoCpuMode || cfg.EnableCpuTuning;
            foreach (var it in caps.Items)
            {
                if (it.State != CapState.NeedsPrerequisite) continue;
                bool inUse = it.Key switch
                {
                    "Pl" => usePl,
                    "Epp" => cfg.CpuEpp >= 0,
                    "Turbo" => cfg.CpuTurboRatio > 0,
                    "Uv" => cfg.EnableUndervolt,
                    "Freq" => cfg.EnableFrequencyControl,
                    _ => false
                };
                if (!inUse) continue;
                v.Add(new ParamViolation
                {
                    Key = it.Key, Label = it.Label, Severity = ParamSeverity.Info,
                    Message = "当前不可用：" + it.Detail + (string.IsNullOrEmpty(it.Advice) ? "" : " " + it.Advice)
                });
            }
        }

        return v;
    }

    public static bool HasBlocking(IEnumerable<ParamViolation> v) =>
        v.Any(x => x.Severity == ParamSeverity.Block);

    /// <summary>自检：约束表本身要能拦住典型错误值，也不能误杀合法值。</summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        ThermalConfig Base() => new()
        {
            AutoCpuMode = true, CpuPl1W = 30, CpuPl2W = 38, CpuTauS = 16, CpuEpp = 128,
            CpuTurboRatio = 34, AutoTargetC = 88, AmbientC = 24, FanMinPct = 20, FanMaxPct = 75,
            CriticalTempC = 95, UndervoltMv = -50, EnableUndervolt = false
        };

        // A) 合法配置必须零阻断
        var ok = Validate(Base(), 1.8);
        if (HasBlocking(ok)) errors.Add("合法配置被判为阻断：" + string.Join("；", ok));

        // B) PL2 < PL1 必须阻断
        var c1 = Base(); c1.CpuPl2W = 20;
        if (!HasBlocking(Validate(c1, 1.8))) errors.Add("PL2<PL1 未被阻断");

        // C) 温度上限超过 95 必须阻断
        var c2 = Base(); c2.AutoTargetC = 98;
        if (!HasBlocking(Validate(c2, 1.8))) errors.Add("温度上限 98℃ 未被阻断");

        // D) 温度上限低于室温+15 必须阻断
        var c3 = Base(); c3.AutoTargetC = 70; c3.AmbientC = 60;
        if (!HasBlocking(Validate(c3, 1.8))) errors.Add("上限−室温<15℃ 未被阻断");

        // E) τ 非 2 的幂 → 警告（可应用），不能吞掉
        var c4 = Base(); c4.CpuTauS = 20;
        var v4 = Validate(c4, 1.8);
        if (HasBlocking(v4)) errors.Add("τ=20s 不该被阻断（应只警告）");
        if (!v4.Any(x => x.Key == "Tau" && x.Severity == ParamSeverity.Warn))
            errors.Add("τ=20s 没有给出量化警告");

        // F) τ=64 合法，τ=128 阻断
        var c5 = Base(); c5.CpuTauS = 128;
        if (!HasBlocking(Validate(c5, 1.8))) errors.Add("τ=128s 未被阻断");

        // G) 风扇下限 > 上限 必须阻断
        var c6 = Base(); c6.FanMinPct = 90; c6.FanMaxPct = 60;
        if (!HasBlocking(Validate(c6, 1.8))) errors.Add("风扇下限>上限 未被阻断");

        // H) 降压 -200mV 必须阻断
        var c7 = Base(); c7.UndervoltMv = -200;
        if (!HasBlocking(Validate(c7, 1.8))) errors.Add("降压 -200mV 未被阻断");

        // I) 断链机（R=1.8）上 PL1=45W 必须阻断：预测 24+1.8*45=105℃ > 95
        var c8 = Base(); c8.CpuPl1W = 45; c8.CpuPl2W = 50;
        if (!HasBlocking(Validate(c8, 1.8))) errors.Add("R=1.8 时 PL1=45W 未被阻断");

        // J) 健康机（R=0.3）上 PL1=45W 只应警告/通过，不能阻断
        var c9 = Base(); c9.CpuPl1W = 45; c9.CpuPl2W = 50;
        if (HasBlocking(Validate(c9, 0.3))) errors.Add("R=0.3 时 PL1=45W 被误杀");

        // K) 睿频超过芯片上限 → 警告
        var c10 = Base(); c10.CpuTurboRatio = 38;
        var v10 = Validate(c10, 0.3, maxTurboRatio: 34);
        if (!v10.Any(x => x.Key == "Turbo" && x.Severity == ParamSeverity.Warn))
            errors.Add("睿频 38 > 芯片 34 未给出警告");

        return errors;
    }
}
