using System;
using System.Collections.Generic;
using System.Linq;

namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// 一个热事件的研究埋点记录（v0.5.0 P1 研究基础设施）。每个真实触顶事件落一行 CSV。
/// 本期**不做自适应冷却公式**：本机 1001 个历史压测样本最高只有 78°C，零个 ≥85°C 的触顶事件，
/// 无法拟合「超温幅度 → 恢复时长」。先采数，等 H1（超温幅度能预测二次触顶）验证通过再换策略。
/// </summary>
public sealed record ThermalEventRecord(
    DateTime StartedAt,
    double PeakC,
    double CeilingC,
    double OvershootC,
    int StepDownCount,
    double StepDownWatts,
    double EventDurationS,
    double FallRateP50,
    double FallRateP95,
    double CooldownSeconds,
    int SecondaryTouches60s,
    string EndReason);

/// <summary>
/// 热事件观测器：纯计算，不落盘、不写寄存器。调用方每拍把「温度 / 天花板 / 已下发 PL1 台阶」
/// 喂进来，事件结束后取回一条记录写 CSV。
///
/// 状态：Idle → Active（温度 ≥ 天花板）→ Recovery（回落到天花板 −2°C 以下）→ Idle。
/// Recovery 阶段固定观察 60 秒，专门统计「恢复后 60s 内二次触顶次数」，这是未来验证
/// 自适应冷却公式的关键标签。
/// </summary>
public sealed class ThermalEventRecorder
{
    public const double RecoveryGapC = 2.0;
    public const double ObserveWindowS = 60.0;
    public const double MaxEventDurationS = 600.0;

    private enum Phase { Idle, Active, Recovery }

    private Phase _phase = Phase.Idle;
    private DateTime _startedAt;
    private DateTime _recoveryAt;
    private double _peakC;
    private double _ceilingC;
    private int _stepDownCount;
    private double _stepDownWatts;
    private int _lastPl1Step;
    private int _secondaryTouches;
    private readonly List<(DateTime At, double TempC)> _temps = new();

    public bool IsActive => _phase != Phase.Idle;
    public string PhaseText => _phase switch
    {
        Phase.Active => "触顶中",
        Phase.Recovery => "恢复观察 60s",
        _ => "空闲"
    };

    /// <summary>每拍观察。返回非 null 表示有一个事件已完结，调用方应把它写入 CSV。</summary>
    public ThermalEventRecord? Observe(double tempC, double ceilingC, int appliedPl1Step, DateTime utcNow)
    {
        if (double.IsNaN(tempC) || double.IsNaN(ceilingC))
        {
            // 温度源中断：不伪造事件数据，正在进行的事件按「数据中断」收尾。
            return _phase == Phase.Idle ? null : Finalize("温度读数中断", utcNow);
        }

        _temps.Add((utcNow, tempC));

        switch (_phase)
        {
            case Phase.Idle:
                if (tempC >= ceilingC) Begin(tempC, ceilingC, appliedPl1Step, utcNow);
                break;

            case Phase.Active:
                _peakC = Math.Max(_peakC, tempC);
                _ceilingC = Math.Max(_ceilingC, ceilingC);
                if (appliedPl1Step < _lastPl1Step)
                {
                    _stepDownCount++;
                    _stepDownWatts += _lastPl1Step - appliedPl1Step;
                }
                _lastPl1Step = appliedPl1Step;

                if (tempC < ceilingC - RecoveryGapC)
                {
                    _phase = Phase.Recovery;
                    _recoveryAt = utcNow;
                }
                else if ((utcNow - _startedAt).TotalSeconds >= MaxEventDurationS)
                {
                    return Finalize("事件超时（10 分钟）", utcNow);
                }
                break;

            case Phase.Recovery:
                if (tempC >= ceilingC) _secondaryTouches++;
                if ((utcNow - _recoveryAt).TotalSeconds >= ObserveWindowS)
                    return Finalize("恢复观察完成", utcNow);
                break;
        }

        return null;
    }

    /// <summary>程序退出时把未完结的事件收尾，避免丢样本（Windows 关机会直接终止进程）。</summary>
    public ThermalEventRecord? Flush(DateTime utcNow) =>
        _phase == Phase.Idle ? null : Finalize("会话结束", utcNow);

    private void Begin(double tempC, double ceilingC, int appliedPl1Step, DateTime utcNow)
    {
        _phase = Phase.Active;
        _startedAt = utcNow;
        _recoveryAt = utcNow;
        _peakC = tempC;
        _ceilingC = ceilingC;
        _stepDownCount = 0;
        _stepDownWatts = 0;
        _lastPl1Step = appliedPl1Step;
        _secondaryTouches = 0;
        _temps.Clear();
        _temps.Add((utcNow, tempC));
    }

    private ThermalEventRecord Finalize(string reason, DateTime utcNow)
    {
        var rates = FallRates();
        var record = new ThermalEventRecord(
            _startedAt,
            Math.Round(_peakC, 1),
            Math.Round(_ceilingC, 1),
            Math.Round(Math.Max(0, _peakC - _ceilingC), 1),
            _stepDownCount,
            Math.Round(_stepDownWatts, 1),
            Math.Round((utcNow - _startedAt).TotalSeconds, 1),
            Math.Round(Percentile(rates, 0.50), 2),
            Math.Round(Percentile(rates, 0.95), 2),
            Math.Round(_phase == Phase.Recovery ? (utcNow - _recoveryAt).TotalSeconds : 0, 1),
            _secondaryTouches,
            reason);

        _phase = Phase.Idle;
        _temps.Clear();
        return record;
    }

    /// <summary>连续样本之间的降温速率（°C/s，取负值部分），用于将来拟合冷却因子。</summary>
    private List<double> FallRates()
    {
        var rates = new List<double>();
        for (int i = 1; i < _temps.Count; i++)
        {
            double dt = (_temps[i].At - _temps[i - 1].At).TotalSeconds;
            if (dt <= 0 || dt > 30) continue;
            double dT = _temps[i].TempC - _temps[i - 1].TempC;
            if (dT < 0) rates.Add(-dT / dt);
        }
        return rates;
    }

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        double idx = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
