using System;
using System.Collections.Generic;

namespace ThermalTuner.Core.Taxonomy;

/// <summary>下发请求的来源通道。事件通道是用户显式意图，自动通道是决策树每拍的连续演算值。</summary>
public enum WriteChannel
{
    /// <summary>决策树每拍（1Hz）：严格受阶梯迟滞与升档冷却约束。</summary>
    AutoTick,
    /// <summary>智能微调页选中档位：跳过迟滞，但仍受升档冷却与硬闸。</summary>
    ProfileEvent,
    /// <summary>手工微调页「保存并下发」：用户显式写入，跳过迟滞。</summary>
    ManualSave,
    /// <summary>开机重放已持久化的手工参数。</summary>
    StartupReplay
}

public enum WriteVerdict
{
    /// <summary>允许立即下发（调用方负责回读校验与审计）。</summary>
    Write,
    /// <summary>本次不下发，只演算；不弹确认框。</summary>
    Skip,
    /// <summary>需要用户二次确认后才能下发（升档冷却 / 跨档升功率）。</summary>
    NeedConfirmation,
    /// <summary>硬红线或熔断，二次确认也不能解除。</summary>
    HardBlocked
}

/// <summary>一次下发请求。目标 PL1/PL2/Tau 都是决策树演算值，不是寄存器实读值。</summary>
public sealed record WriteRequest(
    WriteChannel Channel,
    double TargetPl1W,
    double TempC,
    DateTime UtcNow,
    /// <summary>写入闸门（管理员 + 驱动 + 显式授权）是否全开。</summary>
    bool GatesOpen = true,
    /// <summary>PROCHOT / 安全上限：达到后「只降不升」是唯一不可解除硬闸。</summary>
    double HardCeilingC = 95.0);

/// <summary>调度结论。StepPl1W 是量化后真正要写入的 1W 台阶。</summary>
public sealed record WriteDecision(
    WriteVerdict Verdict,
    int StepPl1W,
    string Reason,
    double CooldownRemainingS,
    bool IsRampUp,
    bool IsThermalStepDown);

/// <summary>
/// 阶梯式下发调度器（v0.5.0）。纯计算、不访问硬件、不落盘，方便单测覆盖全部节流规则。
///
/// 背景：写 0x610 是改 RAPL 的 PL1 平均功率预算，PCU 需要按 Tau（本机 16~32s）重新收敛。
/// 决策树每拍输出连续瓦数，若按 3s 冷却 + Δ≥1W 直接写，温度噪声会让 PL1 在两个值间
/// 来回跳（chattering），控制环永远不收敛。这里把连续值量化成 1W 台阶，并只对「升档」
/// 施加迟滞与冷却；高温降档立即放行，安全优先。
///
/// 通道约定：
/// · AutoTick：连续 3 拍同台阶 + 升档冷却，冷却内直接 Skip（不打扰用户）。
/// · ProfileEvent / ManualSave / StartupReplay：跳过迟滞；升档冷却内返回 NeedConfirmation，
///   由 UI 弹二次确认，用户确认后调用 ForceWrite 兜底放行，保证「选中即生效」不被静默吞掉。
/// · 所有通道共享每分钟写入上限（熔断），高温升档硬闸不可由二次确认解除。
/// </summary>
public sealed class WriteScheduler
{
    public const double DefaultCooldownSeconds = 20.0;
    public const int DefaultHysteresisTicks = 3;
    public const int DefaultMaxWritesPerMinute = 4;
    public const double MinCooldownSeconds = 5.0;
    public const double MaxCooldownSeconds = 120.0;

    /// <summary>升档冷却秒数，设置页可调；默认 20s（本机实测热惯性 5~15s + 余量）。</summary>
    public double CooldownSeconds { get; set; } = DefaultCooldownSeconds;
    public int HysteresisTicks { get; }
    public int MaxWritesPerMinute { get; }

    /// <summary>上次成功写入的量化台阶；null = 本次会话尚未写过。</summary>
    public int? LastAppliedStep { get; private set; }
    public DateTime LastWriteUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastRampUpUtc { get; private set; } = DateTime.MinValue;
    public int CandidateTicks { get; private set; }
    public int? CandidateStep { get; private set; }
    public int WritesInLastMinute => Prune(LastWriteUtc).Count;
    public bool Tripped { get; private set; }

    private readonly double _minPl1W;
    private readonly double _maxPl1W;
    private readonly Queue<DateTime> _writes = new();

