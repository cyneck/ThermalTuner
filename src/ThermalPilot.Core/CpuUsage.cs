using System.Runtime.InteropServices;

namespace ThermalPilot.Core;

/// <summary>
/// 系统级 CPU 使用率（0–100，恒不超过 100）。
///
/// 为什么不用监控库的 "CPU Total"？
/// Windows 性能计数器里存在两个语义不同的量：
///   · % Processor Time    —— 时间占比，理论上限 100%；
///   · % Processor Utility —— "有效算力利用率"，会按当前频率/睿频折算，
///                            Turbo 或节能策略下 **可以超过 100%**（这是官方定义的行为）。
/// 部分监控库在某些平台/版本上取的是后者（或把逻辑处理器累加），于是出现 148% 这种读数。
/// 这里改为直接对 GetSystemTimes 做差分（内核态 + 用户态 - 空闲），物理上限就是 100%，
/// 与本机计数器实现无关，跨机型一致。
/// </summary>
public sealed class CpuUsageCounter
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;
    private DateTime _lastAt = DateTime.MinValue;
    private double _last = -1;

    /// <summary>
    /// 统计窗口下限（毫秒）。低于它就返回上一次的结果，而不是重新开一个窗口。
    ///
    /// 为什么必须有它：Read() 会被**多个调用方**按不同节奏调用（引擎 tick 约 1Hz、
    /// UI 刷新约 4~5Hz）。旧实现在每次调用时都重设基线，于是实际窗口被压到 200~250ms，
    /// 再加上 EMA 的滞后，读数与任务管理器/OEM 面板能差十几个百分点
    /// （截图为证：我们 25%、OMEN Hub 39%）。固定成 ≥1 秒的窗口后，两边口径才一致。
    /// </summary>
    public const int MinWindowMs = 1000;

    /// <summary>最近一次的结果（0–100）；窗口未到时直接复用它。</summary>
    public double Smooth => _last < 0 ? 0 : _last;

    /// <summary>使用率（0–100），按 ≥1 秒的窗口计算；调用过频时返回上一次的值。</summary>
    public double Next()
    {
        var now = DateTime.UtcNow;
        if (_hasPrev && (now - _lastAt).TotalMilliseconds < MinWindowMs && _last >= 0)
            return _last;                       // 窗口没到，别把基线提前挪走

        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return _last < 0 ? 0 : _last;

        ulong i = idle.ToUInt64(), k = kernel.ToUInt64(), u = user.ToUInt64();
        if (!_hasPrev)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u; _hasPrev = true; _lastAt = now;
            return 0;
        }

        ulong di = i - _prevIdle;
        ulong dk = (k - _prevKernel) + (u - _prevUser);   // 注意：内核时间已包含空闲时间
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        _lastAt = now;

        if (dk == 0) return _last < 0 ? 0 : _last;

        double busy = dk > di ? (double)(dk - di) : 0;
        _last = Math.Clamp(busy / dk * 100.0, 0, 100);
        return _last;
    }

    /// <summary>重置基线（暂停/恢复后避免出现尖峰）。</summary>
    public void Reset()
    {
        _hasPrev = false;
        _lastAt = DateTime.MinValue;
        _last = -1;
    }
}
