using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace ThermalPilot.Core;

/// <summary>平台对某项参数的"支持程度"。注意它和"能不能应用"是两件事，见各类注释。</summary>
public enum CapState
{
    /// <summary>平台支持 + 当前权限具备 → 可写入。</summary>
    Supported = 0,
    /// <summary>平台层面没有这个东西（AMD 没有 Intel 的 0x610，老 CPU 没有 HWP…）→ UI 直接禁用，配置里的值会被清掉。</summary>
    Unsupported = 1,
    /// <summary>平台支持，但当前拿不到（非管理员 / HVCI / 驱动未加载）→ UI 可编辑但会明确提示；不清值（下次提权后仍想生效）。</summary>
    NeedsPrerequisite = 2,
    /// <summary>还没探测（引擎未启动）→ 不做任何判断。</summary>
    Unknown = 3
}

/// <summary>一项能力：状态 + 事实 + 用户能做什么。</summary>
public sealed class CapItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public CapState State { get; init; } = CapState.Unknown;
    /// <summary>事实（为什么是这个状态），直接给用户看。</summary>
    public string Detail { get; init; } = "";
    /// <summary>可执行建议。</summary>
    public string Advice { get; init; } = "";

    /// <summary>平台层面就没有 → 必须禁用、必须清值。</summary>
    public bool Hard => State == CapState.Unsupported;
    /// <summary>现在就能写。</summary>
    public bool Usable => State == CapState.Supported;

    public string Badge => State switch
    {
        CapState.Supported => "✓",
        CapState.Unsupported => "✗ 本机不支持",
        CapState.NeedsPrerequisite => "! 需前置条件",
        _ => "… 未探测"
    };

    public string Tip => string.IsNullOrEmpty(Advice) ? Detail : Detail + Environment.NewLine + Advice;
}

/// <summary>
/// CPU 平台能力矩阵：**这一层才是"高级参数该不该开放"的最终依据**。
///
/// 为什么必须有它：参数范围约束（<see cref="ParamConstraints"/>）回答的是"这个值能多大"，
/// 但回答不了"这台机器上这个旋钮存不存在"：
///   · AMD 平台根本没有 Intel 的 0x610/0x773/0x1AD/0x150 —— 写进去不是"失败"而是"没有这个寄存器"；
///   · 10 代以前没有 HWP（Speed Shift），0x773 的 EPP 无意义；
///   · 无睿频的 CPU（赛扬/奔腾、部分低压 U）不存在 0x1AD 倍率表；
///   · Plundervolt 之后的 Intel（11 代起移动端）把 FIVR 电压偏移关了，0x150 写不进；
///   · 非管理员 / 开 HVCI 时 Ring0 驱动加载不了 —— 这是"暂时"而不是"没有"。
///
/// 于是分三态处理（<see cref="CapState"/>），只有 Unsupported 才真正禁用并清值：
/// 把"暂时不可用"当成"不支持"会让用户提权后还得重填一遍参数。
///
/// 探测分两段：
///   1) CPUID + WMI，无需任何权限，程序一启动就能给出厂商 / 型号 / 睿频 / HWP；
///   2) MSR 实测（0x610 / 0x773 / 0x1AD 能否读出合理值），需要引擎启动后有 Ring0。
/// </summary>
public sealed class CpuCapabilities
{
    // ---------------- 静态信息（无需权限） ----------------

    public string Vendor { get; private init; } = "";
    public string Brand { get; private init; } = "未知 CPU";
    public int Family { get; private init; }
    public int Model { get; private init; }
    public int Stepping { get; private init; }
    public int Cores { get; private init; }
    public int Threads { get; private init; }