    public WriteScheduler(double minPl1W, double maxPl1W,
        double cooldownSeconds = DefaultCooldownSeconds,
        int hysteresisTicks = DefaultHysteresisTicks,
        int maxWritesPerMinute = DefaultMaxWritesPerMinute)
    {
        _minPl1W = minPl1W;
        _maxPl1W = Math.Max(minPl1W, maxPl1W);
        CooldownSeconds = Math.Clamp(cooldownSeconds, MinCooldownSeconds, MaxCooldownSeconds);
        HysteresisTicks = Math.Max(1, hysteresisTicks);
        MaxWritesPerMinute = Math.Max(1, maxWritesPerMinute);
    }

    /// <summary>评估一拍请求，不修改状态（除迟滞候选计数）。真正写入后必须调用 Commit。</summary>
    public WriteDecision Evaluate(WriteRequest req)
    {
        int step = Quantize(req.TargetPl1W);
        bool rampUp = LastAppliedStep is { } last && step > last;
        bool stepDown = LastAppliedStep is { } prev && step < prev;
        // 高温降档：温度超过天花板且目标功率低于当前台阶，立即放行，不受冷却/迟滞。
        bool thermalDown = stepDown && !double.IsNaN(req.TempC) && req.TempC >= req.HardCeilingC - 3.0;
        double remaining = CooldownRemaining(req.UtcNow);

        if (!req.GatesOpen)
            return Decide(WriteVerdict.HardBlocked, step, rampUp, thermalDown, remaining,
                "写入闸门未全开（管理员 / PawnIO 驱动 / 显式授权）；本次只演算");

        // 熔断优先于其它判定，且二次确认不可解除。
        if (Tripped || Prune(req.UtcNow).Count >= MaxWritesPerMinute)
        {
            Tripped = true;
            return Decide(WriteVerdict.HardBlocked, step, rampUp, thermalDown, remaining,
                $"写入熔断：最近 60 秒已达 {MaxWritesPerMinute} 次上限，自动下发已停止（需重置调度器）");
        }

        // 硬红线：接近硬件上限时只降不升。二次确认不覆盖这一条。
        if (rampUp && !double.IsNaN(req.TempC) && req.TempC >= req.HardCeilingC)
            return Decide(WriteVerdict.HardBlocked, step, rampUp, thermalDown, remaining,
                $"温度 {req.TempC:F1}°C ≥ 安全上限 {req.HardCeilingC:F0}°C：只降不升（不可由二次确认解除）");

        // 高温降档：立即写，冷却与迟滞全部让路。
        if (thermalDown)
            return Decide(WriteVerdict.Write, step, rampUp, thermalDown, remaining,
                "高温降档：立即下发，不受升档冷却与迟滞约束");

        bool isAuto = req.Channel == WriteChannel.AutoTick;

        // 会话首次下发（还没有台阶基准）：写一次建立基准，避免自动通道永远跳过。
        if (!rampUp && !stepDown && LastAppliedStep is null)
            return Decide(WriteVerdict.Write, step, rampUp, thermalDown, remaining,
                $"首次建立台阶基准：下发 {step}W 并回读校验");

        // 台阶未变：自动通道直接跳过（避免反复写同一个值）；事件通道仍写一次，
        // 因为那是用户显式意图，需要一次「写入 + 回读」的确定性反馈。
        if (!rampUp && !stepDown && isAuto)
        {
            CandidateStep = null;
            CandidateTicks = 0;
            return Decide(WriteVerdict.Skip, step, rampUp, thermalDown, remaining,
                $"PL1 台阶未变（{step}W）：不重复下发");
        }

        // 自动通道升档：连续 N 拍同台阶才算真实趋势，滤掉温度噪声。
        if (rampUp && isAuto)
        {
            if (CandidateStep == step) CandidateTicks++;
            else { CandidateStep = step; CandidateTicks = 1; }

            if (CandidateTicks < HysteresisTicks)
                return Decide(WriteVerdict.Skip, step, rampUp, thermalDown, remaining,
                    $"升档迟滞 {CandidateTicks}/{HysteresisTicks} 拍：等待同一台阶稳定");
        }

        // 升档冷却：只约束升档（RAPL 需要按 Tau 收敛）；降档与同台阶不受影响。
        if (rampUp && remaining > 0)
        {
            string reason = $"升档冷却中（剩余 {remaining:F0}s / 设定 {CooldownSeconds:F0}s）";
            return isAuto
                ? Decide(WriteVerdict.Skip, step, rampUp, thermalDown, remaining, reason + "：自动通道等待")
                : Decide(WriteVerdict.NeedConfirmation, step, rampUp, thermalDown, remaining,
                    reason + "：用户确认后可强制下发一次");
        }

        string ok = rampUp ? $"升档 {LastAppliedStep}W → {step}W，允许下发"
            : stepDown ? $"降档 {LastAppliedStep}W → {step}W，允许下发"
            : $"显式下发当前台阶 {step}W（回读校验）";
        return Decide(WriteVerdict.Write, step, rampUp, thermalDown, remaining, ok);
    }

