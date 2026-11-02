namespace ThermalPilot.Core;

/// <summary>一次读取到的 CPU MSR 控制状态（用于 UI「读取当前值」与还原校验）。</summary>
public sealed class CpuMsrState
{
    public bool Valid { get; init; }
    public double Pl1W { get; init; }
    public double Pl2W { get; init; }
    public double TauS { get; init; }
    public int Epp { get; init; }
    /// <summary>单核睿频倍率（0x1AD bits[7:0]），例如 38 = 3.8GHz。</summary>
    public int TurboRatio1Core { get; init; }
    /// <summary>全核睿频倍率（0x1AD 最高存在的档位，例如 34 = 3.4GHz）。四核满载的上限就是它。</summary>
    public int TurboRatioAllCore { get; init; }
    public uint Raw610 { get; init; }
    public uint Raw773 { get; init; }
    public uint Raw1AD { get; init; }
}

/// <summary>
/// CPU 精细调控（等价 ThrottleStop 的核心旋钮，直接读写 Intel MSR）：
///   PL1 / PL2 / Time(τ)  MSR 0x610 IA32_PACKAGE_POWER_LIMIT
///   EPP (Speed Shift)    MSR 0x773 IA32_HWP_REQUEST_PKG
///   睿频倍率上限          MSR 0x1AD TURBO_RATIO_LIMIT
///
/// 设计要点：
///   1) 全部 read-modify-write，只改目标字段，保留其它位（不破坏 BIOS 其它设置）；
///   2) 写后立刻读回校验，字段不符即判定失败（BIOS 锁 / 不支持）；
///   3) 首次成功写入前保存原始值，Restore() 精确还原；
///   4) 任何一步失败都记录原因，不抛异常（控制循环不能被它带崩）。
///
/// 单位：PL1/PL2 字段为 1/8 W；Time 字段为 log2(秒)；EPP 0..255（0=性能，255=节能）。
/// </summary>
public sealed class CpuControlService
{
    // MSR 地址
    public const uint MSR_PKG_POWER_LIMIT = 0x610;
    public const uint MSR_HWP_REQUEST_PKG = 0x773;
    public const uint MSR_TURBO_RATIO_LIMIT = 0x1AD;

    // 0x610 字段
    private const ulong PL1_MASK = 0x7FFFUL;          // bits 14:0
    private const int PL1_ENABLE_BIT = 15;
    private const int PL1_CLAMP_BIT = 16;
    private const int PL1_TIME_SHIFT = 17;            // bits 23:17
    private const ulong TIME_MASK = 0x7FUL;

    private const int PL2_SHIFT = 32;                 // bits 46:32
    private const int PL2_ENABLE_BIT = 47;
    private const int PL2_CLAMP_BIT = 48;
    private const int PL2_TIME_SHIFT = 49;            // bits 55:49

    // 0x773 EPP 字段
    private const int EPP_SHIFT = 24;                 // bits 31:24
    private const ulong EPP_MASK = 0xFFUL;

    private readonly MsrAccess _msr = new();
    private readonly object _gate = new();

    private ulong? _orig610;
    private ulong? _orig773;
    private ulong? _orig1AD;

    public bool Available => _msr.Available;
    public bool IsActive { get; private set; }
    public string LastMessage { get; private set; } = "未应用";
    public string LastError => _msr.LastError;
    /// <summary>共享同一个 Ring0 句柄，供 PerfMetrics 等其它 MSR 读者复用。</summary>
    public MsrAccess Msr => _msr;

    public bool EnsureAvailable() => _msr.Available || _msr.Probe();

    /// <summary>读取当前 PL1/PL2/τ/EPP/睿频倍率。失败返回 null。</summary>
    public CpuMsrState? ReadCurrent()
    {
        lock (_gate) return ReadCurrentCore();
    }

    private CpuMsrState? ReadCurrentCore()
    {
        if (!EnsureAvailable()) return null;
        if (!_msr.TryRead(MSR_PKG_POWER_LIMIT, out ulong v610)) return null;
        _msr.TryRead(MSR_HWP_REQUEST_PKG, out ulong v773);
        _msr.TryRead(MSR_TURBO_RATIO_LIMIT, out ulong v1ad);

        return new CpuMsrState
        {
            Valid = true,
            Pl1W = Pl1Of(v610),
            Pl2W = Pl2Of(v610),
            TauS = TauOf(v610),
            Epp = (int)((v773 >> EPP_SHIFT) & EPP_MASK),
            TurboRatio1Core = OneCoreRatio(v1ad),
            TurboRatioAllCore = AllCoreRatio(v1ad),
            Raw610 = (uint)v610,
            Raw773 = (uint)v773,
            Raw1AD = (uint)v1ad
        };
    }

