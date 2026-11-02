using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ThermalPilot.Core;

/// <summary>当前到底是「谁」在限制 CPU 变快——这是控温控频的靶心。</summary>
public enum BindingLimit
{
    Unknown = 0,
    /// <summary>没有撞任何墙：还有余量，加功率能换性能。</summary>
    None = 1,
    /// <summary>温度墙：撞 TJmax / PROCHOT，降温才有救，加功率无效。</summary>
    Thermal = 2,
    /// <summary>功率墙：包功率顶在 PL1，加 PL1 能直接换性能。</summary>
    Power = 3,
    /// <summary>频率上限：倍频已顶到 TURBO_RATIO_LIMIT / OS 上限，加功率也上不去。</summary>
    FrequencyCap = 4,
    /// <summary>空闲/轻载：需求本身低于任何限制，调什么都没意义。</summary>
    Idle = 5
}

/// <summary>一次 CPU 交付性能采样（全部来自 MSR，不依赖第三方库的估算）。</summary>
public sealed class PerfSample
{
    public bool Valid { get; init; }
    /// <summary>有效频率（APERF/MPERF × 基准频率），即「真正拿到手的时钟」。0 = 本轮算不出可信值。</summary>
    public double EffectiveMhz { get; init; }
    /// <summary>有效频率算不出来时的原因（"计数器增量过小""占比超物理上限"）。给用户看的。</summary>
    public string EffNote { get; init; } = "";
    /// <summary>C0（核心活跃）期间的平均频率。与有效频率对比即可分辨"被降频"还是"只是没活干"。</summary>
    public double C0Mhz { get; init; }
    /// <summary>当前倍频（IA32_PERF_STATUS[15:8]）× 100MHz。</summary>
    public double CurrentMhz { get; init; }
    /// <summary>全核睿频上限（四核满载能到的最高频），判定"频率是否顶死"用这个。</summary>
    public double TurboLimitMhz { get; init; }
    /// <summary>单核睿频上限（0x1AD 第 1 档），只用于解释"为什么满载到不了这个数"。</summary>
    public double TurboLimit1CoreMhz { get; init; }
    /// <summary>RAPL 能量计数换算出的包功率。</summary>
    public double PackagePowerW { get; init; }
    public bool ThermalThrottle { get; init; }
    public bool Prochot { get; init; }
    public bool PowerLimited { get; init; }
    public BindingLimit Limit { get; init; }
    public string LimitText { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>判定依据的原始数字（例如「包功率 40.2W ≥ PL1 40W」），让人能复核而不是只信结论。</summary>
    public string Evidence { get; init; } = "";
}

/// <summary>
/// CPU 交付性能测量（对应「不要只看温度，要看 CPU 的具体指标」）。
///
/// 温度是**滞后**的代理量：同一个 82℃ 既可能是 15W 轻载、也可能是 45W 满载，
/// 单看温度无法判断「还能不能更快」。所以这里直接量三件事：
///
///   1) 有效频率  ΔAPERF(0xE8)/ΔMPERF(0xE7) × 基准频率
///      —— 含睿频、含 throttling 的真实平均时钟，比"当前倍频"可信；
///   2) 包功率    Δ(0x611 PKG_ENERGY_STATUS) × 能量单位 / Δt（单位取自 0x606[12:8]）
///      —— 与 PL1 比对即可判断功率墙是否在顶；
///   3) 限制原因  0x64B/0x1B1 的热状态位 + 功率是否贴 PL1 + 倍频是否顶上限
///      —— 直接回答「现在加钱（功率）能不能买到性能」。
///
/// 采样成本：每次只在一个逻辑核上读 2 个 MSR（轮转各核），1Hz 完全无感。
/// APERF/MPERF 是 per-LP 计数器，所以要读某个核必须在那个核上执行，
/// 用 SetThreadAffinityMask 临时绑核、读完立即恢复。
/// </summary>
public sealed class PerfMetrics
{
    // ---- MSR 编号
    private const uint MSR_MPERF = 0xE7;
    private const uint MSR_APERF = 0xE8;
    private const uint MSR_PERF_STATUS = 0x198;
    private const uint MSR_THERM_STATUS = 0x64B;
    private const uint MSR_PKG_THERM_STATUS = 0x1B1;
    private const uint MSR_PKG_ENERGY_STATUS = 0x611;
    private const uint MSR_RAPL_POWER_UNIT = 0x606;
    private const uint MSR_TURBO_RATIO_LIMIT = 0x1AD;

