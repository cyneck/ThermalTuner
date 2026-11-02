using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermalPilot.Core;

/// <summary>同名进程聚合后的一行负载。</summary>
public sealed class ProcLoad
{
    public string Name { get; init; } = "";
    /// <summary>同名实例数（Chrome 这类会把工作拆到几十个进程）。</summary>
    public int Instances { get; init; }
    /// <summary>该组里功耗最大的实例 PID，其余列在后面（供悬浮提示）。</summary>
    public string PidText { get; init; } = "";

    /// <summary>按 CPU 时间份额分摊到的包功率（W）。是估算，不是独立测量。</summary>
    public double PowerW { get; init; }
    /// <summary>占整机 CPU 的百分比：全核跑满 = 100（与任务管理器同口径）。</summary>
    public double CpuPct { get; init; }
    /// <summary>工作集合计（MB）。同名进程会重复计算共享页，因此偏高。</summary>
    public double MemMb { get; init; }
    /// <summary>文件读写合计（MB/s）。走 IO 计数器，缓存命中也计入。</summary>
    public double DiskMBps { get; init; }
}

/// <summary>一次进程采样的汇总。</summary>
public sealed class ProcSummary
{
    public List<ProcLoad> Rows { get; init; } = new();
    public double TotalPowerW { get; init; }
    public double TotalCpuPct { get; init; }
    public double TotalMemMb { get; init; }
    public double TotalDiskMBps { get; init; }
    public int ProcessCount { get; init; }
    /// <summary>false = 没有包功率可用（无 MSR 权限），排序退化为纯 CPU。</summary>
    public bool PowerAvailable { get; init; }
}