    /// <summary>下发 PL1/PL2/Time（τ 同时用于 PL1 与 PL2 时间窗）。成功返回 true。</summary>
    public bool ApplyPowerLimits(double pl1W, double? pl2W = null, double? tauS = null)
    {
        lock (_gate) return ApplyPowerLimitsCore(pl1W, pl2W, tauS);
    }

    private bool ApplyPowerLimitsCore(double pl1W, double? pl2W, double? tauS)
    {
        if (!EnsureAvailable())
        {
            LastMessage = "MSR 不可用：" + LastError;
            return false;
        }

        pl1W = Math.Clamp(pl1W, 3, 200);
        if (pl2W is { } p2) pl2W = Math.Clamp(p2, pl1W, 250);
        if (tauS is { } t) tauS = Math.Clamp(t, 1, 64);

        if (!_msr.TryRead(MSR_PKG_POWER_LIMIT, out ulong cur))
        {
            LastMessage = "读取 MSR 0x610 失败";
            return false;
        }

        ulong value = EncodePowerLimit(cur, pl1W, pl2W, tauS);

        if (!_msr.TryWrite(MSR_PKG_POWER_LIMIT, value))
        {
            LastMessage = "写入 MSR 0x610 失败（BIOS 可能锁定 PL）";
            return false;
        }

        // 读回校验
        if (!_msr.TryRead(MSR_PKG_POWER_LIMIT, out ulong rb))
        {
            LastMessage = "写入后回读 MSR 0x610 失败";
            return false;
        }

        bool okPl1 = Math.Abs(Pl1Of(rb) - pl1W) <= 0.26;
        bool okPl2 = pl2W is null || Math.Abs(Pl2Of(rb) - pl2W.Value) <= 0.26;
        bool okTau = tauS is null || Math.Abs(TauOf(rb) - DecodeTau(EncodeTau(tauS.Value))) < 0.01;

        if (!okPl1 || !okPl2 || !okTau)
        {
            LastMessage = $"PL 写入被拒绝（回读 PL1={Pl1Of(rb):F1}W PL2={Pl2Of(rb):F1}W τ={TauOf(rb):F0}s）";
            return false;
        }

        _orig610 ??= cur;
        IsActive = true;
        LastMessage = $"PL1={Pl1Of(rb):F1}W" + (pl2W is null ? "" : $" PL2={Pl2Of(rb):F1}W") +
                      (tauS is null ? "" : $" τ={TauOf(rb):F0}s") + " 已生效";
        return true;
    }

    /// <summary>下发 EPP（0=性能 … 255=节能）。成功返回 true。</summary>
    public bool ApplyEpp(int epp)
    {
        lock (_gate) return ApplyEppCore(epp);
    }

    private bool ApplyEppCore(int epp)
    {
        if (!EnsureAvailable()) { LastMessage = "MSR 不可用：" + LastError; return false; }
        epp = Math.Clamp(epp, 0, 255);

        if (!_msr.TryRead(MSR_HWP_REQUEST_PKG, out ulong cur))
        {
            LastMessage = "读取 MSR 0x773 失败（可能不支持 HWP/Speed Shift）";
            return false;
        }

        ulong value = (cur & ~(EPP_MASK << EPP_SHIFT)) | ((ulong)epp << EPP_SHIFT);
        if (!_msr.TryWrite(MSR_HWP_REQUEST_PKG, value))
        {
            LastMessage = "写入 MSR 0x773 失败（HWP 可能被 BIOS 锁定）";
            return false;
        }

        if (!_msr.TryRead(MSR_HWP_REQUEST_PKG, out ulong rb))
        {
            LastMessage = "写入后回读 MSR 0x773 失败";
            return false;
        }

        int got = (int)((rb >> EPP_SHIFT) & EPP_MASK);
        if (got != epp)
        {
            LastMessage = $"EPP 写入被拒绝（回读 {got}）";
            return false;
        }

        _orig773 ??= cur;
        IsActive = true;
        LastMessage = $"EPP={epp} 已生效";
        return true;
    }

    /// <summary>把各核睿频倍率上限压到 maxRatio（×100MHz，0=不改）。成功返回 true。</summary>
    public bool ApplyTurboRatio(int maxRatio)
    {
        lock (_gate) return ApplyTurboRatioCore(maxRatio);
    }

