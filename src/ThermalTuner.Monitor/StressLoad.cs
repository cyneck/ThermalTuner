using ThermalTuner.Core.Stress;

namespace ThermalTuner.Monitor;

/// <summary>
/// 压测负载发生器（--stress）：按场景内核起 N 个后台线程制造真实 CPU 负载。
/// 内核实现与主程序共用 Core.Stress.StressWorker，保证两个入口的功耗特征一致。
/// </summary>
public sealed class StressLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();

    public StressLoad(int threadCount, StressKernel kernel = StressKernel.ScalarFloat,
        double dutyCycle = 1.0)
    {
        int n = Math.Clamp(threadCount, 1, Environment.ProcessorCount);
        double duty = double.IsNaN(dutyCycle) ? 1.0 : Math.Clamp(dutyCycle, 0.05, 1.0);
        if (kernel == StressKernel.Idle) return;

        var token = _cts.Token;
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() => StressWorker.Run(kernel, duty, token))
            { IsBackground = true, Priority = ThreadPriority.Normal };
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
