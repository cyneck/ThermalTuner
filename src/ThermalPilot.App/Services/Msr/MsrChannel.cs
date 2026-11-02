using ThermalPilot.Core.Msr;

namespace ThermalPilot.App.Services.Msr;

public enum MsrStatus
{
    /// <summary>未安装 PawnIO。</summary>
    NotInstalled,
    /// <summary>驱动未加载 / 服务未就绪。</summary>
    DriverUnavailable,
    /// <summary>缺少管理员权限。</summary>
    NoAdmin,
    /// <summary>找不到可用的 Pawn 模块。</summary>
    ModuleUnavailable,
    /// <summary>可读，但不可写（固件锁定或写入被拒）。</summary>
    ReadOnly,
    /// <summary>可读可写。</summary>
    Writable
}

/// <summary>
/// MSR 0x610 访问通道。
/// 能力探测顺序：库 → 驱动句柄 → 模块 → 读 0x610 → 检查 lock 位。
/// 启动时不做任何写操作；写能力在首次应用时由回读校验确认。
/// </summary>
public sealed class MsrChannel : IDisposable
{
    private IntPtr _handle;

    private MsrChannel(MsrStatus status, string detail, IntPtr handle)
    {
        Status = status;
        Detail = detail;
        _handle = handle;
    }

    private string _lockDetail = string.Empty;

    public MsrStatus Status { get; private set; }
    public string Detail { get; private set; }
    public bool CanWrite => Status == MsrStatus.Writable;

    /// <summary>运行期发现不可写（如模块拒绝）时自我降级，UI 据此隐藏写入入口。</summary>
    public void MarkNotWritable(string reason)
    {
        Status = MsrStatus.ReadOnly;
        Detail = reason;
    }

    public static MsrChannel Open()
    {
        if (!PawnIoNative.IsAvailable)
            return new MsrChannel(MsrStatus.NotInstalled,
                "未找到 PawnIOLib.dll（安装 PawnIO 后重试）", IntPtr.Zero);

        int hr = PawnIoNative.Open(out IntPtr handle);
        if (hr < 0)
            return new MsrChannel(MapOpenError(hr), Describe(hr, "打开 PawnIO 设备失败"), IntPtr.Zero);

        byte[]? blob = LhmPawnModule.TryReadIntelMsr();
        if (blob is null)
        {
            PawnIoNative.Close(handle);
            return new MsrChannel(MsrStatus.ModuleUnavailable, "未取到内置 IntelMSR 模块", IntPtr.Zero);
        }

        hr = PawnIoNative.Load(handle, blob);
        if (hr < 0)
        {
            PawnIoNative.Close(handle);
            return new MsrChannel(MsrStatus.ModuleUnavailable, Describe(hr, "加载 Pawn 模块失败"), IntPtr.Zero);
        }

        var channel = new MsrChannel(MsrStatus.ReadOnly, "已就绪", handle);

        if (!channel.TryRead(RaplPowerLimit.MsrPkgPowerLimit, out ulong current))
        {
            channel.Dispose();
            return new MsrChannel(MsrStatus.ReadOnly, "读取 MSR 0x610 失败（可能被虚拟化拦截）", IntPtr.Zero);
        }

        var decoded = RaplPowerLimit.Decode(current);
        if (decoded.Locked)
            return channel.WithStatus(MsrStatus.ReadOnly,
                $"0x610 的 lock 位已置位（固件锁定）：{RaplPowerLimit.Describe(current)}");

        // 刻意不在启动时做写探测：避免工具一启动就写 MSR。
        // 真实可写性由首次「应用」的回读校验确认，失败即自动回滚（G-03）。
        return channel.WithStatus(MsrStatus.Writable,
            $"可读可写（{RaplPowerLimit.Describe(current)}；写入以回读校验确认）");
    }

    private MsrChannel WithStatus(MsrStatus status, string detail)
    {
        var clone = new MsrChannel(status, detail, _handle) { _lockDetail = _lockDetail };
        _handle = IntPtr.Zero;   // 句柄所有权转移给 clone，避免 double close
        return clone;
    }

    public bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        if (_handle == IntPtr.Zero) return false;

        ulong[] input = { msr };
        ulong[] output = new ulong[1];
        int hr = PawnIoNative.Execute(_handle, "ioctl_read_msr", input, output, out nuint written);
        if (hr < 0 || written < 1) return false;

        value = output[0];
        return true;
    }

    public bool TryWrite(uint msr, ulong value) => TryWrite(msr, value, out _);

    public bool TryWrite(uint msr, ulong value, out int hresult)
    {
        hresult = unchecked((int)0x80004005);
        if (_handle == IntPtr.Zero) return false;

        ulong[] input = { msr, value };
        hresult = PawnIoNative.Execute(_handle, "ioctl_write_msr", input, Array.Empty<ulong>(), out nuint written);
        LastWriteHResult = hresult;
        LastWriteReturned = written;
        return hresult >= 0;
    }

    /// <summary>最近一次写入的 HRESULT 与模块返回条目数（诊断用）。</summary>
    public int LastWriteHResult { get; private set; }
    public nuint LastWriteReturned { get; private set; }

    /// <summary>直接调用模块函数（探测函数是否存在用；传入空数组不会产生写入）。</summary>
    public int InvokeRaw(string function, ulong[] input)
    {
        if (_handle == IntPtr.Zero) return unchecked((int)0x80004005);
        return PawnIoNative.Execute(_handle, function, input, Array.Empty<ulong>(), out _);
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
        MsrStatus.NotInstalled => "未装 PawnIO",
        MsrStatus.DriverUnavailable => "驱动未就绪",
        MsrStatus.ModuleUnavailable => "模块不可用",
        _ => "未知"
    };

    private static MsrStatus MapOpenError(int hr) => hr switch
    {
        unchecked((int)0x80070005) => MsrStatus.NoAdmin,       // E_ACCESSDENIED
        unchecked((int)0x80070002) => MsrStatus.DriverUnavailable, // FILE_NOT_FOUND
        _ => MsrStatus.DriverUnavailable
    };

    private static string Describe(int hr, string prefix) =>
        $"{prefix}（HRESULT 0x{hr:X8}）";

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            try { PawnIoNative.Close(_handle); } catch { /* 忽略 */ }
            _handle = IntPtr.Zero;
        }
    }
}
