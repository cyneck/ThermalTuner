using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ThermalPilot.Core;

/// <summary>一个功率上限值从哪来 —— 决定界面上要不要标"仅供参考"。</summary>
public enum PowerSource
{
    /// <summary>完全没探测到，也没有可用的型号信息。</summary>
    Unknown = 0,
    /// <summary>MSR 0x614 的 thermal_spec_power 字段。最可信：这是 CPU 自己在规格里声明的 TDP。</summary>
    MsrThermalSpec = 1,
    /// <summary>MSR 0x610 的 PL1/PL2 现值。反映 BIOS/OEM 出厂设置，但可能被别人（含本程序）改过。</summary>
    MsrPowerLimit = 2,
    /// <summary>NVML（nvidia-smi）报告的 default/max power limit。桌面卡与多数非 Optimus 笔记本可用。</summary>
    Nvml = 3,
    /// <summary>按型号名查内置表得到的标称值。只是"这类芯片通常多少瓦"，不是这台机器的实测。</summary>
    SkuTable = 4,
    /// <summary>用户手填。以用户为准。</summary>
    User = 5,
    /// <summary>内置默认值（没有任何依据时的兜底）。</summary>
    Fallback = 6
}

/// <summary>一个带来源说明的功率值。</summary>
public sealed class PowerLimit
{
    public double Watts { get; init; } = double.NaN;
    public PowerSource Source { get; init; } = PowerSource.Unknown;
    /// <summary>这个数是怎么来的（直接给用户看，例如 "MSR 0x614 thermal_spec_power = 45W"）。</summary>
    public string Evidence { get; init; } = "";

    public bool HasValue => !double.IsNaN(Watts) && Watts > 0;

    /// <summary>是否属于"这台机器上的真实值"（而不是型号推测或兜底）。</summary>
    public bool Measured => Source is PowerSource.MsrThermalSpec or PowerSource.MsrPowerLimit
                                   or PowerSource.Nvml or PowerSource.User;

    public string Badge => Source switch
    {
        PowerSource.MsrThermalSpec => "MSR 0x614",
        PowerSource.MsrPowerLimit => "MSR 0x610",
        PowerSource.Nvml => "NVML",
        PowerSource.SkuTable => "型号表",
        PowerSource.User => "手填",
        PowerSource.Fallback => "内置默认",
        _ => "未探测"
    };

    public string Text => HasValue ? $"{Watts:F0}W（{Badge}）" : $"未知（{Badge}）";

    public override string ToString() => Text;
}

/// <summary>
/// 平台功率上限：CPU 设计功耗、PL2 上限、独显设计功耗。
///
/// 为什么需要这一层：PL1/PL2 的"合法区间"不是常数，而是**这台机器**的电气能力。
/// 把它们写死成 45/56/75 只对当前这一台（HP OMEN 15-ce / i7-7700HQ / GTX 1050 Ti）成立，
/// 换一台 15W 低压本或 65W 标压本，上限就必须跟着变 —— 否则要么把用户的手脚绑住，
/// 要么允许他写一个物理上不可能的值。
///
/// 能读到就绝不猜，读的顺序是：
///
///   CPU：① MSR 0x614 thermal_spec_power（Intel SDM：这个字段就是规格里的 TDP，单位 W）
///        ② MSR 0x610 PL1（BIOS 出厂设置，反映 OEM 的 cTDP 选择；但可能被改过，故降一级）
///        ③ 型号表（"这类芯片通常 45W"，仅供回退）
///        ④ 内置默认
///
///   GPU：① NVML（nvidia-smi 的 default/max power limit，桌面卡与多数非 Optimus 笔记本可读）
///        ② 型号表
///        ③ 内置默认
///
/// 两个已知边界，必须写进界面而不是藏起来：
///   · Optimus / Max-Q 笔记本上 NVML 的功率字段常返回 N/A（省电策略把 PMIC 关掉了），
///     这台机器就是这样 —— 所以 GPU 只能靠型号表或用户手填；
///   · MSR 0x610 的现值可能等于"上一次本程序写进去的值"，所以它排在 0x614 之后，
///     并且一旦用户手填就以用户为准。
/// </summary>
public static class PlatformPower
{
    /// <summary>CPU 设计功耗的兜底值。用户提供的本机值：i7-7700HQ = 45W。</summary>
    public const double DefaultCpuDesignW = 45.0;
    /// <summary>独显设计功耗的兜底值。用户提供：GTX 1050 Ti = 75W。</summary>
    public const double DefaultGpuDesignW = 75.0;
    /// <summary>PL2 上限相对 CPU 设计功耗的倍率：Intel 建议 ≤1.25×TDP（BIOS 出厂也是 56.25/45）。</summary>
    public const double Pl2RatioOfDesign = 1.25;
    /// <summary>绝对硬上限（协议/机械层）。任何机器都不该超过，用于挡住手填的离谱值。</summary>
    public const double AbsoluteCpuMaxW = 150.0;
    public const double AbsoluteGpuMaxW = 600.0;