/// <summary>
/// 找出「谁在烧电」。
///
/// 为什么需要它：温度只是结果，真正推动温度的是**某个进程在持续吃 CPU**。
/// 温控程序如果只告诉你 96℃，你还是不知道该关谁；把 top10 按功耗排出来，
/// 才知道是游戏、是浏览器、还是某个后台索引服务。
///
/// 四个量分别怎么来的（都写明是估算还是实测，别把估算当实测）：
///
///   1) CPU%   —— GetProcessTimes 差分 / (Δt × 逻辑核数)。口径与任务管理器一致：
///                全核跑满 = 100%，所以单线程满载在 8 核机上是 12.5%。
///   2) 功耗    —— **分摊估算**：包功率 × CPU 时间份额。RAPL 只给整颗 CPU 的功率，
///                硬件层面不存在「每进程功耗」这种计数器。可加性是对的
///                （份额之和 = 整机 CPU 使用率，所以合计 ≈ 包功率 × 使用率），
///                差额是空闲 / 核显 / uncore 的部分。
///   3) 内存    —— WorkingSetSize 合计。同名进程会重复计共享页，因此偏高，看相对值。
///   4) 磁盘IO  —— GetProcessIoCounters 的读+写字节数差分。这是**逻辑 IO**，
///                缓存命中也会计入，不等于物理磁盘吞吐。
///
/// 采样成本：约 100–300 个进程各开一次句柄，合计 3–10 ms，放后台线程 2 秒一次完全无感。
/// </summary>
public sealed class ProcessMonitor
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;

        public readonly ulong TotalBytes => ReadTransferCount + WriteTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit,
                                               out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters io);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr h, out MemCounters mem, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    private sealed class Prev
    {
        public long Creation;
        public long CpuTicks;
        public ulong IoBytes;
    }

    private readonly Dictionary<int, Prev> _prev = new();
    private long _prevTs;

    /// <summary>采样是否已经有基线（第一次调用只建基线，返回空表）。</summary>
    public bool HasBaseline => _prevTs != 0;

    /// <summary>丢弃基线（停止/恢复后调用，避免出现跨越暂停区间的大尖峰）。</summary>
    public void Reset()
    {
        _prev.Clear();
        _prevTs = 0;
    }

    /// <summary>
    /// 采一次并按「功耗 → CPU → 内存 → 磁盘IO」排出 top N。
    /// packagePowerW ≤ 0 时（无 MSR 权限）功耗列全 0，排序自然退化为纯 CPU。
    /// </summary>
    public ProcSummary Sample(double packagePowerW, int top = 10)
    {
        int nCores = Math.Max(1, Environment.ProcessorCount);
        long ts = Stopwatch.GetTimestamp();
        double dt = _prevTs == 0 ? 0 : (ts - _prevTs) / (double)Stopwatch.Frequency;
        _prevTs = ts;

        var raw = new List<ProcRaw>();
        var next = new Dictionary<int, Prev>();

        foreach (var p in Process.GetProcesses())
        {
            int pid;
            string name;
            try
            {
                pid = p.Id;
                name = p.ProcessName;
            }
            catch
            {
                p.Dispose();
                continue;
            }
            // 名字已经取到本地变量；Process 对象只用来做枚举，句柄立即释放
            p.Dispose();

            // pid 0 是 Idle（代表"没跑东西"），列出来只会顶掉真正吃电的进程
            if (pid <= 0 || string.IsNullOrEmpty(name)) continue;

            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            bool full = h != IntPtr.Zero;
            if (!full) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) continue;

            try
            {
                if (!GetProcessTimes(h, out long created, out _, out long kern, out long usr)) continue;
                long cpu = kern + usr;   // 100ns 单位

                GetProcessIoCounters(h, out var io);
                ulong ioBytes = io.TotalBytes;

                double wsMb = 0;
                if (full)
                {
                    var mc = new MemCounters { cb = (uint)Marshal.SizeOf<MemCounters>() };
                    if (K32GetProcessMemoryInfo(h, out mc, mc.cb))
                        wsMb = mc.WorkingSetSize.ToUInt64() / 1048576.0;
                }

                var cur = new Prev { Creation = created, CpuTicks = cpu, IoBytes = ioBytes };
                next[pid] = cur;

                double cpuPct = 0, ioMBps = 0;
                // 进程 PID 会被复用：creation 时间不一致就当成新进程，第一次不算增量
                if (dt > 0.05 && _prev.TryGetValue(pid, out var old) && old.Creation == created)
                {
                    long dCpu = cpu - old.CpuTicks;
                    if (dCpu < 0) dCpu = 0;
                    // dCpu(100ns) / (dt 秒 × 1e7) = 折算到单核的核数，再除以逻辑核数换成整机百分比
                    cpuPct = Math.Clamp(dCpu / (dt * 10_000_000.0) / nCores * 100.0, 0, 100);
                    ulong dIo = ioBytes >= old.IoBytes ? ioBytes - old.IoBytes : 0;
                    ioMBps = dIo / dt / 1048576.0;
                }

                raw.Add(new ProcRaw(name, pid, cpuPct, wsMb, ioMBps));
            }
            finally
            {
                CloseHandle(h);
            }
        }

        // 只保留还活着的进程的基线，防止长时间运行后字典膨胀
        _prev.Clear();
        foreach (var kv in next) _prev[kv.Key] = kv.Value;

        // 第一次采样只建立基线：没有 Δt 就算不出用量，硬凑一张「按内存排」的表会误导人
        if (dt <= 0.05) return new ProcSummary { PowerAvailable = packagePowerW > 0.5 };
        return Aggregate(raw, packagePowerW, top);
    }

    /// <summary>采样到的单进程原始增量，聚合前的中间形态（自检也用它）。</summary>
    public readonly record struct ProcRaw(string Name, int Pid, double CpuPct, double MemMb, double DiskMBps);

    /// <summary>
    /// 按进程名合并（Chrome / Edge 会把工作拆成几十个子进程，不合并的话
    /// 第一名永远是某个 3% 的 renderer，而真正的大头被摊薄到看不见），
    /// 然后按 功耗 → CPU → 内存 → IO 排序取前 top 名。
    ///
    /// 功耗要在**聚合前**按实例份额分摊再相加，这样合计正好等于整机使用率那一份；
    /// 聚合后再乘会重复计算。
    /// </summary>
    public static ProcSummary Aggregate(IEnumerable<ProcRaw> raw, double packagePowerW, int top = 10)
    {
        bool powerOk = packagePowerW > 0.5;
        var groups = new Dictionary<string, List<ProcRaw>>(StringComparer.OrdinalIgnoreCase);
        double sumCpu = 0, sumMem = 0, sumIo = 0;
        int count = 0;

        foreach (var r in raw)
        {
            if (r.CpuPct <= 0 && r.MemMb <= 0) continue;
            count++;
            sumCpu += r.CpuPct;
            sumMem += r.MemMb;
            sumIo += r.DiskMBps;
            if (!groups.TryGetValue(r.Name, out var list)) groups[r.Name] = list = new List<ProcRaw>();
            list.Add(r);
        }

        var rows = new List<ProcLoad>(groups.Count);
        double sumPower = 0;

        foreach (var kv in groups)
        {
            var list = kv.Value;
            double cpu = list.Sum(x => x.CpuPct);
            double mem = list.Sum(x => x.MemMb);
            double io = list.Sum(x => x.DiskMBps);
            // 功耗 = 包功率 × CPU 份额。cpu 已经是「整机百分比」，所以份额就是 cpu/100
            double power = powerOk ? packagePowerW * Math.Clamp(cpu, 0, 100) / 100.0 : 0;
            sumPower += power;

            var ordered = list.OrderByDescending(x => x.CpuPct).ThenBy(x => x.Pid).ToList();
            string pids = ordered.Count <= 3
                ? string.Join(" / ", ordered.Select(x => x.Pid))
                : string.Join(" / ", ordered.Take(3).Select(x => x.Pid)) + $" …共 {ordered.Count} 个";

            rows.Add(new ProcLoad
            {
                Name = kv.Key,
                Instances = list.Count,
                PidText = pids,
                PowerW = power,
                CpuPct = cpu,
                MemMb = mem,
                DiskMBps = io
            });
        }

        var topRows = rows
            .OrderByDescending(r => r.PowerW)
            .ThenByDescending(r => r.CpuPct)
            .ThenByDescending(r => r.MemMb)
            .ThenByDescending(r => r.DiskMBps)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, top))
            .ToList();

        return new ProcSummary
        {
            Rows = topRows,
            TotalPowerW = sumPower,
            TotalCpuPct = sumCpu,
            TotalMemMb = sumMem,
            TotalDiskMBps = sumIo,
            ProcessCount = count,
            PowerAvailable = powerOk
        };
    }

    // ---------------- 自检 ----------------

    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 排序主键：功耗
        var a = new List<ProcRaw>
        {
            new("low", 1, 20, 100, 0),
            new("high", 2, 8, 50, 0),
            new("mid", 3, 40, 10, 0),
        };
        var s = Aggregate(a, 100);
        if (s.Rows.Count != 3) errors.Add($"聚合后应有 3 行，实际 {s.Rows.Count}");
        else if (s.Rows[0].Name != "mid" || s.Rows[1].Name != "low" || s.Rows[2].Name != "high")
            errors.Add("功耗排序错误：" + string.Join(",", s.Rows.Select(r => r.Name)));

        // B) 功耗相同 → 比 CPU；再相同 → 比内存；再相同 → 比 IO
        var b = new List<ProcRaw>
        {
            new("mem", 1, 0, 800, 0),
            new("io", 2, 0, 100, 40),
            new("cpu", 3, 5, 10, 0),
        };
        // 三个都是 0 功耗（包功率给 0），CPU 只有 cpu 有值 → cpu 第一；其余两个 CPU 都是 0，比内存 → mem 第二
        var sb = Aggregate(b, 0);
        if (sb.Rows[0].Name != "cpu") errors.Add($"CPU 次序错误，实际首位 {sb.Rows[0].Name}");
        if (sb.Rows[1].Name != "mem") errors.Add($"内存次序错误，实际第二 {sb.Rows[1].Name}");
        if (sb.PowerAvailable) errors.Add("包功率为 0 时不应声称功耗可用");

        // C) 同名聚合 + 实例数 + PID 列表
        var c = new List<ProcRaw>
        {
            new("chrome", 11, 6, 120, 1),
            new("chrome", 12, 4, 80, 2),
            new("notepad", 13, 1, 10, 0),
        };
        var sc = Aggregate(c, 20);
        var ch = sc.Rows.FirstOrDefault(r => r.Name == "chrome");
        if (ch == null) errors.Add("同名进程没有聚合出 chrome");
        else
        {
            if (ch.Instances != 2) errors.Add($"chrome 实例数应为 2，实际 {ch.Instances}");
            if (Math.Abs(ch.CpuPct - 10) > 0.001) errors.Add($"chrome CPU 合计应为 10，实际 {ch.CpuPct:F2}");
            if (Math.Abs(ch.MemMb - 200) > 0.001) errors.Add($"chrome 内存合计应为 200，实际 {ch.MemMb:F1}");
            // 20W × 10% = 2.0W
            if (Math.Abs(ch.PowerW - 2.0) > 0.001) errors.Add($"chrome 分摊功耗应为 2.0W，实际 {ch.PowerW:F2}");
        }
        if (sc.Rows.Count != 2) errors.Add($"聚合后应只剩 2 行，实际 {sc.Rows.Count}");
        if (sc.TotalCpuPct < 10.99 || sc.TotalCpuPct > 11.01) errors.Add($"CPU 合计应为 11，实际 {sc.TotalCpuPct:F2}");

        // D) top N 截断
        var many = Enumerable.Range(1, 30).Select(i => new ProcRaw("p" + i, i, i, i, 0)).ToList();
        var sd = Aggregate(many, 50, top: 10);
        if (sd.Rows.Count != 10) errors.Add($"top 10 应只保留 10 行，实际 {sd.Rows.Count}");
        if (sd.Rows[0].Name != "p30") errors.Add($"top 首位应为 p30，实际 {sd.Rows[0].Name}");

        // E) 真实采样：第一次建基线返回空，第二次能拿到数据且各项在物理范围内
        try
        {
            var mon = new ProcessMonitor();
            var first = mon.Sample(30, 10);
            if (first.Rows.Count != 0) errors.Add("首次采样应只建基线、不返回数据");
            Thread.Sleep(700);
            var real = mon.Sample(30, 10);
            double cpuCap = 100 * Math.Max(1, Environment.ProcessorCount) * 1.05;
            if (real.TotalCpuPct > cpuCap)
                errors.Add($"进程 CPU 合计 {real.TotalCpuPct:F1}% 超过物理上限 {cpuCap:F0}%");
            if (real.TotalPowerW > 30 * 1.05)
                errors.Add($"分摊功耗合计 {real.TotalPowerW:F1}W 超过包功率 30W");
            if (real.ProcessCount < 10)
                errors.Add($"只识别到 {real.ProcessCount} 个进程，明显偏少");
            if (real.Rows.Any(r => r.MemMb < 0 || r.PowerW < 0 || r.DiskMBps < 0))
                errors.Add("出现了负值");
            if (real.Rows.Count > 10) errors.Add("真实采样超过了 top 10");
        }
        catch (Exception ex)
        {
            errors.Add("真实采样抛异常：" + ex.Message);
        }

        return errors;
    }
}
