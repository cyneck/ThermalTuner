namespace ThermalPilot.Core;

/// <summary>
/// 温度抗毛刺滤波（5 点滑动中值）。
///
/// 为什么要它 —— 见 lab-data/lab-deep1-20261028-191903.csv（真机 707 行，1 s 采样）：
///   704 个有效样本里有 43 个「孤立尖点」（单拍偏离前后均值 ≥ 8℃），占 6.1%；
///   最极端一次 47 → 85 → 52℃，但同一拍的功率只从 14.5 W 变成 15.4 W。
///   按本机标定的 R=1.21 K/W，涨 38℃ 需要额外 31 W，实际只有 0.9 W —— 物理上不成立。
///   中位幅度 9.5℃ 的尖点对应功率增量中位数是 −0.5 W（不但没涨，还略降）。
///   结论：这些不是真实温升，是**读数层面的单点离群值**（DTS 经 Ring0 偶发读到非温度值）。
///
/// 为什么不是「采样太频繁」：
///   采样慢只会**漏掉**尖峰，不会制造尖峰。1 s 采样对 500 ms 的突发已经有 50% 概率漏掉；
///   再降频只会让偶发高点更容易被"最后一帧"撞上，观感更糟。
///
/// 为什么用中值而不是平均（EMA）：
///   平均会把 85℃ 摊进后面几拍（47, 85, 52 → 均值 61，之后 3 秒都偏高），
///   中值直接把它当异常点丢掉（47, 85, 52 → 52）。升温/降温的真实阶跃中值也跟得上：
///   实测压测开始 41 → 96 → 97，中值第 3 拍就到 96℃，不损失真实信息。
///
/// 代价：输出滞后约 1 个窗口（1 s 采样 × 5 点 → 约 2 s）。对风扇（3 s 才写一次 EC）
/// 和 PI（时间常数 τ_hs≈130 s）完全无影响；只有超温保护的响应被推后 1~2 s，
/// 所以在 ThermalEngine 里另加了「原始值连续 2 拍越线」的安全阀。
/// </summary>
public sealed class TempFilter
{
    public const int DefaultWindow = 5;

    /// <summary>物理上不可能出现的读数（DTS 原始值越界 / 读到 0），直接丢弃不污染窗口。</summary>
    private const double MinPlausibleC = 1.0;
    private const double MaxPlausibleC = 125.0;

    private readonly double[] _ring;
    private readonly double[] _scratch;
    private int _count;
    private int _head;

    /// <summary>滤波后的温度（℃）。首帧 / 全部样本无效时为 NaN。</summary>
    public double Value { get; private set; } = double.NaN;

    /// <summary>最近一次被丢掉的离群读数与原值的差（℃），供界面解释"刚才那个尖是毛刺"。</summary>
    public double LastRejectedDeltaC { get; private set; }

    /// <summary>累计丢掉多少个离群读数（诊断用）。</summary>
    public int RejectedCount { get; private set; }

    public int Window => _ring.Length;

    public TempFilter(int window = DefaultWindow)
    {
        if (window < 3) window = 3;
        if ((window & 1) == 0) window++;          // 中值必须奇数窗口
        _ring = new double[window];
        _scratch = new double[window];
    }

    public void Reset()
    {
        _count = 0;
        _head = 0;
        Value = double.NaN;
        LastRejectedDeltaC = 0;
        RejectedCount = 0;
    }

    /// <summary>
    /// 推入一个原始读数，返回滤波后的温度。
    /// raw ≤ 0 或越界时**不更新窗口**，直接返回上一次的滤波值（保持最后有效值）。
    /// </summary>
    public double Push(double raw)
    {
        if (!(raw >= MinPlausibleC && raw <= MaxPlausibleC)) return Value;

        LastRejectedDeltaC = 0;

        _ring[_head] = raw;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;

        // 窗口内取中值：这是唯一真正"抗单点毛刺"的算子。
        // 窗口未满时取「偏低」的那个中位（count=2 取较小值），宁可晚一拍跟上升温，
        // 也不让刚启动那一两拍的单点毛刺漏到界面上。
        Array.Copy(_ring, _scratch, _count);
        var span = _scratch.AsSpan(0, _count);
        span.Sort();

        double med = span[(_count - 1) / 2];

        // 诊断：只有窗口填满后才判断"这一拍是不是离群"，否则首帧的 63℃ 会被误报成毛刺。
        if (_count >= 3 && Math.Abs(raw - med) >= 8.0)
        {
            LastRejectedDeltaC = raw - med;
            RejectedCount++;
        }

        Value = med;
        return Value;
    }

