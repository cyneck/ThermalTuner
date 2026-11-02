using System.Diagnostics;

namespace ThermalPilot.App.Services;

/// <summary>
/// 压测负载：用满 CPU 制造真实热量，用于标定与演示。
/// 纯托管实现（无第三方依赖），可在普通权限下工作。
/// </summary>
public sealed class StressLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();

    public StressLoad(int threads)
    {
        for (int i = 0; i < Math.Max(1, threads); i++)
            _workers.Add(Task.Run(() => Spin(_cts.Token)));
    }

    private static void Spin(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        double x = 1.0;
        while (!token.IsCancellationRequested)
        {
            // 混合浮点与整数运算，避免被编译器优化掉
            for (int i = 0; i < 50_000; i++)
            {
                x = Math.Sqrt(x * 1.0000001 + i % 7);
                if (x > 1e6) x = 1.0;
            }
            if (sw.ElapsedMilliseconds > 60) sw.Restart();
        }
        GC.KeepAlive(x);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_workers.ToArray(), 500); } catch { /* 忽略 */ }
        _cts.Dispose();
    }
}