    private bool ApplyTurboRatioCore(int maxRatio)
    {
        if (maxRatio <= 0) return true;
        if (!EnsureAvailable()) { LastMessage = "MSR 不可用：" + LastError; return false; }
        maxRatio = Math.Clamp(maxRatio, 8, 60);

        if (!_msr.TryRead(MSR_TURBO_RATIO_LIMIT, out ulong cur) || cur == 0)
        {
            LastMessage = "读取 MSR 0x1AD 失败（该平台不暴露睿频倍率表）";
            return false;
        }

        ulong value = cur;
        for (int i = 0; i < 8; i++)
        {
            int shift = i * 8;
            int ratio = (int)((cur >> shift) & 0xFF);
            if (ratio == 0) continue;                 // 该核数档位不存在
            int nv = Math.Min(ratio, maxRatio);
            value = (value & ~(0xFFUL << shift)) | ((ulong)nv << shift);
        }

        if (value == cur)
        {
            LastMessage = $"睿频倍率已 ≤ {maxRatio}（无需修改）";
            return true;
        }

        if (!_msr.TryWrite(MSR_TURBO_RATIO_LIMIT, value))
        {
            LastMessage = "写入 MSR 0x1AD 失败（倍频上限多被 BIOS 锁定）";
            return false;
        }

        if (!_msr.TryRead(MSR_TURBO_RATIO_LIMIT, out ulong rb))
        {
            LastMessage = "写入后回读 MSR 0x1AD 失败";
            return false;
        }

        // 校验：所有存在的档位都不超过 maxRatio
        for (int i = 0; i < 8; i++)
        {
            int ratio = (int)((rb >> (i * 8)) & 0xFF);
            if (ratio != 0 && ratio > maxRatio)
            {
                LastMessage = $"睿频倍率写入被拒绝（回读第 {i + 1} 档 {ratio}）";
                return false;
            }
        }

        _orig1AD ??= cur;
        IsActive = true;
        LastMessage = $"睿频倍率上限 = {maxRatio}（{maxRatio / 10.0:F1}GHz）已生效";
        return true;
    }

    /// <summary>一键下发一组旋钮（仅非空项）。返回每条结果说明。</summary>
    public List<string> ApplyAll(double? pl1W, double? pl2W, double? tauS, int? epp, int? turboRatio)
    {
        var notes = new List<string>();
        if (pl1W is > 0) notes.Add(ApplyPowerLimits(pl1W.Value, pl2W, tauS) ? "✔ " : "✘ " + LastMessage);
        if (epp is >= 0) notes.Add(ApplyEpp(epp.Value) ? "✔ " : "✘ " + LastMessage);
        if (turboRatio is > 0) notes.Add(ApplyTurboRatio(turboRatio.Value) ? "✔ " : "✘ " + LastMessage);
        return notes;
    }

    /// <summary>还原到首次写入前的原始值。</summary>
    public void Restore()
    {
        lock (_gate) RestoreCore();
    }

    private void RestoreCore()
    {
        if (!EnsureAvailable()) return;
        try
        {
            if (_orig610 is { } v610) _msr.TryWrite(MSR_PKG_POWER_LIMIT, v610);
            if (_orig773 is { } v773) _msr.TryWrite(MSR_HWP_REQUEST_PKG, v773);
            if (_orig1AD is { } v1ad) _msr.TryWrite(MSR_TURBO_RATIO_LIMIT, v1ad);
            if (IsActive) Log.Info("CPU MSR 控制已还原为原始值。");
        }
        catch (Exception ex)
        {
            Log.Error("还原 CPU MSR 失败", ex);
        }
        finally
        {
            _orig610 = _orig773 = _orig1AD = null;
            IsActive = false;
        }
    }

    // ---------------------------------------------------------------- 编解码（公开以便自检）

    public static ulong EncodePowerLimit(ulong cur, double pl1W, double? pl2W, double? tauS)
    {
        ulong v = cur;

        v &= ~PL1_MASK;
        v |= WattsToField(pl1W);
        v |= 1UL << PL1_ENABLE_BIT;
        v |= 1UL << PL1_CLAMP_BIT;

        if (tauS is { } t)
        {
            ulong code = EncodeTau(t);
            v &= ~(TIME_MASK << PL1_TIME_SHIFT);
            v |= code << PL1_TIME_SHIFT;
        }

        if (pl2W is { } p2)
        {
            v &= ~(PL1_MASK << PL2_SHIFT);
            v |= WattsToField(p2) << PL2_SHIFT;
            v |= 1UL << PL2_ENABLE_BIT;
            v |= 1UL << PL2_CLAMP_BIT;

            if (tauS is { } t2)
            {
                ulong code = EncodeTau(t2);
                v &= ~(TIME_MASK << PL2_TIME_SHIFT);
                v |= code << PL2_TIME_SHIFT;
            }
        }

        return v;
    }

    /// <summary>
    /// 0x1AD 的布局：每个字节一个"活跃核数档位"，从低到高是 1 核、2 核、3 核、4 核…
    /// **低字节是单核**（本机 i7-7700HQ = 38/36/35/34），不是全核上限。
    /// 旧代码把低字节当"全核倍频"用，于是满载 3.41GHz 被拿去和 3.8GHz 比，
    /// 永远判不出"已顶到全核上限"——这就是"40W 时明明已经满频却报功率墙"的根因。
    /// </summary>
    public static int OneCoreRatio(ulong raw) => (int)(raw & 0xFF);