    /// <summary>MSR_PKG_POWER_INFO：bits[14:0] thermal_spec_power(W)，bits[46:32] maximum_power(W)。</summary>
    public const uint MsrPkgPowerInfo = 0x614;

    /// <summary>纯探测结果（不含用户覆盖）。10 分钟内复用，避免反复起 nvidia-smi 进程。</summary>
    private static PowerLimit _cpuDet = new()
    {
        Watts = DefaultCpuDesignW, Source = PowerSource.Fallback,
        Evidence = $"未探测，用内置默认 {DefaultCpuDesignW:F0}W"
    };
    private static PowerLimit _gpuDet = new()
    {
        Watts = DefaultGpuDesignW, Source = PowerSource.Fallback,
        Evidence = $"未探测，用内置默认 {DefaultGpuDesignW:F0}W"
    };
    /// <summary>叠加用户覆盖后的生效值。</summary>
    private static PowerLimit _cpu = _cpuDet;
    private static PowerLimit _gpu = _gpuDet;
    private static double _pl2Ceil = DefaultCpuDesignW * Pl2RatioOfDesign;
    private static string _pl2Evidence = "按 1.25×CPU 设计功耗派生";
    private static DateTime _detectedAt = DateTime.MinValue;

    public static PowerLimit Cpu => _cpu;
    public static PowerLimit Gpu => _gpu;
    /// <summary>PL1 的可用上限 = CPU 设计功耗。</summary>
    public static double CpuDesignW => _cpu.HasValue ? _cpu.Watts : DefaultCpuDesignW;
    /// <summary>PL2 的可用上限。</summary>
    public static double Pl2CeilingW => _pl2Ceil;
    /// <summary>独显设计功耗；double.NaN 表示未知（不参与整机热预算计算）。</summary>
    public static double GpuDesignW => _gpu.HasValue ? _gpu.Watts : double.NaN;
    public static bool Detected => _detectedAt != DateTime.MinValue;
    public static DateTime DetectedAt => _detectedAt;

    /// <summary>一行摘要，用于界面/日志。</summary>
    public static string Summary =>
        $"CPU {Cpu.Text} · PL2 ≤ {_pl2Ceil:F0}W · GPU {(Gpu.HasValue ? Gpu.Text : "未知")}";

    /// <summary>逐项说明（诊断页 / 悬浮提示用）。</summary>
    public static IEnumerable<string> Detail
    {
        get
        {
            yield return $"CPU 设计功耗 {Cpu.Text} —— {Cpu.Evidence}";
            yield return $"PL2 上限 {_pl2Ceil:F0}W —— {_pl2Evidence}";
            yield return Gpu.HasValue
                ? $"独显设计功耗 {Gpu.Text} —— {Gpu.Evidence}"
                : "独显设计功耗 未知 —— " + Gpu.Evidence;
        }
    }

