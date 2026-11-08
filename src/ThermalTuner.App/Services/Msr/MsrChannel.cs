using System.Security.Principal;
using ThermalTuner.Core.Msr;

namespace ThermalTuner.App.Services.Msr;

public enum MsrStatus
{
    /// <summary>PawnIO 驱动未安装（设备对象不存在）。</summary>
    NotInstalled,
    /// <summary>驱动已装但设备打不开 / 模块加载失败。</summary>
    DriverUnavailable,
    /// <summary>缺少管理员权限（PawnIO 设备 ACL 只允许 SYSTEM 与 Administrators）。</summary>
    NoAdmin,
    /// <summary>模块缺失或为只读版本（不含 ioctl_write_msr）。</summary>
    ModuleUnavailable,
    /// <summary>可读，但不可写（固件锁定或写入被拒）。</summary>
    ReadOnly,
    /// <summary>可读可写。</summary>
    Writable
}

/// <summary>
/// MSR 访问通道 —— **PawnIO 后端**（v0.5.1 起唯一后端）。
///
/// 为什么废弃 WinRing0：
///   OpenLibSys 的 WinRing0 已停止维护，1.2.0.5（2008 年编译）是最终版本，
///   且漏洞是设计性的（设备 ACL 宽松 + 无调用者校验，暴露任意 MSR 写与物理内存读写，
///   CVE-2023-1048）。它被列入微软易受攻击驱动名单，Defender / 火绒会按哈希删除，
///   产品无法依赖它长期可用，因此不再保留降级路径。
///
/// PawnIO 的不同：
///   驱动本体由微软 WHQL 签名（Microsoft Windows Hardware Compatibility Publisher），
///   只执行**已签名白名单模块**；实际硬件操作由模块内的白名单函数完成，
///   调用方拿不到「任意 MSR / 任意物理内存」这种能力。
///   IntelMSR 模块的写白名单包含 MSR_PKG_POWER_LIMIT(0x610)、MSR_OC_MAILBOX(0x150)、
///   MSR_VR_CURRENT_CONFIG、MSR_MISC_FEATURE_CONTROL —— 本项目所需能力均在名单内。
///
/// 能力探测顺序：管理员 → 设备句柄 → 模块加载 → 模块含写接口 → 读 0x610 → 检查 lock 位。
/// 启动时不做任何写操作；写能力由首次应用时的回读校验确认（见 MsrGuard）。
/// </summary>
public sealed class MsrChannel : IDisposable
{
    private static readonly object ProbeGate = new();
    private static bool _elevationChecked;
    private static bool _elevated;

    private PawnIoMsrBackend? _backend;

