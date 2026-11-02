using System.Management;

namespace ThermalPilot.Core;

/// <summary>CPU 基本信息与降压可行性判定。</summary>
public sealed class CpuInfo
{
    public string Name { get; set; } = "未知";
    public string Manufacturer { get; set; } = "未知";
    public int Cores { get; set; }
    public int LogicalProcessors { get; set; }
    public bool IsIntel => Manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd => Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Name}";
}

/// <summary>
/// CPU 电压微调（undervolt）——等价 ThrottleStop 的 FIVR Voltage Offset。
/// 原理：写 Intel OC Mailbox（MSR 0x150），经 LibreHardwareMonitor 自带内核驱动完成 ring0 访问。
///
/// 风险与限制（务必阅读）：
///   1) 10 代及以后 Intel + 已修复 Plundervolt 的 BIOS 基本会直接忽略写入（读回校验会失败）；
///   2) 开启内存完整性(HVCI)/核心隔离时，第三方驱动被禁止加载，功能不可用；
///   3) 该 offset 是易失的，重启即失效，因此每次启动需重新下发；
///   4) 写入后立刻读回校验，不匹配则视为失败。
/// </summary>
public sealed class UndervoltService
{
    private const uint MsrOcMailbox = 0x150;
    /// <summary>写使能位（不同平台有 0x1/0x2 两种编码，按失败自动尝试）。</summary>
    private static readonly ulong[] EnableBits = { 1UL << 20, 2UL << 20 };

    private readonly MsrAccess _msr = new();

    public CpuInfo Cpu { get; } = ReadCpu();
    public bool IsApplied { get; private set; }
    public int AppliedMv { get; private set; }
    public string LastMessage { get; private set; } = "未尝试";
    public bool MsrAvailable => _msr.Available;

    public static bool HvciEnabled => Privilege.HvciEnabled;
    public static bool IsElevated => Privilege.IsElevated;

    public bool Supported => Cpu.IsIntel && !Cpu.IsAmd;

    private static CpuInfo ReadCpu()
    {
        var info = new CpuInfo();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                info.Name = (mo["Name"] as string)?.Trim() ?? "未知";
                info.Manufacturer = (mo["Manufacturer"] as string)?.Trim() ?? "未知";
                info.Cores = Convert.ToInt32(mo["NumberOfCores"] ?? 0);
                info.LogicalProcessors = Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? 0);
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 CPU 信息失败：{ex.Message}");
        }
        return info;
    }

    /// <summary>探测内核驱动的 MSR 接口（幂等）。</summary>
    public bool Probe() => _msr.Probe();

    /// <summary>下发电压偏移（单位 mV，负值降压）。成功返回 true。</summary>
    public bool Apply(int offsetMv)
    {
        offsetMv = Math.Clamp(offsetMv, -150, 0);

        if (!Supported)
        {
            LastMessage = "仅支持 Intel 平台（AMD 请用 BIOS PBO Curve Optimizer）";
            return false;
        }
        if (!IsElevated)
        {
            LastMessage = "需要管理员权限";
            return false;
        }
        if (HvciEnabled)
        {
            LastMessage = "系统已开启内存完整性(HVCI)，内核驱动被禁止加载，降压不可用";
            return false;
        }
        if (!Probe())
        {
            LastMessage = "MSR 不可用：" + _msr.LastError;
            return false;
        }

        foreach (ulong enable in EnableBits)
        {
            ulong value = Encode(offsetMv, enable);
            if (!_msr.TryWrite(MsrOcMailbox, value)) continue;

            if (!_msr.TryRead(MsrOcMailbox, out ulong readBack)) continue;
            if (readBack != 0 && (readBack & 0xFFE00000UL) == (value & 0xFFE00000UL))
            {
                IsApplied = true;
                AppliedMv = offsetMv;
                LastMessage = $"已下发 {offsetMv} mV（读回 0x{readBack:X16} 校验通过）";
                Log.Info("降压：" + LastMessage);
                return true;
            }
        }

        IsApplied = false;
        LastMessage = "写入被 BIOS 拒绝或读回不匹配（常见于 10 代以后 Intel / 未开放 OC Mailbox）";
        Log.Warn("降压失败：" + LastMessage);
        return false;
    }

    /// <summary>读取当前 OC Mailbox 里的电压偏移（mV）。不可用返回 null。</summary>
    public int? ReadCurrentMv()
    {
        if (!Probe()) return null;
        if (!_msr.TryRead(MsrOcMailbox, out ulong v) || v == 0) return null;

        long raw = (long)((v >> 21) & 0x7FF);
        if ((raw & 0x400) != 0) raw -= 0x800;      // 11 位补码
        return (int)Math.Round(raw * 1000.0 / 1024.0);
    }

    /// <summary>回零（退出时必须调用）。</summary>
    public void Restore()
    {
        if (!IsApplied) return;
        try
        {
            for (int i = 0; i < 2; i++) _msr.TryWrite(MsrOcMailbox, 0);
            IsApplied = false;
            Log.Info("电压偏移已回零。");
        }
        catch (Exception ex)
        {
            Log.Error("回零电压偏移失败", ex);
        }
    }

    /// <summary>
    /// 编码：MSR 0x150 的 [31:21] 为 11 位补码电压偏移，单位 1/1024 V；[20] 为写使能位。
    /// （Intel OC Mailbox 的电压 offset 命令格式，不同平台使能位可能是 1 或 2，故上面两种都试。）
    /// </summary>
    private static ulong Encode(int offsetMv, ulong enableBit)
    {
        long units = (long)Math.Round(offsetMv * 1024.0 / 1000.0);
        ulong field = (ulong)(units & 0x7FF) << 21;
        return field | enableBit;
    }
}