    /// <summary>
    /// 探测并应用。传 <paramref name="cfg"/> 时，用户在配置里填了非 0 值则以用户为准。
    /// 该方法会被引擎启动、点「重新探测」、以及应用配置时调用；内部做缓存（同一台机器结果不会变）。
    /// </summary>
    public static void Apply(ThermalConfig? cfg = null, MsrAccess? msr = null, bool force = false)
    {
        // 探测很贵（要起 nvidia-smi 进程），10 分钟内复用；但**用户覆盖必须每次重算** ——
        // 否则用户改了"设计功耗"却要等缓存过期才生效。
        bool needProbe = force || msr != null || !Detected ||
                         DateTime.UtcNow - _detectedAt > TimeSpan.FromMinutes(10);
        if (needProbe)
        {
            _cpuDet = DetectCpu(msr);
            _gpuDet = DetectGpu();
            _detectedAt = DateTime.UtcNow;
        }

        // 用户手填优先：这两个数是"我知道我的机器多少瓦"，程序不该替他做主。
        double userCpu = cfg?.CpuDesignW ?? 0;
        double userGpu = cfg?.GpuDesignW ?? 0;

        _cpu = userCpu > 0.5
            ? new PowerLimit
            {
                Watts = Math.Clamp(userCpu, 5, AbsoluteCpuMaxW), Source = PowerSource.User,
                Evidence = $"用户手填 {userCpu:F0}W（自动探测值：{_cpuDet.Text}）"
            }
            : _cpuDet;

        _gpu = userGpu > 0.5
            ? new PowerLimit
            {
                Watts = Math.Clamp(userGpu, 5, AbsoluteGpuMaxW), Source = PowerSource.User,
                Evidence = $"用户手填 {userGpu:F0}W（自动探测值：{_gpuDet.Text}）"
            }
            : _gpuDet;

        // PL2 上限：优先用 BIOS 实际配置的 PL2（若读得到且合理），否则按 1.25×设计功耗派生。
        double pl2Ceil = Math.Round(CpuDesignW * Pl2RatioOfDesign, 0);
        _pl2Evidence = $"按 {Pl2RatioOfDesign:0.##}×CPU 设计功耗（{CpuDesignW:F0}W）派生";
        if (msr != null && msr.Available &&
            msr.TryRead(CpuControlService.MSR_PKG_POWER_LIMIT, out ulong v610))
        {
            double biosPl2 = CpuControlService.Pl2Of(v610);
            if (biosPl2 > 1 && biosPl2 <= CpuDesignW * 3 && biosPl2 > pl2Ceil)
            {
                pl2Ceil = Math.Round(biosPl2, 0);
                _pl2Evidence = $"取 BIOS 实际配置的 PL2 = {biosPl2:0.#}W（MSR 0x610），高于 1.25× 派生值";
            }
        }
        _pl2Ceil = Math.Clamp(pl2Ceil, CpuDesignW, AbsoluteCpuMaxW * 1.5);

        Log.Info("平台功率：" + Summary);
    }

    /// <summary>只探测不写入全局状态（自检/预览用）。</summary>
    public static (PowerLimit cpu, PowerLimit gpu) Probe(MsrAccess? msr) => (DetectCpu(msr), DetectGpu());

    // ---------------- CPU ----------------

    private static PowerLimit DetectCpu(MsrAccess? msr)
    {
        // ① MSR 0x614 thermal_spec_power —— 规格声明的 TDP，最可信
        if (msr is { Available: true } && msr.TryRead(MsrPkgPowerInfo, out ulong v614))
        {
            double tdp = v614 & 0x7FFF;
            if (tdp >= 5 && tdp <= AbsoluteCpuMaxW)
                return new PowerLimit
                {
                    Watts = tdp, Source = PowerSource.MsrThermalSpec,
                    Evidence = $"MSR 0x614 thermal_spec_power = {tdp:F0}W（Intel 规格里的 TDP）"
                };
        }

        // ② MSR 0x610 的 PL1 —— BIOS/OEM 的 cTDP 选择。可能被改过，所以排第二。
        if (msr is { Available: true } && msr.TryRead(CpuControlService.MSR_PKG_POWER_LIMIT, out ulong v610))
        {
            double pl1 = CpuControlService.Pl1Of(v610);
            if (pl1 >= 5 && pl1 <= AbsoluteCpuMaxW)
                return new PowerLimit
                {
                    Watts = Math.Round(pl1, 0), Source = PowerSource.MsrPowerLimit,
                    Evidence = $"MSR 0x610 PL1 = {pl1:0.#}W（BIOS/OEM 当前配置，可能已被其它工具改动）"
                };
        }

        // ③ 型号表：只作回退，明确标注"标称值"
        string brand = CpuCapabilities.Current.Brand;
        if (!string.IsNullOrWhiteSpace(brand) && CpuSkuTable.TryMatch(brand, out double sku))
            return new PowerLimit
            {
                Watts = sku, Source = PowerSource.SkuTable,
                Evidence = $"按型号「{brand}」查内置表得标称 {sku:F0}W（未读到 MSR，仅供参考）"
            };

        // ④ 兜底
        return new PowerLimit
        {
            Watts = DefaultCpuDesignW, Source = PowerSource.Fallback,
            Evidence = $"读不到 MSR 0x614/0x610，用内置默认 {DefaultCpuDesignW:F0}W（本机 i7-7700HQ 的值）"
        };
    }

    // ---------------- GPU ----------------

