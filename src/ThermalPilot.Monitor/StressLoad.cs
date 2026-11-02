namespace ThermalPilot.Monitor;

/// <summary>
/// 压测负载发生器（--stress）：用 N 个后台线程跑浮点循环制造真实 CPU 负载，
/// 让温度/功耗真实上升，用于观察控制回路反应。进程退出即停止。
/// </summary>
public sealed class StressLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();

    public StressLoad(int threadCount)
    {
        int n = Math.Clamp(threadCount, 1, Environment.ProcessorCount);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() =>
            {
                double x = 1.000001;
                while (!_cts.IsCancellationRequested)
                    x = Math.Sqrt(x * 1.0000001 + 0.1);
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
            _threads.Add(t);
        }
    }

    public int ThreadCount => _threads.Count;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
