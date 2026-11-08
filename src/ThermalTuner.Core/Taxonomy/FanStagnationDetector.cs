using System;

namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// EC 原始值恒定检测：区分「随负载/温度真实变化的读数」与「恒定不变的档位刻度或残留值」。
///
/// 设计边界（重要）：
/// · 只在**真正完成一次 EC 读**时调用 Observe；采集层的 2.2s 缓存命中不得调用，
///   否则界面刷新次数会被误当成新的采样次数，恒定判定会提前触发。
/// · 判定结果只是「疑似非实时」的提示，不是把读数判为无效，更不是物理转速 RPM。
///   UI 必须继续显示原始字节，由人判断这个寄存器到底是不是风扇状态。
/// · 只读语义：本类纯计算，不访问任何端口、不写 EC。
/// </summary>
public sealed class FanStagnationDetector
{
    /// <summary>默认连续 10 次真实采样不变即提示；按 2.2s 采集周期约 22 秒。</summary>
    public const int DefaultThreshold = 10;

    public int Threshold { get; }

    /// <summary>连续「与上一次相同」的采样次数。首次有效读为 0。</summary>
    public int Consecutive { get; private set; }

    public bool IsStagnant => Consecutive >= Threshold;

    private double _last = double.NaN;

    public FanStagnationDetector(int threshold = DefaultThreshold)
    {
        Threshold = Math.Max(2, threshold);
    }

    /// <summary>清空历史。切换数据源或读失败时调用，避免恢复后出现误判。</summary>
    public void Reset()
    {
        _last = double.NaN;
        Consecutive = 0;
    }

    /// <summary>
    /// 观察一次真实 EC 原始值。NaN（本次没有拿到原始字节，例如只读到服务换算后的百分比）
    /// 视为数据源中断，直接重置而不是累加。
    /// </summary>
    /// <param name="raw">EC 寄存器原始字节数值。</param>
    /// <param name="epsilon">判定「未变化」的容差；原始值是整数字节，默认 0.5。</param>
    public void Observe(double raw, double epsilon = 0.5)
    {
        if (double.IsNaN(raw))
        {
            Reset();
            return;
        }

        Consecutive = !double.IsNaN(_last) && Math.Abs(raw - _last) <= Math.Abs(epsilon)
            ? Consecutive + 1
            : 0;
        _last = raw;
    }
}