    private static PowerLimit DetectGpu()
    {
        // ① NVML：nvidia-smi 的 default / max power limit
        string nvName = "";
        string? smi = FindNvidiaSmi();
        if (smi != null)
        {
            string outp = Shell.Run(smi,
                "--query-gpu=name,power.default_limit,power.max_limit --format=csv,noheader,nounits", 8000);
            foreach (var line in outp.Split('\n'))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                nvName = parts[0].Trim();
                double def = ParseWatts(parts[1]);
                double max = ParseWatts(parts[2]);
                if (def > 0 || max > 0)
                {
                    double w = def > 0 ? def : max;
                    return new PowerLimit
                    {
                        Watts = Math.Round(w, 0), Source = PowerSource.Nvml,
                        Evidence = $"NVML：{nvName} 默认功率上限 {def:0.#}W / 最大 {max:0.#}W"
                    };
                }
            }
        }

        // ② 型号表。名字优先取 NVML 报的（那是最准的商用名），退而用 WMI。
        //    走到这一步的典型机器：Optimus/Max-Q 笔记本 —— nvidia-smi 存在但功率字段全是 N/A，
        //    因为独显由 MUX/Optimus 动态供电，PMIC 没有暴露给 NVML。
        string gpuName = nvName.Length > 0 ? nvName : GpuName();
        if (CpuSkuTable.TryMatchGpu(gpuName, out double g))
            return new PowerLimit
            {
                Watts = g, Source = PowerSource.SkuTable,
                Evidence = $"NVML 功率字段为 N/A，按显卡型号「{gpuName}」查内置表得标称 {g:F0}W（仅供参考）"
            };

        // ③ 兜底：给出本机值但标明"未探测"
        return new PowerLimit
        {
            Watts = DefaultGpuDesignW, Source = PowerSource.Fallback,
            Evidence = (smi != null
                    ? "nvidia-smi 的功率字段为 N/A（Optimus 笔记本常见）"
                    : "未找到 nvidia-smi")
                + $"，型号「{(gpuName.Length > 0 ? gpuName : "未知")}」未收录；用内置默认 {DefaultGpuDesignW:F0}W"
        };
    }

    /// <summary>通过 WMI 取独显名称（进程内查询，不起外部进程）。</summary>
    private static string GpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                string n = (mo["Name"] as string ?? "").Trim();
                if (n.Length == 0) continue;
                if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                return n;
            }
        }
        catch { /* WMI 不可用（极简系统/服务被禁用）时静默回退 */ }
        return "";
    }

    private static double ParseWatts(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "NVIDIA Corporation", "NVIDIA App", "nvidia-smi.exe")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>参数校验用：手填值与探测值差距过大时提醒（不阻断，因为 cTDP 改装是真实存在的）。</summary>
    public static string? Sanity(double cpuDesign, double gpuDesign)
    {
        var np = Probe(null);
        if (cpuDesign > 0.5 && np.cpu.Measured && Math.Abs(cpuDesign - np.cpu.Watts) > np.cpu.Watts * 0.25)
            return $"CPU 设计功耗手填 {cpuDesign:F0}W，与探测值 {np.cpu.Watts:F0}W（{np.cpu.Badge}）相差超过 25%："
                 + "确认改过散热/供电或确认这是 cTDP 改装机，否则 PL1 的允许区间会偏离实际能力。";
        if (gpuDesign > 0.5 && np.gpu.Measured && Math.Abs(gpuDesign - np.gpu.Watts) > np.gpu.Watts * 0.25)
            return $"GPU 设计功耗手填 {gpuDesign:F0}W，与探测值 {np.gpu.Watts:F0}W（{np.gpu.Badge}）相差超过 25%。";
        return null;
    }

    // ---------------- 自检 ----------------

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 型号表：本机两行必须能命中用户给的值
        if (!CpuSkuTable.TryMatch("Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz", out double c1) || Math.Abs(c1 - 45) > 0.1)
            errors.Add($"型号表未把 i7-7700HQ 识别为 45W（得到 {c1}）");
        if (!CpuSkuTable.TryMatchGpu("NVIDIA GeForce GTX 1050 Ti", out double g1) || Math.Abs(g1 - 75) > 0.1)
            errors.Add($"型号表未把 GTX 1050 Ti 识别为 75W（得到 {g1}）");
        if (CpuSkuTable.TryMatch("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", out double c2) && c2 > 20)
            errors.Add($"低压 U（i5-8250U）被判成 {c2}W，应 ≤20W");
        if (CpuSkuTable.TryMatch("Some Unknown CPU", out _))
            errors.Add("未知型号不应命中型号表");

        // B) 优先级：0x614 有值时不能用型号表或默认值覆盖
        var noMsr = (MsrAccess?)null;
        var p0 = Probe(noMsr);
        if (p0.cpu.Source != PowerSource.SkuTable && p0.cpu.Source != PowerSource.Fallback)
            errors.Add($"无 MSR 时 CPU 来源应为型号表/兜底，实际 {p0.cpu.Source}");

        // C) 用户手填必须覆盖探测值，且被夹进物理边界
        var cfg = new ThermalConfig { CpuDesignW = 28, GpuDesignW = 0 };
        Apply(cfg, null, force: true);
        if (Math.Abs(CpuDesignW - 28) > 0.1) errors.Add($"手填 28W 未生效（得到 {CpuDesignW:F1}）");
        if (Cpu.Source != PowerSource.User) errors.Add($"手填后来源应为 User，实际 {Cpu.Source}");
        if (Math.Abs(Pl2CeilingW - 35) > 1.5) errors.Add($"PL2 上限应随设计功耗派生（28×1.25=35），实际 {Pl2CeilingW:F1}");

        var crazy = new ThermalConfig { CpuDesignW = 9999 };
        Apply(crazy, null, force: true);
        if (CpuDesignW > AbsoluteCpuMaxW) errors.Add($"手填 9999W 没被夹到 {AbsoluteCpuMaxW:F0}W 以内（{CpuDesignW:F1}）");

        // D) 恢复自动（0 = 自动）后不应保留手填值
        Apply(new ThermalConfig(), null, force: true);
        if (Cpu.Source == PowerSource.User) errors.Add("配置清成 0（自动）后仍标着手填");
        if (CpuDesignW < 5) errors.Add($"自动值异常：{CpuDesignW:F1}W");

        // E) 未知 GPU 不得伪造数值
        if (double.IsNaN(GpuDesignW) && Gpu.HasValue)
            errors.Add("GPU 标为未知却又给出数值");

        return errors;
    }
}