    /// <summary>
    /// 自检：用真机那批尖点的形态验证「毛刺被吃掉、真实阶跃被保住」。
    /// </summary>
    public static List<string> SelfTest()
    {
        var errs = new List<string>();

        // ① 单点毛刺：47 → 85 → 52（真机 D4 段原样）
        var f = new TempFilter();
        double o1 = f.Push(47); double o2 = f.Push(85); double o3 = f.Push(52);
        if (o2 >= 80) errs.Add($"单点毛刺没滤掉：47→85→52 输出 {o2:F0}℃");
        if (Math.Abs(o3 - 52) > 1) errs.Add($"毛刺后没回到真实值：期望 52℃，实得 {o3:F0}℃");
        if (!(o1 > 0)) errs.Add("首帧应直接可用");

        // ② 交替毛刺：47 → 68 → 47 → 85 → 52（真机 D4 段 27~32 s）
        //    3 点中值在这里会漏（窗口 68,47,85 中值 = 68），5 点才吃得掉。
        f = new TempFilter();
        foreach (var v in new[] { 47.0, 68, 47, 85, 52 }) f.Push(v);
        if (f.Value >= 60) errs.Add($"交替毛刺没滤掉：47→68→47→85→52 末值 {f.Value:F0}℃");
        if (Math.Abs(f.Value - 52) > 1) errs.Add($"交替毛刺后应回到 52℃，实得 {f.Value:F0}℃");

        // ③ 真实阶跃必须跟上：41 → 96 → 97 → 98（压测开始）
        f = new TempFilter();
        foreach (var v in new[] { 41.0, 41, 41 }) f.Push(v);
        f.Push(96); f.Push(97);
        double after = f.Push(98);
        if (after < 96) errs.Add($"真实升温被削弱：压测 41→96→97→98 只到 {after:F0}℃");

        // ④ 真实掉载降温也要跟上：96 ×3 → 58 → 66 → 52 → 50（压测结束）
        //    5 点中值翻向需要 3 个新样本，即滞后 3 s —— 这是窗口长度的固有代价，
        //    对风扇（3 s 写一次 EC）与 PI（τ_hs≈130 s）无影响，故断言到 80℃ 以内即可。
        f = new TempFilter();
        foreach (var v in new[] { 96.0, 96, 96 }) f.Push(v);
        f.Push(58); f.Push(66); f.Push(52); f.Push(50);
        if (f.Value > 80) errs.Add($"真实降温被延迟过头：96→58→66→52→50 仍是 {f.Value:F0}℃");

        // ⑤ 无效读数不能污染窗口
        f = new TempFilter();
        f.Push(47); f.Push(47); f.Push(47);
        f.Push(0); f.Push(-5); f.Push(double.NaN);
        if (Math.Abs(f.Value - 47) > 0.01) errs.Add($"0/负值/NaN 污染了滤波值：{f.Value:F1}℃");
        if (f.Push(48) > 60) errs.Add("无效读数之后窗口未恢复");

        // ⑥ 稳态不能被中值改变
        f = new TempFilter();
        for (int i = 0; i < 12; i++) f.Push(66);
        if (Math.Abs(f.Value - 66) > 0.01) errs.Add($"稳态 66℃ 被改成 {f.Value:F1}℃");

        // ⑦ 偶数/过小窗口要自动纠正为奇数
        if (new TempFilter(4).Window != 5) errs.Add("窗口 4 未纠正为 5");
        if (new TempFilter(1).Window != 3) errs.Add("窗口 1 未纠正为 3");

        return errs;
    }
}