    /// <summary>全核倍率：取最高存在档位（最后一个非零字节）。</summary>
    public static int AllCoreRatio(ulong raw)
    {
        int all = 0;
        for (int i = 0; i < 8; i++)
        {
            int r = (int)((raw >> (i * 8)) & 0xFF);
            if (r > 5 && r < 80) all = r;
        }
        return all > 0 ? all : OneCoreRatio(raw);
    }

    /// <summary>所有存在档位（从 1 核开始，0 表示该档不存在）。</summary>
    public static int[] TurboTiers(ulong raw)
    {
        var list = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            int r = (int)((raw >> (i * 8)) & 0xFF);
            list.Add(r is > 5 and < 80 ? r : 0);
        }
        return list.ToArray();
    }

    public static double Pl1Of(ulong v) => (v & PL1_MASK) / 8.0;
    public static double Pl2Of(ulong v) => ((v >> PL2_SHIFT) & PL1_MASK) / 8.0;
    public static double TauOf(ulong v) => DecodeTau((v >> PL1_TIME_SHIFT) & TIME_MASK);
    public static int EppOf(ulong v) => (int)((v >> EPP_SHIFT) & EPP_MASK);

    private static ulong WattsToField(double w) => (ulong)Math.Round(Math.Clamp(w, 0, 4095) * 8.0);

    /// <summary>时间窗编码：log2(秒) 取整，范围 1..64s（Intel 的定义）。</summary>
    public static ulong EncodeTau(double seconds)
    {
        double s = Math.Clamp(seconds, 1, 64);
        int code = (int)Math.Round(Math.Log2(s));
        return (ulong)Math.Clamp(code, 0, 6);
    }

    /// <summary>
    /// 时间窗解码。Intel 定义字段是 log2(秒)，合法域 0..6（1~64s）。
    /// 有些 BIOS 根本不写这两个字段，读出来是 110 / 33 这类垃圾值——直接 2^110 会得到
    /// 1.3e33 秒这种荒谬数字。所以越界一律返回 0，表示「未设置 / 非法」，由上层显示成「—」。
    /// </summary>
    public static double DecodeTau(ulong field) => field <= 6 ? Math.Pow(2, (double)field) : 0.0;

    /// <summary>τ 的显示文本（0 = 未设置）。</summary>
    public static string TauText(double tauS) => tauS >= 1 ? $"{tauS:0}s" : "—";

    /// <summary>离线自检：验证编码/解码与字段互不干扰。返回空列表表示全部通过。</summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) errors.Add($"{name} 失败 {detail}");
        }

        // 典型 45W 级笔记本：PL1=35W PL2=60W τ=20s，且保留一个无关位
        ulong baseVal = 0xA000_0000_0000_0000UL | (0x1234UL); // 高位随机 + 低位已有值
        ulong v = EncodePowerLimit(baseVal, 35, 60, 20);
        Check("PL1", Math.Abs(Pl1Of(v) - 35) < 1e-9, $"{Pl1Of(v)}");
        Check("PL2", Math.Abs(Pl2Of(v) - 60) < 1e-9, $"{Pl2Of(v)}");
        Check("Tau", Math.Abs(TauOf(v) - 16) < 1e-9, $"{TauOf(v)}"); // log2(20)≈4.32→4→16s
        Check("Enable", (v & (1UL << PL1_ENABLE_BIT)) != 0);
        Check("Clamp", (v & (1UL << PL1_CLAMP_BIT)) != 0);

        // 再改一次，确认上一次的字段被干净覆盖
        ulong v2 = EncodePowerLimit(v, 25, 45, 8);
        Check("PL1-2", Math.Abs(Pl1Of(v2) - 25) < 1e-9, $"{Pl1Of(v2)}");
        Check("PL2-2", Math.Abs(Pl2Of(v2) - 45) < 1e-9, $"{Pl2Of(v2)}");
        Check("Tau-2", Math.Abs(TauOf(v2) - 8) < 1e-9, $"{TauOf(v2)}");

        // 不传 PL2/τ 时不应破坏原有高位
        ulong v3 = EncodePowerLimit(baseVal, 30, null, null);
        Check("Retain-high", (v3 & 0xA000_0000_0000_0000UL) == 0xA000_0000_0000_0000UL,
            $"0x{v3:X16}");

        // EPP 编解码
        ulong e = ((ulong)55 << 8) | ((ulong)200 << EPP_SHIFT);
        Check("EPP", EppOf(e) == 200, $"{EppOf(e)}");

        return errors;
    }
}
