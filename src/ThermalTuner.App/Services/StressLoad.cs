using ThermalTuner.Core.Stress;

namespace ThermalTuner.App.Services;

/// <summary>
/// 压测负载发生器：按场景指定负载内核、线程数与占空比，制造不同功耗特征的真实 CPU 负载。
/// 内核实现统一放在 Core.Stress.StressWorker，主程序、Monitor CLI 与自检探针共用同一份，
/// 避免不同入口跑出不同功耗特征。纯托管、普通权限可运行，不写任何 MSR / EC / 电源计划。
/// </summary>
public sealed class StressLoad : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();

    public StressKernel Kernel { get; }
    public int Threads { get; }
    public double DutyCycle { get; }

    public StressLoad(int threads, StressKernel kernel = StressKernel.ScalarFloat,
        double dutyCycle = 1.0)
    {
        Threads = Math.Max(1, threads);
        Kernel = kernel;
        DutyCycle = double.IsNaN(dutyCycle) ? 1.0 : Math.Clamp(dutyCycle, 0.05, 1.0);
        if (kernel == StressKernel.Idle) return;   // 空载基线不起工作线程

        var token = _cts.Token;
        var k = Kernel;
        var duty = DutyCycle;
        for (int i = 0; i < Threads; i++)
            _workers.Add(Task.Run(() => StressWorker.Run(k, duty, token), token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_workers.ToArray(), 500); } catch { /* 忽略取消引发的聚合异常 */ }
        _cts.Dispose();
    }
}
