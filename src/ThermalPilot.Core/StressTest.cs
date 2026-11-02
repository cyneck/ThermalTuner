using System.Diagnostics;

namespace ThermalPilot.Core;

/// <summary>
/// 压测负载发生器：为标定提供可重复的负载条件。
/// · CPU 负载为真实多线程计算（线程数 = 逻辑核心数），可产生稳定热量；
/// · GPU 负载不做"自造"（没有通用、安全的跨厂商 GPU 压测接口），改为提示用户运行
///   FurMark / 3DMark 等外部工具，程序只负责等待与采样——这样数据才是真实的。
/// </summary>
public sealed class StressTest : IDisposable
{
    private readonly List<Thread> _threads = new();
    private volatile bool _stop;
    private static long _sink;

    public bool IsRunning { get; private set; }
    public StressKind Kind { get; private set; } = StressKind.Idle;
    public int Threads { get; private set; }

    public static string ExternalGpuHint =>
        "请手动运行 FurMark / 3DMark / 游戏等 GPU 压力程序并保持前台渲染，ThermalPilot 只负责采集数据。";

    public void Start(StressKind kind)
    {
        Stop();
        Kind = kind;
        if (kind is StressKind.Idle or StressKind.GpuExternal)
        {
            IsRunning = true;
            Log.Info($"压测阶段：{kind}（外部负载）");
            return;
        }

        _stop = false;
        Threads = Math.Max(1, Environment.ProcessorCount - (kind == StressKind.CpuGpu ? 1 : 0));
        for (int i = 0; i < Threads; i++)
        {
            var t = new Thread(Burn) { IsBackground = true, Name = $"ThermalPilot-Stress-{i}" };
            t.Start();
            _threads.Add(t);
        }
        IsRunning = true;
        Log.Info($"压测阶段：{kind}，启动 {Threads} 个计算线程。");
    }

    private void Burn()
    {
        double acc = 0;
        int i = 0;
        while (!_stop)
        {
            // 混合算术 + 开方，避免被 JIT 优化掉，产生可复现的稳定热负载
            acc += Math.Sqrt(i * 1.0000011) + Math.Sin(i * 0.000017);
            if ((++i & 0xFFFF) == 0)
            {
                Interlocked.Exchange(ref _sink, BitConverter.DoubleToInt64Bits(acc));
                acc = 0;
            }
        }
        Interlocked.Exchange(ref _sink, BitConverter.DoubleToInt64Bits(acc));
    }

    public void Stop()
    {
        if (_threads.Count == 0)
        {
            IsRunning = false;
            return;
        }
        _stop = true;
        foreach (var t in _threads)
        {
            try { t.Join(1500); } catch { }
        }
        _threads.Clear();
        IsRunning = false;
        Log.Info("压测负载已停止。");
    }

    public void Dispose() => Stop();
}