    public bool IsIntel => Vendor.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd => Vendor.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase);
    /// <summary>CPUID.06H:EAX[1]：支持 Turbo Boost / IDA。</summary>
    public bool HasTurboBoost { get; private init; }
    /// <summary>CPUID.06H:EAX[7]：支持 HWP（Speed Shift），0x773 的 EPP 才有意义。</summary>
    public bool HasHwp { get; private init; }
    /// <summary>CPUID 探测本身是否可用（ARM64 上没 CPUID）。</summary>
    public bool CpuidOk { get; private init; }

    /// <summary>Ring0 MSR 通道当前是否可用（管理员 + 无 HVCI + 驱动加载成功）。</summary>
    public bool MsrReady { get; private init; }
    public string MsrError { get; private init; } = "未探测（引擎未启动）";

    // ---------------- 各项能力 ----------------

    /// <summary>PL1 / PL2 / τ（MSR 0x610）。</summary>
    public CapItem Pl { get; private init; } = new();
    /// <summary>EPP 能效偏好（MSR 0x773，需 HWP）。</summary>
    public CapItem Epp { get; private init; } = new();
    /// <summary>睿频倍率上限（MSR 0x1AD）。</summary>
    public CapItem Turbo { get; private init; } = new();
    /// <summary>降压（MSR 0x150 OC Mailbox）。</summary>
    public CapItem Uv { get; private init; } = new();
    /// <summary>Windows 电源管理限频（powercfg PROCTHROTTLEMAX），全平台可用。</summary>
    public CapItem Freq { get; private init; } = new();

    public IReadOnlyList<CapItem> Items => new[] { Pl, Epp, Turbo, Uv, Freq };

    /// <summary>按参数键取能力：Pl1/Pl2/Tau 都归 PL。</summary>
    public CapItem? For(string key) => (key ?? "").ToLowerInvariant() switch
    {
        "pl1" or "pl2" or "tau" or "pl" => Pl,
        "epp" => Epp,
        "turbo" => Turbo,
        "uv" or "undervolt" => Uv,
        "freq" or "channel" => Freq,
        _ => null
    };

    /// <summary>MSR 精细调控整套是否还有意义（三项全废就只剩电源管理）。</summary>
    public bool AnyMsrCapability => Pl.Hard == false && (Pl.Usable || Epp.Usable || Turbo.Usable || Uv.Usable);
    public bool AllMsrUnsupported => Pl.Hard && Epp.Hard && Turbo.Hard;

    // ---------------- 一行摘要 / 详情 ----------------

    public string CpuLine
    {
        get
        {
            string arch = CpuidOk && (Family != 0 || Model != 0)
                ? $"，family {Family:X}h model {Model:X}h"
                : "";
            string brand = Brand.Length > 0 ? Brand : "未知 CPU";
            string mt = Cores > 0 ? $"，{Cores} 核 {Threads} 线程" : "";
            return $"{brand}（{VendorShort}{arch}{mt}）";
        }
    }

    private string VendorShort => IsIntel ? "Intel" : IsAmd ? "AMD" : (Vendor.Length > 0 ? Vendor : "未知厂商");

    /// <summary>紧凑徽章串：PL1/PL2 ✓ · EPP ✓ · 睿频 ✓ · 降压 ✗。</summary>
    public string BadgeLine =>
        $"PL1/PL2 {Pl.Badge}　EPP {Epp.Badge}　睿频 {Turbo.Badge}　降压 {Uv.Badge}";

    /// <summary>多行详情（只列非 Supported 的项，避免正常运行也刷一屏）。</summary>
    public string Details
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var it in Items)
            {
                if (it.State == CapState.Supported) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append($"{it.Label}：{it.Badge} —— {it.Detail}");
                if (!string.IsNullOrEmpty(it.Advice)) sb.Append(" 建议：" + it.Advice);
            }
            return sb.Length == 0 ? "全部能力可用。" : sb.ToString();
        }
    }

    /// <summary>写进支持报告的多行段落。</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  CPU：" + CpuLine);
        sb.AppendLine($"  CPUID：{(CpuidOk ? "可用" : "不可用（非 x86 架构）")}；Turbo={(HasTurboBoost ? "是" : "否")}；"
                    + $"HWP/SpeedShift={(HasHwp ? "是" : "否")}");
        sb.AppendLine($"  MSR/Ring0：{(MsrReady ? "可用" : "不可用（" + MsrError + "）")}");
        foreach (var it in Items)
            sb.AppendLine($"  · {it.Label,-14} {it.Badge,-12} {it.Detail}");
        return sb.ToString();
    }

    // ---------------- 探测 ----------------

    /// <summary>
    /// 探测平台能力。<paramref name="msr"/> 为空表示还没有 Ring0（引擎未启动），
    /// 此时 MSR 相关项为 Unknown / NeedsPrerequisite，不会误判成"不支持"。
    /// </summary>
    public static CpuCapabilities Detect(MsrAccess? msr = null)
    {
        var (vendor, family, model, stepping, cpuidOk, turbo, hwp) = ReadCpuid();
        var (brand, cores, threads) = ReadWmi();

        bool msrReady = false;
        string msrError;
        ulong v610 = 0, v773 = 0, v1ad = 0;
        bool r610 = false, r773 = false, r1ad = false;

        if (msr == null)
        {
            msrError = "未探测（引擎未启动，Ring0 尚未加载）";
        }
        else if (!msr.Available)
        {
            msrError = string.IsNullOrWhiteSpace(msr.LastError) ? "MSR 不可用" : msr.LastError;
        }
        else
        {
            msrReady = true;
            msrError = "";
            r610 = msr.TryRead(0x610, out v610);
            r773 = msr.TryRead(0x773, out v773);
            r1ad = msr.TryRead(0x1AD, out v1ad);
        }

        bool intel = vendor.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase);
        bool amd = vendor.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase);

        return new CpuCapabilities
        {
            Vendor = vendor,
            Brand = brand,
            Family = family,
            Model = model,
            Stepping = stepping,
            Cores = cores,
            Threads = threads,
            CpuidOk = cpuidOk,
            HasTurboBoost = turbo,
            HasHwp = hwp,
            MsrReady = msrReady,
            MsrError = msrError,
            Pl = EvalPl(intel, amd, msr, msrReady, msrError, r610, v610),
            Epp = EvalEpp(intel, amd, hwp, msr, msrReady, msrError, r773),
            Turbo = EvalTurbo(intel, amd, turbo, msr, msrReady, msrError, r1ad, v1ad),
            Uv = EvalUv(intel, family, model, msr, msrReady, msrError),
            Freq = EvalFreq()
        };
    }

    private static CapItem EvalPl(bool intel, bool amd, MsrAccess? msr, bool ready, string err, bool read, ulong raw)
    {
        const string label = "PL1/PL2/τ（功率上限）";
        if (amd)
            return new CapItem
            {
                Key = "Pl", Label = label, State = CapState.Unsupported,
                Detail = "AMD 的功率上限走 SMU 邮箱（PPT/TDC/EDC），与 Intel 的 0x610 是两套完全不同的协议，本程序未实现 SMU 写入。",
                Advice = "用 BIOS 的 PBO / Precision Boost 设置，或 Windows 电源计划的「处理器最大频率」。"
            };
        if (!intel)
            return new CapItem
            {
                Key = "Pl", Label = label, State = CapState.Unsupported,
                Detail = "未识别为 Intel 平台，无法使用 0x610 功率上限寄存器。",
                Advice = "改用 Windows 电源计划限频通道。"
            };
        if (msr == null)
            return new CapItem
            {
                Key = "Pl", Label = label, State = CapState.Unknown,
                Detail = "需要 Ring0 驱动才能读写 0x610，引擎启动后再判定。",
                Advice = ""
            };
        if (!ready)
            return new CapItem
            {
                Key = "Pl", Label = label, State = CapState.NeedsPrerequisite,
                Detail = "平台支持（Intel MSR 0x610），但当前拿不到 Ring0：" + err,
                Advice = "以管理员运行，并在「Windows 安全中心 → 内核隔离」关闭内存完整性后重启。"
            };
        if (!read || (raw & 0x7FFF) == 0)
            return new CapItem
            {
                Key = "Pl", Label = label, State = CapState.Unsupported,
                Detail = read
                    ? "0x610 读出的 PL1 字段为 0，BIOS 未启用 RAPL 功率上限（部分超极本/OEM 锁定）。"
                    : "0x610 读取失败，固件未暴露该 MSR。",
                Advice = "只能用 Windows 电源计划限频，或在 BIOS 里解开功耗墙。"
            };
        return new CapItem
        {
            Key = "Pl", Label = label, State = CapState.Supported,
            Detail = $"0x610 可读写，当前 PL1={CpuControlService.Pl1Of(raw):F1}W / PL2={CpuControlService.Pl2Of(raw):F1}W。",
            Advice = ""
        };
    }

    private static CapItem EvalEpp(bool intel, bool amd, bool hwp, MsrAccess? msr, bool ready, string err, bool read)
    {
        const string label = "EPP 能效偏好";
        if (amd)
            return new CapItem
            {
                Key = "Epp", Label = label, State = CapState.Unsupported,
                Detail = "AMD 用 CPPC/PBO 调能效，没有 Intel 的 HWP EPP 字段（0x773），本程序未实现对应接口。",
                Advice = "用 BIOS 的 PBO / Curve Optimizer，或 Windows 电源计划的「处理器性能提升模式」。"
            };
        if (!intel)
            return new CapItem
            {
                Key = "Epp", Label = label, State = CapState.Unsupported,
                Detail = "未识别为 Intel 平台，无 HWP EPP。", Advice = "改用 Windows 电源计划。"
            };
        if (!hwp)
            return new CapItem
            {
                Key = "Epp", Label = label, State = CapState.Unsupported,
                Detail = "该 CPU 无 HWP（Speed Shift，Skylake 起才有，CPUID.06H:EAX[7]=0），0x773 的 EPP 字段无意义。",
                Advice = "用 Windows 电源计划的「处理器性能提升模式」近似控制，或 BI﻿OS 里调 C-state/EPP。"
            };
        if (msr == null)
            return new CapItem { Key = "Epp", Label = label, State = CapState.Unknown, Detail = "引擎启动后再判定 0x773。" };
        if (!ready)
            return new CapItem
            {
                Key = "Epp", Label = label, State = CapState.NeedsPrerequisite,
                Detail = "平台支持 HWP，但当前拿不到 Ring0：" + err,
                Advice = "以管理员运行并关闭内存完整性。"
            };
        if (!read)
            return new CapItem
            {
                Key = "Epp", Label = label, State = CapState.Unsupported,
                Detail = "0x773 读取失败，BIOS 未启用 HWP 请求寄存器。",
                Advice = "在 BIOS 里开启 Speed Shift / HWP 后再试；否则只能用电源计划。"
            };
        return new CapItem
        {
            Key = "Epp", Label = label, State = CapState.Supported,
            Detail = "0x773 可读写（写入后回读校验，被 BIOS 锁定时会明确报失败）。",
            Advice = ""
        };
    }

    private static CapItem EvalTurbo(bool intel, bool amd, bool turboBit, MsrAccess? msr, bool ready, string err,
                                     bool read, ulong raw)
    {
        const string label = "睿频倍率上限";
        if (amd)
            return new CapItem
            {
                Key = "Turbo", Label = label, State = CapState.Unsupported,
                Detail = "AMD 的 Core Performance Boost 由 PBO/SMU 管理，没有 Intel 的 0x1AD 倍率表。",
                Advice = "用 BIOS 的 PBO / Core Boost 设置。"
            };
        if (!intel)
            return new CapItem
            {
                Key = "Turbo", Label = label, State = CapState.Unsupported,
                Detail = "未识别为 Intel 平台，无 0x1AD 睿频倍率表。", Advice = "改用 Windows 电源计划限频。"
            };
        if (!turboBit)
            return new CapItem
            {
                Key = "Turbo", Label = label, State = CapState.Unsupported,
                Detail = "该 CPU 不支持睿频（CPUID.06H:EAX[1]=0，多见于赛扬/奔腾/部分低压 U）。",
                Advice = "没有睿频可限，用 Windows 电源计划的频率百分比即可。"
            };
        if (msr == null)
            return new CapItem { Key = "Turbo", Label = label, State = CapState.Unknown, Detail = "引擎启动后再判定 0x1AD。" };
        if (!ready)
            return new CapItem
            {
                Key = "Turbo", Label = label, State = CapState.NeedsPrerequisite,
                Detail = "平台支持睿频，但当前拿不到 Ring0：" + err, Advice = "以管理员运行并关闭内存完整性。"
            };
        if (!read || (raw & 0xFF) == 0)
            return new CapItem
            {
                Key = "Turbo", Label = label, State = CapState.Unsupported,
                Detail = "0x1AD 读出 0 / 读取失败，BIOS 未暴露倍率表（部分 OEM 机型锁定）。",
                Advice = "睿频上限改用 Windows 电源计划的「处理器最大频率(%)」。"
            };
        return new CapItem
        {
            Key = "Turbo", Label = label, State = CapState.Supported,
            Detail = $"0x1AD 可读写：单核 {CpuControlService.OneCoreRatio(raw)}×100MHz / "
                   + $"全核 {CpuControlService.AllCoreRatio(raw)}×100MHz（四核满载的实际天花板）。",
            Advice = ""
        };
    }

    private static CapItem EvalUv(bool intel, int family, int model, MsrAccess? msr, bool ready, string err,
                                  bool? laptop = null)
    {
        const string label = "降压（电压偏移）";
        if (!intel)
            return new CapItem
            {
                Key = "Uv", Label = label, State = CapState.Unsupported,
                Detail = "降压写的是 Intel OC Mailbox（MSR 0x150），AMD 没有这个接口。",
                Advice = "AMD 用 BIOS 的 Curve Optimizer / PBO 降压。"
            };

        int gen = IntelGeneration(family, model);
        if (gen >= 11)
        {
            // Plundervolt（CVE-2019-11157）之后 Intel 在 11 代起关闭 FIVR 电压偏移；
            // 台式 K 系列 / 部分开放 BIOS 仍可写，所以台式机保留"可尝试"，移动端直接判不支持。
            MachineProbe.Load();
            bool lap = laptop ?? MachineProbe.IsLaptop;
            if (lap)
                return new CapItem
                {
                    Key = "Uv", Label = label, State = CapState.Unsupported,
                    Detail = $"该 CPU 为 {gen} 代 Intel 笔记本平台，11 代起移动端 BIOS 已关闭 FIVR 电压偏移（Plundervolt 缓解），0x150 写入会被忽略。",
                    Advice = "改用调 EPP / PL1 降温；或在 BIOS 里找 Voltage Offset（多数消费本不开放）。"
                };
        }

        if (msr == null)
            return new CapItem { Key = "Uv", Label = label, State = CapState.Unknown, Detail = "引擎启动后再判定 0x150。" };
        if (!ready)
            return new CapItem
            {
                Key = "Uv", Label = label, State = CapState.NeedsPrerequisite,
                Detail = "平台可尝试降压，但当前拿不到 Ring0：" + err,
                Advice = "以管理员运行并关闭内存完整性（HVCI 会禁止第三方内核驱动加载）。"
            };
        return new CapItem
        {
            Key = "Uv", Label = label, State = CapState.Supported,
            Detail = gen >= 11
                ? $"0x150 可访问，但 {gen} 代平台多数被 BIOS 锁定 —— 写入后回读校验，失败会明确报错，不会假装成功。"
                : "0x150 可访问（写入后回读校验）。",
            Advice = ""
        };
    }

    private static CapItem EvalFreq() => new()
    {
        Key = "Freq", Label = "电源计划限频", State = CapState.Supported,
        Detail = "Windows 电源管理的 PROCTHROTTLEMAX（处理器最大频率 %）对所有平台有效，限制的是频率百分比而不是瓦数。",
        Advice = ""
    };

    /// <summary>Intel 家族代号 → 代数；认不出来返回 0。只用于"降压是否被锁"的粗判。</summary>
    public static int IntelGeneration(int family, int model)
    {
        if (family != 6) return 0;
        return model switch
        {
            // 4/5 代 Haswell / Broadwell
            0x3C or 0x3F or 0x45 or 0x46 => 4,
            0x3D or 0x47 or 0x4F or 0x56 => 5,
            // 6/7/8/9 代 Skylake → Coffee Lake Refresh
            // 0x9E 同时出现在 Kaby Lake-H 与 Coffee Lake-S 上，取 8 不影响判定（都 < 11）
            0x4E or 0x5E or 0x55 => 6,
            0x8E or 0x9E => 8,
            // 10 代 Ice Lake / Comet Lake
            0x7D or 0x7E or 0xA5 or 0xA6 => 10,
            // 11 代 Tiger Lake / Rocket Lake
            0x8C or 0x8D or 0xA7 => 11,
            // 12 代 Alder Lake
            0x97 or 0x9A or 0x9B => 12,
            // 13/14 代 Raptor Lake
            0xB7 or 0xBA or 0xBF => 13,
            _ => 0
        };
    }

    // ---------------- 配置清洗 ----------------

    /// <summary>
    /// 把"本机不支持"的参数从配置里清掉（只处理 Unsupported，不动 NeedsPrerequisite）。
    /// 返回 true 表示有改动 —— 调用方（参数中心/引擎）需要保存。
    ///
    /// 为什么必须清值而不是只在 UI 禁用：配置文件是跨机器共用的（换机器、重装、公司下发），
    /// 一份带 EPP=128 的配置落到 AMD 机器上，如果不清掉就会一路传到写入层，
    /// 最后变成一条"写入失败"的红字，用户根本不知道那是硬件不存在。
    /// </summary>
    public bool Sanitize(ThermalConfig? c)
    {
        if (c == null) return false;
        bool changed = false;

        if (Pl.Hard)
        {
            if (c.CpuPl1W != 0) { c.CpuPl1W = 0; changed = true; }
            if (c.CpuPl2W != 0) { c.CpuPl2W = 0; changed = true; }
            if (c.CpuTauS != 0) { c.CpuTauS = 0; changed = true; }
            if (c.AutoCpuMode) { c.AutoCpuMode = false; changed = true; }
        }
        if (Epp.Hard && c.CpuEpp >= 0) { c.CpuEpp = -1; changed = true; }
        if (Turbo.Hard && c.CpuTurboRatio != 0) { c.CpuTurboRatio = 0; changed = true; }
        if (Uv.Hard)
        {
            if (c.EnableUndervolt) { c.EnableUndervolt = false; changed = true; }
            if (c.UndervoltMv != 0) { c.UndervoltMv = 0; changed = true; }
        }

        // MSR 三项全废 → 手工调控没有意义，通道退回电源计划（否则写了也全是失败）
        if (AllMsrUnsupported)
        {
            if (c.EnableCpuTuning) { c.EnableCpuTuning = false; changed = true; }
            if (string.Equals(c.ControlChannel, ControlChannels.Msr, StringComparison.OrdinalIgnoreCase))
            {
                c.ControlChannel = ControlChannels.PowerCfg;
                c.EnableFrequencyControl = true;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>配置里"用了但本机不支持"的项（校验面板用来解释为什么没生效）。</summary>
    public IReadOnlyList<string> UnsupportedInUse(ThermalConfig c)
    {
        var list = new List<string>();
        if (c == null) return list;
        if (Pl.Hard && (c.CpuPl1W > 0 || c.CpuPl2W > 0 || c.CpuTauS > 0 || c.AutoCpuMode))
            list.Add("PL1/PL2/τ");
        if (Epp.Hard && c.CpuEpp >= 0) list.Add("EPP");
        if (Turbo.Hard && c.CpuTurboRatio > 0) list.Add("睿频上限");
        if (Uv.Hard && (c.EnableUndervolt || c.UndervoltMv != 0)) list.Add("降压");
        return list;
    }

    // ---------------- CPUID / WMI ----------------

    private static (string vendor, int family, int model, int stepping, bool ok, bool turbo, bool hwp) ReadCpuid()
    {
        if (!X86Supported())
            return ("", 0, 0, 0, false, false, false);
        try
        {
            var leaf0 = X86Base.CpuId(0, 0);
            var sb = new StringBuilder(12);
            AppendVendor(sb, leaf0.Ebx);
            AppendVendor(sb, leaf0.Edx);
            AppendVendor(sb, leaf0.Ecx);
            string vendor = sb.ToString().TrimEnd('\0', ' ');

            var leaf1 = X86Base.CpuId(1, 0);
            uint eax = unchecked((uint)leaf1.Eax);
            int baseFamily = (int)((eax >> 8) & 0xF);
            int extFamily = (int)((eax >> 20) & 0xFF);
            int family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
            int baseModel = (int)((eax >> 4) & 0xF);
            int extModel = (int)((eax >> 16) & 0xF);
            int model = (baseFamily == 0x6 || baseFamily == 0xF) ? (extModel << 4) | baseModel : baseModel;
            int stepping = (int)(eax & 0xF);

            bool turbo = false, hwp = false;
            try
            {
                uint l6 = unchecked((uint)X86Base.CpuId(6, 0).Eax);
                turbo = (l6 & (1u << 1)) != 0;
                hwp = (l6 & (1u << 7)) != 0;
            }
            catch { }

            return (vendor, family, model, stepping, true, turbo, hwp);
        }
        catch
        {
            return ("", 0, 0, 0, false, false, false);
        }
    }

    private static void AppendVendor(StringBuilder sb, int reg)
    {
        uint v = unchecked((uint)reg);
        for (int i = 0; i < 4; i++) sb.Append((char)((v >> (i * 8)) & 0xFF));
    }

    private static bool X86Supported()
    {
        var a = RuntimeInformation.ProcessArchitecture;
        return a == Architecture.X64 || a == Architecture.X86;
    }

    private static (string brand, int cores, int threads) ReadWmi()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                string name = (mo["Name"] as string)?.Trim() ?? "";
                int cores = Convert.ToInt32(mo["NumberOfCores"] ?? 0);
                int th = Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? 0);
                return (name, cores, th);
            }
        }
        catch { }
        return (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", 0, 0);
    }

    // ---------------- 单例（进程内唯一） ----------------

    private static CpuCapabilities? _current;
    /// <summary>当前能力快照。第一次访问做静态探测（无权限也能探测厂商/型号/特性）。</summary>
    public static CpuCapabilities Current => _current ??= Detect(null);

    /// <summary>引擎启动 / 权限变化后刷新（这次带上 Ring0 实测结果）。</summary>
    public static CpuCapabilities Refresh(MsrAccess? msr)
    {
        _current = Detect(msr);
        return _current;
    }

    /// <summary>
    /// 仅供界面自检：把当前快照替换成伪造平台（真机上没法换 CPU，只能这样验证"禁用"逻辑）。
    /// 生产路径不会调用它。
    /// </summary>
    public static void Override(CpuCapabilities caps) => _current = caps;

    /// <summary>仅供自检：按名字构造一份常见的"不支持"平台，用于截图核对禁用状态。</summary>
    public static CpuCapabilities Fake(string kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "amd" => Synthetic("AuthenticAMD", 0x19, 0x21, true, false, CapState.Unsupported, false,
                           "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics"),
        "noturbo" => Synthetic("GenuineIntel", 6, 0x7A, false, true, CapState.Unsupported, true,
                               "Intel(R) Celeron(R) N5105 @ 2.00GHz"),
        "nohwp" => Synthetic("GenuineIntel", 6, 0x3C, true, false, CapState.Unsupported, true,
                             "Intel(R) Core(TM) i7-4770HQ CPU @ 2.20GHz"),
        "needadmin" => Synthetic("GenuineIntel", 6, 0x9E, true, true, CapState.NeedsPrerequisite, true,
                                 "Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz"),
        _ => Synthetic("GenuineIntel", 6, 0x9E, true, true, CapState.Unknown, true,
                       "Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz")
    };

    /// <summary>仅供自检：注入一份伪造的平台特性，验证判定规则。</summary>
    public static CpuCapabilities Synthetic(string vendor, int family, int model, bool turbo, bool hwp,
                                             CapState msrState, bool laptop = true, string brand = "SYNTHETIC")
    {
        bool intel = vendor.Contains("GenuineIntel");
        bool amd = vendor.Contains("AuthenticAMD");
        MsrAccess? msr = msrState == CapState.Unknown ? null : new MsrAccess("自检注入：MSR 不可用");
        bool ready = msrState == CapState.Supported;
        const string err = "自检注入：MSR 不可用";

        return new CpuCapabilities
        {
            Vendor = vendor,
            Brand = brand,
            Family = family,
            Model = model,
            CpuidOk = true,
            HasTurboBoost = turbo,
            HasHwp = hwp,
            MsrReady = ready,
            MsrError = ready ? "" : err,
            Pl = EvalPl(intel, amd, msr, ready, err, ready, ready ? 0x004281C200DD8168UL : 0),
            Epp = EvalEpp(intel, amd, hwp, msr, ready, err, ready),
            Turbo = EvalTurbo(intel, amd, turbo, msr, ready, err, ready, ready ? 38UL : 0),
            Uv = EvalUv(intel, family, model, msr, ready, err, laptop),
            Freq = EvalFreq()
        };
    }

    // ---------------- 自检 ----------------

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) AMD 平台：Intel 私有 MSR 全部判不支持，电源计划仍可用，且配置被清干净
        var amd = Synthetic("AuthenticAMD", 0x19, 0x21, true, false, CapState.Unsupported);
        if (!amd.Pl.Hard || !amd.Epp.Hard || !amd.Turbo.Hard || !amd.Uv.Hard)
            errors.Add("AMD 平台未把 0x610/0x773/0x1AD/0x150 判为不支持");
        if (amd.Freq.State != CapState.Supported)
            errors.Add("AMD 平台的电源计划限频应为可用");
        var ac = new ThermalConfig
        {
            ControlChannel = ControlChannels.Msr, EnableCpuTuning = true, AutoCpuMode = true,
            CpuPl1W = 35, CpuPl2W = 44, CpuTauS = 16, CpuEpp = 128, CpuTurboRatio = 35,
            EnableUndervolt = true, UndervoltMv = -80
        };
        if (!amd.Sanitize(ac)) errors.Add("AMD 平台没有清洗配置");
        if (ac.CpuPl1W != 0 || ac.CpuPl2W != 0 || ac.CpuTauS != 0 || ac.CpuEpp != -1 ||
            ac.CpuTurboRatio != 0 || ac.EnableUndervolt || ac.EnableCpuTuning || ac.AutoCpuMode)
            errors.Add("AMD 平台清洗后仍残留 Intel 私有参数");
        if (ac.ControlChannel != ControlChannels.PowerCfg)
            errors.Add($"AMD 平台通道未退回电源计划（当前 {ac.ControlChannel}）");
        if (!ac.EnableFrequencyControl) errors.Add("AMD 平台退回电源计划后未开启限频");

        // B) Intel 但无睿频（赛扬类）：睿频项必须不支持，PL/EPP 不受影响
        var noTurbo = Synthetic("GenuineIntel", 6, 0x7A, false, true, CapState.Unsupported);
        if (!noTurbo.Turbo.Hard) errors.Add("无睿频 CPU 的睿频项未判为不支持");
        if (!noTurbo.Turbo.Detail.Contains("睿频")) errors.Add("无睿频 CPU 的原因文案缺失");

        // C) Intel 但无 HWP（Haswell 及以前）：EPP 不支持，PL 仍支持
        var noHwp = Synthetic("GenuineIntel", 6, 0x3C, true, false, CapState.Unsupported);
        if (!noHwp.Epp.Hard) errors.Add("无 HWP 的 CPU 未把 EPP 判为不支持");
        if (noHwp.Pl.Hard) errors.Add("无 HWP 不该影响 PL1/PL2 支持");

        // D) 非管理员：MSR 三项都是"需前置条件"，不清值（否则提权后要重填）
        var needAdmin = Synthetic("GenuineIntel", 6, 0x9E, true, true, CapState.NeedsPrerequisite);
        if (needAdmin.Pl.State != CapState.NeedsPrerequisite) errors.Add("非管理员时 PL 状态应为「需前置条件」");
        if (needAdmin.Pl.Hard) errors.Add("非管理员不该判成「平台不支持」");
        var nc = new ThermalConfig { CpuPl1W = 30, CpuEpp = 128 };
        if (needAdmin.Sanitize(nc)) errors.Add("非管理员时不应清洗配置");
        if (nc.CpuPl1W != 30 || nc.CpuEpp != 128) errors.Add("非管理员时配置被误改");

        // E) 引擎未启动（Unknown）：不做判断、不清值
        var unknown = Synthetic("GenuineIntel", 6, 0x9E, true, true, CapState.Unknown);
        if (unknown.Pl.State != CapState.Unknown) errors.Add("未探测时 PL 状态应为 Unknown");
        var uc = new ThermalConfig { CpuPl1W = 30 };
        if (unknown.Sanitize(uc)) errors.Add("未探测时不应清洗配置");

        // F) 11 代移动端：降压判不支持（Plundervolt 后 FIVR 关闭）
        var tl = Synthetic("GenuineIntel", 6, 0x8C, true, true, CapState.Supported);
        if (!tl.Uv.Hard) errors.Add("11 代移动端降压未判为不支持");

        // G) 三种状态互斥：Hard 与 Usable 不能同时为真
        foreach (var it in amd.Items.Concat(needAdmin.Items))
            if (it.Hard && it.Usable) errors.Add($"能力 {it.Label} 状态自相矛盾");

        // H) 真实机器：静态部分必须能识别出厂商（本机是 Intel，但换了机器 AMD 同样要成立）
        var real = Current;
        if (real.CpuidOk && real.Vendor.Length == 0) errors.Add("CPUID 可用但厂商为空");
        if (real.Brand.Length == 0) errors.Add("未取到 CPU 型号名");
        if (real.BadgeLine.Length == 0) errors.Add("能力徽章串为空");

        return errors;
    }
}
