using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ThermalPilot.Core.Stress;

/// <summary>
/// 压测负载内核实现（纯托管、无第三方依赖、不访问任何 MSR / EC / 电源计划）。
/// 放在 Core 是为了让主程序、Monitor CLI、自检探针共用同一套负载，避免不同入口跑出不同功耗特征。
///
/// 覆盖的执行单元：整数 ALU、标量浮点、AVX2/FMA 向量单元、内存子系统、混合轮换、占空比脉冲。
/// 只跑一种标量循环会严重低估机器可达功耗，因此场景库必须组合这些内核。
/// </summary>
public static class StressWorker
{
    // 静态吸收槽：让计算结果产生可观测副作用，防止 JIT 把纯计算循环整体优化掉。
    private static double _sinkFloat;
    private static ulong _sinkInt;

    public static bool SimdAvailable => Fma.IsSupported && Avx.IsSupported;

    /// <summary>按指定内核持续施加负载，直到 token 被取消。</summary>
    public static void Run(StressKernel kernel, double dutyCycle, CancellationToken token)
    {
        double duty = double.IsNaN(dutyCycle) ? 1.0 : Math.Clamp(dutyCycle, 0.05, 1.0);

        switch (kernel)
        {
            case StressKernel.ScalarInt:
                SpinScalarInt(token);
                break;
            case StressKernel.ScalarFloat:
                SpinScalarFloat(token);
                break;
            case StressKernel.SimdFma:
                SpinSimd(token);
                break;
            case StressKernel.MemoryStream:
                SpinMemory(token);
                break;
            case StressKernel.Mixed:
                SpinMixed(token);
                break;
            case StressKernel.Pulse:
                SpinPulse(token, duty);
                break;
            case StressKernel.Idle:
            default:
                // 空载基线：不占 CPU，轮询等待取消（不访问 WaitHandle，避免 Dispose 竞态）
                while (!token.IsCancellationRequested) Thread.Sleep(50);
                break;
        }
    }

    // ---------------- 整数 ALU ----------------

    private static void SpinScalarInt(CancellationToken token)
    {
        ulong x = 0x9E3779B97F4A7C15UL;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 200_000; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                x = x * 6364136223846793005UL + 1442695040888963407UL;
            }
            Volatile.Write(ref _sinkInt, x);
        }
    }

    // ---------------- 标量浮点 ----------------

    private static void SpinScalarFloat(CancellationToken token)
    {
        double x = 1.0;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 50_000; i++)
            {
                x = Math.Sqrt(x * 1.0000001 + (i & 7));
                if (x > 1e6 || double.IsNaN(x)) x = 1.0;
            }
            Volatile.Write(ref _sinkFloat, x);
        }
    }

    // ---------------- SIMD / FMA ----------------

    private static void SpinSimd(CancellationToken token)
    {
        if (SimdAvailable) SpinFma256(token);
        else if (Vector.IsHardwareAccelerated) SpinVector(token);
        else SpinScalarFloat(token);
    }

    private static void SpinFma256(CancellationToken token)
    {
        var mul = Vector256.Create(1.0000001, 1.0000002, 1.0000003, 1.0000004);
        var add = Vector256.Create(0.0000001, 0.0000002, 0.0000003, 0.0000004);
        var seed = Vector256.Create(1.0, 2.0, 3.0, 4.0);
        var acc = seed;

        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 100_000; i++)
            {
                acc = Fma.MultiplyAdd(acc, mul, add);
                if ((i & 2047) == 0) acc = seed;   // 复位，避免指数增长溢出成 Inf
            }
            Volatile.Write(ref _sinkFloat, acc.GetElement(0));
        }
    }

    private static void SpinVector(CancellationToken token)
    {
        var mul = new Vector<double>(1.0000001);
        var add = new Vector<double>(0.0000001);
        var seed = new Vector<double>(1.0);
        var acc = seed;

        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 100_000; i++)
            {
                acc = acc * mul + add;
                if ((i & 2047) == 0) acc = seed;
            }
            Volatile.Write(ref _sinkFloat, acc[0]);
        }
    }

    // ---------------- 内存带宽 ----------------

    private static void SpinMemory(CancellationToken token)
    {
        const int size = 8 * 1024 * 1024;   // 每工作线程 8 MiB，通常超过 LLC
        var buf = new byte[size];
        for (int i = 0; i < size; i += 64) buf[i] = (byte)(i & 0xFF);

        long sum = 0;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < size; i += 64)
            {
                buf[i] = (byte)(buf[i] + 1);
                sum += buf[i];
            }
            Volatile.Write(ref _sinkInt, (ulong)sum);
        }
    }

    // ---------------- 混合轮换 ----------------

    private static void SpinMixed(CancellationToken token)
    {
        double x = 1.0;
        ulong u = 1;
        long sum = 0;
        var buf = new byte[2 * 1024 * 1024];
        var mul = Vector256.Create(1.0000001, 1.0000002, 1.0000003, 1.0000004);
        var add = Vector256.Create(0.0000001, 0.0000002, 0.0000003, 0.0000004);
        var seed = Vector256.Create(1.0, 2.0, 3.0, 4.0);

        while (!token.IsCancellationRequested)
        {
            long mode = (System.Environment.TickCount64 / 200) % 4;

            if (mode == 3 && SimdAvailable)
            {
                var acc = seed;
                for (int i = 0; i < 20_000; i++)
                {
                    acc = Fma.MultiplyAdd(acc, mul, add);
                    if ((i & 2047) == 0) acc = seed;
                }
                Volatile.Write(ref _sinkFloat, acc.GetElement(0));
                continue;
            }

            for (int i = 0; i < 30_000; i++)
            {
                x = Math.Sqrt(x * 1.0000001 + (i & 7));
                u = u * 6364136223846793005UL + 1442695040888963407UL;
                if (mode == 2)
                {
                    int j = (int)((u >> 8) & (ulong)(buf.Length - 1));
                    buf[j] = (byte)(buf[j] + 1);
                    sum += buf[j];
                }
            }
            if (x > 1e6 || double.IsNaN(x)) x = 1.0;
            Volatile.Write(ref _sinkFloat, x + (u & 0xFFFF) + sum);
        }
    }

    // ---------------- 占空比脉冲 ----------------

    private static void SpinPulse(CancellationToken token, double duty)
    {
        int workMs = Math.Max(5, (int)Math.Round(100 * duty));
        int sleepMs = Math.Max(1, 100 - workMs);

        while (!token.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < workMs && !token.IsCancellationRequested)
                PulseBurst();
            if (token.IsCancellationRequested) break;
            Thread.Sleep(sleepMs);
        }
    }

    private static void PulseBurst()
    {
        if (SimdAvailable)
        {
            var mul = Vector256.Create(1.0000001, 1.0000002, 1.0000003, 1.0000004);
            var add = Vector256.Create(0.0000001, 0.0000002, 0.0000003, 0.0000004);
            var acc = Vector256.Create(1.0, 2.0, 3.0, 4.0);
            for (int i = 0; i < 4096; i++)
                acc = Fma.MultiplyAdd(acc, mul, add);
            Volatile.Write(ref _sinkFloat, acc.GetElement(0));
            return;
        }

        double x = 1.0;
        for (int i = 0; i < 4096; i++)
            x = Math.Sqrt(x * 1.0000001 + (i & 7));
        Volatile.Write(ref _sinkFloat, x);
    }
}