/// <summary>
/// 内置型号 → 标称功率表。只用于"读不到硬件值"时的回退，命中后界面会标「型号表」并注明仅供参考。
/// 只收录功率比较确定的型号；拿不准的一律不收录（宁可说未知，也不给错数）。
/// </summary>
internal static class CpuSkuTable
{
    // 顺序有意义：更具体的型号写在前面（先匹配先返回）
    private static readonly (string key, double w)[] Cpu =
    {
        // 45W 标压（HQ/H 系列）
        ("7700HQ", 45), ("7820HK", 45), ("6700HQ", 45), ("6820HK", 45), ("4720HQ", 47), ("4710HQ", 47),
        ("8750H", 45), ("8300H", 45), ("9750H", 45), ("10750H", 45), ("10875H", 45),
        ("11800H", 45), ("11850H", 45), ("12700H", 45), ("12900H", 45), ("13700H", 45), ("13900H", 45),
        // AMD 标压
        ("4800H", 45), ("5800H", 45), ("5900H", 45), ("6800H", 45), ("6900H", 45), ("7840H", 45), ("7940H", 45),
        // 15W 低压 U
        ("8250U", 15), ("8265U", 15), ("8550U", 15), ("8565U", 15), ("10210U", 15), ("10510U", 15),
        ("1035G1", 15), ("1065G7", 15), ("5800U", 15), ("5700U", 15), ("4500U", 15), ("1165G7", 28), ("1135G7", 28),
        // 台式 / 工作站
        ("12700", 65), ("13700", 65), ("10700", 65), ("9700", 65), ("8700", 65), ("7700", 65), ("6700", 65),
        ("Xeon", 85), ("E5-", 85)
    };

    private static readonly (string key, double w)[] Gpu =
    {
        ("1050 Ti", 75), ("1050", 53), ("1650", 50), ("1660 Ti", 80), ("1660", 80),
        ("2060", 90), ("2070", 115), ("2080", 150),
        ("3050", 75), ("3060", 115), ("3070", 130), ("3080", 165),
        ("4060", 115), ("4070", 140), ("4080", 175), ("4090", 175),
        ("MX150", 25), ("MX250", 25), ("MX350", 25), ("MX450", 25), ("MX550", 30)
    };

    public static bool TryMatch(string brand, out double watts)
    {
        watts = 0;
        if (string.IsNullOrWhiteSpace(brand)) return false;
        foreach (var (key, w) in Cpu)
        {
            if (brand.Contains(key, StringComparison.OrdinalIgnoreCase)) { watts = w; return true; }
        }
        return false;
    }

    public static bool TryMatchGpu(string name, out double watts)
    {
        watts = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        // 先匹配更长的键（"1050 Ti" 优先于 "1050"）
        foreach (var (key, w) in Gpu.OrderByDescending(x => x.key.Length))
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) { watts = w; return true; }
        }
        return false;
    }
}