    /// <summary>
    /// 二次确认后的强制放行。只绕过升档冷却与迟滞；闸门、高温只降不升、熔断仍然生效。
    /// </summary>
    public WriteDecision ForceWrite(WriteRequest req)
    {
        int step = Quantize(req.TargetPl1W);
        bool rampUp = LastAppliedStep is { } last && step > last;
        double remaining = CooldownRemaining(req.UtcNow);

        if (!req.GatesOpen)
            return Decide(WriteVerdict.HardBlocked, step, rampUp, false, remaining,
                "写入闸门未全开：确认后仍无法写硬件，本次只演算");
        if (Tripped || Prune(req.UtcNow).Count >= MaxWritesPerMinute)
            return Decide(WriteVerdict.HardBlocked, step, rampUp, false, remaining,
                "写入熔断中：二次确认不能解除每分钟写入上限");
        if (rampUp && !double.IsNaN(req.TempC) && req.TempC >= req.HardCeilingC)
            return Decide(WriteVerdict.HardBlocked, step, rampUp, false, remaining,
                $"温度 {req.TempC:F1}°C ≥ 安全上限 {req.HardCeilingC:F0}°C：只降不升（硬红线）");

        return Decide(WriteVerdict.Write, step, rampUp, false, remaining,
            $"用户二次确认已放行（冷却剩余 {remaining:F0}s）：立即下发 {step}W");
    }

    /// <summary>真正写入并回读成功后调用：推进台阶、冷却基准、迟滞与熔断计数。</summary>
    public void Commit(int stepPl1W, DateTime utcNow, bool success = true)
    {
        if (!success)
        {
            // 写入失败不推进冷却基准，避免下次重算被已失败的写入挡住。
            CandidateStep = null;
            CandidateTicks = 0;
            return;
        }

        bool rampUp = LastAppliedStep is { } last && stepPl1W > last;
        LastAppliedStep = stepPl1W;
        LastWriteUtc = utcNow;
        if (rampUp) LastRampUpUtc = utcNow;
        Prune(utcNow);
        _writes.Enqueue(utcNow);
        CandidateStep = null;
        CandidateTicks = 0;
    }

    /// <summary>清除迟滞与熔断（模式切换、设置项变更、用户手动重置）。</summary>
    public void Reset()
    {
        CandidateStep = null;
        CandidateTicks = 0;
        Tripped = false;
        _writes.Clear();
    }

    /// <summary>外部已知寄存器被其它路径改写（开机重放、恢复原值）时同步台阶，避免首拍误判升降。</summary>
    public void SeedAppliedStep(double pl1W, DateTime utcNow)
    {
        LastAppliedStep = Quantize(pl1W);
        LastWriteUtc = utcNow;
        LastRampUpUtc = DateTime.MinValue;   // 重放不算升档，不启动升档冷却
        CandidateStep = null;
        CandidateTicks = 0;
    }

    public double CooldownRemaining(DateTime utcNow)
    {
        if (LastRampUpUtc == DateTime.MinValue) return 0;
        return Math.Max(0, CooldownSeconds - (utcNow - LastRampUpUtc).TotalSeconds);
    }

    private int Quantize(double watts)
    {
        if (double.IsNaN(watts) || double.IsInfinity(watts)) return (int)Math.Round(_minPl1W);
        return (int)Math.Round(Math.Clamp(watts, _minPl1W, _maxPl1W), MidpointRounding.AwayFromZero);
    }

    private Queue<DateTime> Prune(DateTime utcNow)
    {
        while (_writes.Count > 0 && (utcNow - _writes.Peek()).TotalSeconds > 60)
            _writes.Dequeue();
        return _writes;
    }

    private static WriteDecision Decide(WriteVerdict verdict, int step, bool rampUp,
        bool thermalDown, double remaining, string reason) =>
        new(verdict, step, reason, remaining, rampUp, thermalDown);
}