    private MsrChannel(MsrStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    public MsrStatus Status { get; private set; }
    public string Detail { get; private set; }
    public bool CanWrite => Status == MsrStatus.Writable;

    /// <summary>后端名称（诊断报告用）。</summary>
    public static string BackendName
    {
        get
        {
            var backend = PawnIoMsrBackend.Shared();
            if (backend is null) return "PawnIO（不可用）";
            return PawnIoMsrBackend.SupportsWrite
                ? $"PawnIO / IntelMSR（{PawnIoMsrBackend.ModuleSource}）"
                : $"PawnIO / 只读模块（{PawnIoMsrBackend.ModuleSource}）";
        }
    }

    /// <summary>运行期发现不可写（如模块白名单拒绝）时自我降级，UI 据此隐藏写入入口。</summary>
    public void MarkNotWritable(string reason)
    {
        Status = MsrStatus.ReadOnly;
        Detail = reason;
    }

    /// <summary>列出模块导出的函数名（诊断用，替代旧的 Ring0 方法枚举）。</summary>
    public static string[] ListFunctions()
    {
        var backend = PawnIoMsrBackend.Shared();
        return backend is null
            ? Array.Empty<string>()
            : PawnIoMsrBackend.Functions.ToArray();
    }

    private static bool IsElevated()
    {
        if (_elevationChecked) return _elevated;
        _elevationChecked = true;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            _elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { _elevated = false; }
        return _elevated;
    }

    public static MsrChannel Open()
    {
        var backend = PawnIoMsrBackend.Shared();

        if (!IsElevated())
            return new MsrChannel(MsrStatus.NoAdmin,
                backend is null
                    ? "需要管理员权限（PawnIO 设备只允许 SYSTEM 与 Administrators 访问）"
                    : "需要管理员权限才能写入 MSR（只读探测已通过）");

        if (backend is null)
        {
            string why = PawnIoMsrBackend.Failure;
            MsrStatus status = why.Contains("设备对象不存在", StringComparison.Ordinal)
                ? MsrStatus.NotInstalled
                : why.Contains("模块", StringComparison.Ordinal)
                    ? MsrStatus.ModuleUnavailable
                    : MsrStatus.DriverUnavailable;
            return new MsrChannel(status, why);
        }

        var channel = new MsrChannel(MsrStatus.ReadOnly, "已就绪") { _backend = backend };

        if (!channel.TryRead(RaplPowerLimit.MsrPkgPowerLimit, out ulong current))
        {
            return new MsrChannel(MsrStatus.DriverUnavailable,
                "读取 MSR 0x610 失败（模块已加载但寄存器不可读，可能被虚拟化拦截）");
        }

        var decoded = RaplPowerLimit.Decode(current);
        if (decoded.Locked)
            return new MsrChannel(MsrStatus.ReadOnly,
                $"0x610 的 lock 位已置位（固件锁定）：{RaplPowerLimit.Describe(current)}")
            { _backend = backend };

        // 刻意不在启动时做写探测：避免工具一启动就写 MSR。
        // 真实可写性由首次「应用」的回读校验确认，失败即自动回滚（G-03）。
        return new MsrChannel(MsrStatus.Writable,
            $"可读可写（{RaplPowerLimit.Describe(current)}；写入以回读校验确认）")
        { _backend = backend };
    }

    public bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        var backend = _backend ?? PawnIoMsrBackend.Shared();
        return backend is not null && backend.ReadMsr(msr, out value, out _);
    }

    public bool TryWrite(uint msr, ulong value) => TryWrite(msr, value, out _);

    public bool TryWrite(uint msr, ulong value, out int hresult)
    {
        hresult = PawnIoMsrBackend.HrDeviceMissing;
        var backend = _backend ?? PawnIoMsrBackend.Shared();
        if (backend is null)
        {
            LastWriteHResult = hresult;
            LastWriteReturned = 0;
            return false;
        }

        bool ok = backend.WriteMsr(msr, value, out hresult);
        LastWriteHResult = hresult;
        LastWriteReturned = 0;
        return ok;
    }

    /// <summary>最近一次写入的 HRESULT 与返回条目数（诊断用；PawnIO 后端固定 0 条）。</summary>
    public int LastWriteHResult { get; private set; }
    public nuint LastWriteReturned { get; private set; }

    /// <summary>直接执行模块函数（诊断 / 未封装寄存器用）。返回 0 表示成功，否则为 HRESULT。</summary>
    public int InvokeRaw(string function, ulong[] input) =>
        InvokeRaw(function, input, out _);

    /// <summary>直接执行模块函数并取回 ulong64 返回值。</summary>
    public int InvokeRaw(string function, ulong[] input, out ulong output)
    {
        output = 0;
        var backend = _backend ?? PawnIoMsrBackend.Shared();
        if (backend is null) return PawnIoMsrBackend.HrDeviceMissing;
        return backend.ExecuteRaw(function, input, out output);
    }

    /// <summary>读取 0x610 当前解码值（只读，安全）。</summary>
    public bool TryReadLimits(out RaplLimits limits)
    {
        if (TryRead(RaplPowerLimit.MsrPkgPowerLimit, out ulong raw))
        {
            limits = RaplPowerLimit.Decode(raw);
            return true;
        }
        limits = default;
        return false;
    }

    public static string StatusText(MsrStatus status) => status switch
    {
        MsrStatus.Writable => "可写",
        MsrStatus.ReadOnly => "只读",
        MsrStatus.NoAdmin => "需管理员",
        MsrStatus.NotInstalled => "驱动未安装",
        MsrStatus.DriverUnavailable => "驱动不可用",
        MsrStatus.ModuleUnavailable => "模块不可用",
        _ => "未知"
    };

    /// <summary>PawnIO 设备句柄为进程级共享，此处不主动关闭（随进程退出回收）。</summary>
    public void Dispose() { /* no-op：共享设备句柄 */ }
}