    /// <summary>最多处理这么多个逻辑核（APERF/MPERF 是 per-LP 计数器）。</summary>
    private const int MaxLp = 64;

    private readonly MsrAccess _msr;
    private readonly object _gate = new();

    // 每个逻辑核上一次的 (APERF, MPERF, 时间戳)
    private readonly ulong[] _lastAperf = new ulong[MaxLp];
    private readonly ulong[] _lastMperf = new ulong[MaxLp];
    private readonly DateTime[] _lastAt = new DateTime[MaxLp];

    private double _baseMhz = double.NaN;
    private double _energyUnitJ = double.NaN;
    private ulong _lastEnergy;
    private DateTime _lastEnergyAt = DateTime.MinValue;

    private PerfSample _last = new() { Valid = false, Limit = BindingLimit.Unknown, LimitText = "未采样" };

    public PerfMetrics(MsrAccess msr) => _msr = msr;

    public PerfSample Last => _last;

    /// <summary>基准频率（MPERF 的推进速率）。优先取注册表 ~MHz，其次退回 2800。</summary>
    public double BaseMhz
    {
        get
        {
            if (double.IsNaN(_baseMhz)) _baseMhz = ReadBaseMhz();
            return _baseMhz;
        }
    }

    private static double ReadBaseMhz()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("~MHz") is int mhz && mhz > 200) return mhz;
        }
        catch { }
        return 2800;
    }

    // ---- 绑核（读 per-LP 计数器必须在该核上执行）

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr hProcess, out UIntPtr processMask, out UIntPtr systemMask);

    /// <summary>
    /// 本进程允许使用的逻辑核位图。
    /// 不能假设"0..ProcessorCount-1 都能绑"：程序被任务管理器/启动脚本限制到部分核心时，
    /// 绑一个不在允许集里的核会直接失败，结果每一轮都跳过 → 有效频率永远算不出来。
    /// </summary>
    private static ulong AllowedCoreMask()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            if (GetProcessAffinityMask(p.Handle, out UIntPtr pm, out _) && pm != UIntPtr.Zero)
                return pm.ToUInt64();
        }
        catch { }
        // 拿不到就按 ProcessorCount 假定前 n 个核可用
        int n = Math.Clamp(Environment.ProcessorCount, 1, MaxLp);
        return n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    /// <summary>
    /// 采一次样。pl1W 传入当前生效的 PL1（用于判定功率墙），thermalCeiling 为温度上限。
    /// 失败时返回 Valid=false 的样本，绝不抛异常。
    /// </summary>
    public PerfSample Sample(double pl1W, double tempC, double thermalCeilingC, double loadPct)
    {
        lock (_gate)
        {
            try { return SampleCore(pl1W, tempC, thermalCeilingC, loadPct); }
            catch (Exception ex)
            {
                _last = new PerfSample { Valid = false, Limit = BindingLimit.Unknown, LimitText = "采样失败：" + ex.Message };
                return _last;
            }
        }
    }

    private PerfSample SampleCore(double pl1W, double tempC, double thermalCeilingC, double loadPct)
    {
        if (!_msr.Available && !_msr.Probe())
        {
            _last = new PerfSample { Valid = false, Limit = BindingLimit.Unknown, LimitText = "MSR 不可用" };
            return _last;
        }

        var now = DateTime.UtcNow;

        // 当前倍频与睿频上限（package 级寄存器，不需要绑核）。
        // 先读：有效频率的"物理上限"要用它来校验，否则算出来的东西没法判断真假。
        double curMhz = 0, turboMhz = 0, turbo1Mhz = 0;
        if (_msr.TryRead(MSR_PERF_STATUS, out ulong ps))
            curMhz = ((ps >> 8) & 0xFF) * 100.0;
        if (_msr.TryRead(MSR_TURBO_RATIO_LIMIT, out ulong trl))
        {
            // 0x1AD 低字节是**单核**档（本机 38=3.8GHz），全核档在最后一个非零字节（34=3.4GHz）。
            int one = CpuControlService.OneCoreRatio(trl);
            int all = CpuControlService.AllCoreRatio(trl);
            if (all is > 5 and < 80) turboMhz = all * 100.0;
            if (one is > 5 and < 80) turbo1Mhz = one * 100.0;
        }

        // ---- 有效频率：逐逻辑核累加 ΔAPERF / ΔMPERF ----
        //
        // 这三条是这个指标能不能用的分界线，旧实现三条全错，于是空闲时显示 7461MHz：
        //   ① 必须用 64 位差值。APERF 在 3.6GHz 下 1.19 秒就冲掉低 32 位；旧代码把 64 位 MSR
        //      截成 (uint) 再相减，3 秒采样间隔下丢 2~3 圈计数器 → 比率被放大一倍多。
        //      （截图 7461MHz / 基准 2800MHz = 2.66×，而芯片上限只有 3.8/2.8 = 1.36×）
        //   ② 前后两次必须在**同一个核**上读。绑核失败时旧代码照样把值记进该核基线，
        //      下一次就变成"A 核的 APERF 减 B 核的 MPERF"，比率毫无意义。
        //   ③ 要**求和**而不是挑一个核。单个核可能深睡（C-state 里计数器几乎不走），
        //      那里的比率是"噪声除噪声"。全芯片求和后的比率 = 全核平均实际频率，物理意义清楚。
        ulong sumDa = 0, sumDm = 0;
        double dtSum = 0;
        int usedLp = 0;

        ulong coreMask = AllowedCoreMask();
        int coreCount = 0;
        for (int b = 0; b < MaxLp; b++) if ((coreMask >> b & 1UL) != 0) coreCount++;
        bool needPin = coreCount > 1 && Environment.Is64BitProcess;

        for (int lp = 0; lp < MaxLp; lp++)
        {
            if ((coreMask >> lp & 1UL) == 0) continue;   // 不在允许集里的核不碰

            UIntPtr oldMask = UIntPtr.Zero;
            bool ok = true;
            if (needPin)
            {
                oldMask = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << lp));
                ok = oldMask != UIntPtr.Zero;   // 0 = 失败，此时读到的是"碰巧所在核"
            }
            try
            {
                if (!ok) continue;
                if (!_msr.TryRead(MSR_APERF, out ulong aperf) ||
                    !_msr.TryRead(MSR_MPERF, out ulong mperf)) continue;

                if (_lastAt[lp] == default)
                {
                    _lastAperf[lp] = aperf; _lastMperf[lp] = mperf; _lastAt[lp] = now;
                    continue;                   // 首轮只建基线
                }

                ulong da = aperf - _lastAperf[lp];   // 64 位无符号差值，回绕安全
                ulong dm = mperf - _lastMperf[lp];
                double dt = (now - _lastAt[lp]).TotalSeconds;

                _lastAperf[lp] = aperf; _lastMperf[lp] = mperf; _lastAt[lp] = now;

                // 该核在这个窗口里几乎没跑（深 C-state）：分母太小，比率是噪声 → 丢弃
                if (dt <= 0.2 || dm < BaseMhz * 1e6 * dt * 0.01) continue;

                sumDa += da; sumDm += dm; dtSum += dt; usedLp++;
            }
            finally
            {
                if (needPin && ok && oldMask != UIntPtr.Zero)
                    SetThreadAffinityMask(GetCurrentThread(), oldMask);
            }
        }

        // 频率上限：芯片最高频率。读不到 0x1AD 时用「基准×1.5 与当前请求倍频取大」兜底，
        // 这样即使拿不到规格表，也不会把合法的频率判成"超过上限"。
        double maxEffMhz = turbo1Mhz > 0
            ? turbo1Mhz
            : Math.Max(BaseMhz * 1.5, curMhz > 0 ? curMhz : BaseMhz);
        double effMhz = 0, c0Mhz = 0;
        string effWhy = usedLp == 0 ? "本轮没有任何核拿到可用差值（首次采样需两轮）" : "";
        double dtEff = dtSum / Math.Max(1, usedLp);
        // MPERF 实测推进速率：正常应等于基准频率。偏差大说明主板 P1 或注册表 ~MHz 不准 ——
        // 这不影响有效频率（它只用 ΔAPERF/Δt），但值得写出来，免得下次又怀疑计数器。
        double impliedBaseMhz = usedLp > 0 ? sumDm / Math.Max(0.001, dtEff) / 1e6 : 0;
        bool effValid = usedLp > 0 &&
                        TryEffectiveMhz(BaseMhz, sumDa, sumDm, dtEff, maxEffMhz,
                                        out effMhz, out c0Mhz, out effWhy);

        // 热状态位：0x64B 是 per-core（在刚绑过的核上读），0x1B1 是 package
        bool thermal = false, prochot = false;
        if (_msr.TryRead(MSR_THERM_STATUS, out ulong th))
        {
            thermal |= (th & 0x1) != 0;          // Thermal Status
            prochot |= (th & 0x4) != 0;          // PROCHOT# 有效
        }
        if (_msr.TryRead(MSR_PKG_THERM_STATUS, out ulong pth))
        {
            thermal |= (pth & 0x1) != 0;         // Package Thermal Status
            prochot |= (pth & 0x4) != 0;
        }

        // RAPL 包功率
        double pkgW = 0;
        if (double.IsNaN(_energyUnitJ) && _msr.TryRead(MSR_RAPL_POWER_UNIT, out ulong ru))
        {
            int exp = (int)((ru >> 8) & 0x1F);   // bits[12:8] = energy unit 的 2 的负幂
            _energyUnitJ = 1.0 / (1 << exp);
        }
        if (_msr.TryRead(MSR_PKG_ENERGY_STATUS, out ulong e))
        {
            uint cur = (uint)e;
            if (_lastEnergyAt != DateTime.MinValue && pkgW >= 0)
            {
                double dt = (now - _lastEnergyAt).TotalSeconds;
                uint de = (uint)(cur - _lastEnergy);
                if (dt > 0.2 && dt < 60 && de < 0x8000_0000 && !double.IsNaN(_energyUnitJ))
                    pkgW = de * _energyUnitJ / dt;
            }
            _lastEnergy = cur;
            _lastEnergyAt = now;
        }

        // ---- 判定限制原因（顺序即优先级：温度墙 > 功率墙 > 轻载 > 频率上限 > 无）
        //
        // 顺序的修正（v1.9.2）：
        //   · 「轻载」必须在「频率上限」之前。空闲时 IA32_PERF_STATUS 里的**请求倍频**
        //     常常还停在最高档（本机空闲报 3.5~3.8GHz），旧顺序会把空闲误判成"频率上限"，
        //     于是用户看到"1% 负载，频率顶在上限"这种自相矛盾的结论。
        //   · 「频率上限」只在真的有负载（≥30%）时才成立：轻载时倍频高只说明"没降频"，
        //     不代表"想要更快但被倍频卡住"。
        bool powerPinned = pl1W > 1 && pkgW > 1 && pkgW >= pl1W * 0.97;
        bool atCeiling = thermalCeilingC > 1 && tempC >= thermalCeilingC - 1.0;
        bool freqCapped = turboMhz > 0 && curMhz > 0 && curMhz >= turboMhz - 1;
        bool lightLoad = loadPct < 30 && (pl1W <= 1 || pkgW < pl1W * 0.6);

        BindingLimit limit;
        string text, evidence;
        if (thermal || prochot)
        {
            limit = BindingLimit.Thermal;
            text = "温度墙（已触发降频位）";
            evidence = $"DTS 位{(thermal ? "=1" : "=0")}/PROCHOT{(prochot ? "=1" : "=0")}，{tempC:F0}℃（上限 {thermalCeilingC:F0}℃）";
        }
        else if (atCeiling && tempC > 0)
        {
            limit = BindingLimit.Thermal;
            text = "温度墙（贴住温度上限）";
            evidence = $"{tempC:F0}℃ ≥ 上限 {thermalCeilingC:F0}℃ − 1℃";
        }
        else if (freqCapped && loadPct >= 30)
        {
            // 频率已经顶到全核睿频上限 → 这才是"性能已经到顶"的真实原因。
            // 必须排在功率墙之前：此时包功率通常也贴着 PL1（因为 CPU 吃满了），
            // 如果先判功率墙，就会得出"加 PL1 还能更快"的错误结论，把功率往上抬，
            // 结果只是更热、频率不变。本机实测 40W 时已是 3.41GHz（全核上限 3.40）。
            limit = BindingLimit.FrequencyCap;
            text = "频率上限（已顶到全核睿频上限，加功率不会更快）";
            evidence = $"倍频 {curMhz:F0}MHz ≥ 全核上限 {turboMhz:F0}MHz"
                     + (turbo1Mhz > turboMhz ? $"（单核上限 {turbo1Mhz:F0}MHz，四核满载到不了）" : "")
                     + (powerPinned ? $"，包功率 {pkgW:F1}W 也贴着 PL1 {pl1W:F0}W" : "");
        }
        else if (powerPinned)
        {
            limit = BindingLimit.Power;
            text = "功率墙（包功率顶在 PL1，频率还没到上限）";
            evidence = $"包功率 {pkgW:F1}W ≥ PL1 {pl1W:F0}W × 97%，倍频 {curMhz:F0} < 上限 {turboMhz:F0}MHz";
        }
        else if (lightLoad)
        {
            limit = BindingLimit.Idle;
            text = "轻载（需求低于任何限制）";
            evidence = $"负载 {loadPct:F0}% · 包功率 {pkgW:F1}W" + (pl1W > 1 ? $"（PL1 {pl1W:F0}W）" : "");
        }
        else
        {
            limit = BindingLimit.None;
            text = "未受限（加功率还有性能可换）";
            evidence = $"包功率 {pkgW:F1}W" + (pl1W > 1 ? $" < PL1 {pl1W:F0}W" : "")
                     + $"，{tempC:F0}℃ < 上限 {thermalCeilingC:F0}℃";
        }

        var detail = "";
        if (effValid) detail += $"有效 {effMhz:F0}MHz（墙钟平均） · 醒着 {c0Mhz:F0}MHz（与任务管理器同口径）";
        if (effValid && Math.Abs(impliedBaseMhz - BaseMhz) > BaseMhz * 0.15)
            detail += $"（MPERF 实测推进 {impliedBaseMhz:F0}MHz，注册表基准 {BaseMhz:F0}MHz，不影响计算）";
        if (curMhz > 0) detail += (detail.Length > 0 ? " · " : "") + $"倍频 {curMhz:F0}MHz";
        if (pkgW > 1) detail += (detail.Length > 0 ? " · " : "") + $"包功率 {pkgW:F1}W";
        if (detail.Length == 0) detail = "无读数";

        _last = new PerfSample
        {
            Valid = effValid || pkgW > 1,
            EffectiveMhz = effValid ? Math.Round(effMhz, 0) : 0,
            EffNote = effValid ? "" : effWhy,
            C0Mhz = effValid ? Math.Round(c0Mhz, 0) : 0,
            CurrentMhz = Math.Round(curMhz, 0),
            TurboLimitMhz = Math.Round(turboMhz, 0),
            TurboLimit1CoreMhz = Math.Round(turbo1Mhz, 0),
            PackagePowerW = Math.Round(pkgW, 1),
            ThermalThrottle = thermal,
            Prochot = prochot,
            PowerLimited = powerPinned,
            Limit = limit,
            LimitText = text,
            Evidence = evidence,
            Detail = detail
        };
        return _last;
    }

    /// <summary>
    /// 由 APERF/MPERF 增量算有效频率。
    ///
    /// **定义选的是「ΔAPERF / Δt」而不是「基准频率 × ΔAPERF/ΔMPERF」**，这一点很关键：
    /// MPERF 只在 C0（核心活跃）里推进，拿它当分母得到的是"C0 期间的平均频率"，
    /// 空闲时会把频率算得偏高（比如 17% 负载算出 2.4GHz）；除以实际经过的时间得到的
    /// 才是"这段时间平均每秒交付了多少个时钟周期"，也就是用户理解的"有效频率"，
    /// 它同时把"在睡觉"和"跑得慢"都算进去，与任务管理器同口径，且天然不会超过睿频上限。
    ///
    /// 顺带的好处：分母变成 Δt 之后，基准频率只用于**交叉校验**（MPERF 的推进速度不可能超过
    /// 基准频率），即便注册表里的 ~MHz 读错了，算出来的频率也不会跟着错。
    ///
    /// 抽成纯函数是为了能被自检覆盖 —— 这类 bug（32 位截断、跨核配对）在真机上只表现为
    /// "数字离谱"，只有把"3 秒窗口、计数器回绕两圈"这种输入写死进自检才不会再犯。
    /// </summary>
    /// <param name="c0Mhz">C0 期间的平均频率（辅助信息：区分"被降频"与"只是没活干"）。</param>
    public static bool TryEffectiveMhz(double baseMhz, ulong dAperf, ulong dMperf, double dtSec,
                                       double maxMhz, out double effMhz, out double c0Mhz, out string why)
    {
        effMhz = 0;
        c0Mhz = 0;
        why = "";
        if (double.IsNaN(baseMhz) || baseMhz < 200) { why = "基准频率未知"; return false; }
        if (dtSec < 0.2) { why = $"采样窗口 {dtSec:F2}s 太短"; return false; }
        if (dAperf == 0) { why = "APERF 没有推进（计数器未使能或该核全程未执行）"; return false; }
        if (dMperf == 0) { why = "MPERF 没有推进（核心全程在深 C-state）"; return false; }

        // 交叉校验（宽松版）：MPERF 只在 C0 里按基准频率推进，所以它的增量**不应该**远超
        // 基准频率×Δt。但如果主板把 P1 报得比实际低（或者注册表 ~MHz 是错的），
        // 严格卡 1.05× 会导致有效频率永远算不出来 —— 那比不显示更糟。
        // 所以只挡住"明显不是计数器"的情况（3 倍），并把偏差留给调用方做提示。
        double impliedBaseMhz = dMperf / dtSec / 1e6;
        if (impliedBaseMhz > baseMhz * 3)
        {
            why = $"MPERF 推进速率 {impliedBaseMhz:F0}MHz 超过基准频率 {baseMhz:F0}MHz 的 3 倍，判为计数错误";
            return false;
        }

        double eff = dAperf / dtSec / 1e6;      // MHz
        if (eff > maxMhz * 1.02)
        {
            why = $"算出 {eff:F0}MHz 超过芯片最高频率 {maxMhz:F0}MHz，判为计数错误";
            return false;
        }
        if (eff < 30)
        {
            why = $"窗口内几乎没执行（{eff:F1}MHz），不足以给出有效频率";
            return false;
        }

        effMhz = eff;
        c0Mhz = dMperf > 0 ? baseMhz * ((double)dAperf / dMperf) : 0;
        return true;
    }

    /// <summary>
    /// 自检：覆盖截图里的 7461MHz、32 位回绕、深睡噪声、计数器错配、过短窗口。
    /// 参数用本机实测值（基准 2800MHz、单核 3800MHz），数字对不上时一眼能看出来。
    /// </summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();
        const double baseMhz = 2800;
        const double turboMhz = 3800;

        // A) 正常运行：3.4GHz 全核满载、窗口 3 秒。
        //    增量 1.02e10 已经大于 2^32，所以这一条同时证明"64 位差值"是对的
        //    （旧代码 (uint) 截断后这一组会算出完全不同的数）。
        ulong dm = (ulong)(baseMhz * 1e6 * 3.0);
        ulong da = (ulong)(3400 * 1e6 * 3.0);
        if (!TryEffectiveMhz(baseMhz, da, dm, 3.0, turboMhz, out double f1, out double c1, out _)
            || Math.Abs(f1 - 3400) > 60)
            errors.Add($"3.4GHz 满载应算出 ≈3400MHz，实际 {f1:F0}");
        if (Math.Abs(c1 - 3400) > 60)
            errors.Add($"C0 内平均频率应≈3400MHz，实际 {c1:F0}");

        // B) 截图回归：7461MHz 必须被拦下（物理上不存在这个数）
        if (TryEffectiveMhz(baseMhz, (ulong)(7461 * 1e6 * 3.0), dm, 3.0, turboMhz, out double f2, out _, out string why2))
            errors.Add($"7461MHz 必须判无效，实际仍通过（{f2:F0}MHz）");
        else if (!why2.Contains("超过芯片最高频率"))
            errors.Add($"7461MHz 应按「超过芯片最高频率」拒绝，实际理由：{why2}");

        // C) 空闲但仍在 C0：现在按墙钟平均，必须显著低于 C0 内频率
        ulong idleDa = (ulong)(1200 * 1e6 * 3.0);      // 3 秒里平均 1.2GHz
        ulong idleDm = (ulong)(baseMhz * 1e6 * 3.0);
        if (!TryEffectiveMhz(baseMhz, idleDa, idleDm, 3.0, turboMhz, out double f3, out _, out _)
            || Math.Abs(f3 - 1200) > 60)
            errors.Add($"1.2GHz 应算出 ≈1200MHz，实际 {f3:F0}");

        // D) 深睡：计数器几乎不走 → 不能拿噪声当频率
        if (TryEffectiveMhz(baseMhz, 5_000, 8_000, 1.0, turboMhz, out double f4, out _, out _))
            errors.Add($"几乎没执行的样本必须判无效，实际算出 {f4:F1}MHz");

        // E) MPERF 推进速率离谱（32 位回绕没处理、或计数器不是 MPERF）必须拒绝。
        //    注意门槛是 3×：基准频率报得偏低（cTDP/BIOS 设置）属于正常情况，不能因此拒绝。
        if (TryEffectiveMhz(baseMhz, da, (ulong)(baseMhz * 1e6 * 3.0 * 5.0), 3.0, turboMhz, out _, out _, out string why5))
            errors.Add("MPERF 推进超上限必须判无效");
        else if (!why5.Contains("MPERF"))
            errors.Add($"MPERF 异常的拒绝理由不清楚：{why5}");

        // F) 窗口太短
        if (TryEffectiveMhz(baseMhz, 3_000_000, 1_000_000, 0.05, turboMhz, out _, out _, out _))
            errors.Add("0.05 秒窗口必须判无效");

        // G) 单核 3.8GHz 必须通过（不能把合法的单核 turbo 判成错误）
        ulong oneDa = (ulong)(3800 * 1e6 * 3.0);
        ulong oneDm = (ulong)(baseMhz * 1e6 * 3.0 * 0.95);
        if (!TryEffectiveMhz(baseMhz, oneDa, oneDm, 3.0, turboMhz, out double f7, out _, out _)
            || Math.Abs(f7 - 3800) > 60)
            errors.Add($"3.8GHz 单核应验证通过，实际 {f7:F0}");

        return errors;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_lastAperf);
            Array.Clear(_lastMperf);
            Array.Clear(_lastAt);
            _lastEnergyAt = DateTime.MinValue;
            _last = new PerfSample { Valid = false, Limit = BindingLimit.Unknown, LimitText = "未采样" };
        }
    }
}
