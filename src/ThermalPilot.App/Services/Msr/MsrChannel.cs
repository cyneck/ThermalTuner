using System.Reflection;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using ThermalPilot.Core.Msr;

namespace ThermalPilot.App.Services.Msr;

public enum MsrStatus
{
    /// <summary>内核驱动未加载（未提权、被安全软件拦截或 DLL 版本不对）。</summary>
    NotInstalled,
    /// <summary>驱动句柄不可用 / MSR 读测试失败。</summary>
    DriverUnavailable,
    /// <summary>缺少管理员权限。</summary>
    NoAdmin,
    /// <summary>LHM 程序集不提供 Ring0 MSR 接口。</summary>
    ModuleUnavailable,
    /// <summary>可读，但不可写（固件锁定或写入被拒）。</summary>
    ReadOnly,
    /// <summary>可读可写。</summary>
    Writable
}

/// <summary>
/// MSR 访问通道 —— 复用 LibreHardwareMonitor 0.9.4 自带的 WinRing0 内核驱动。
///
/// 背景（v0.4.1 修复）：
///   LHM 0.9.4 的 Ring0 提供 ReadMsr/WriteMsr，可真实读写 0x610 / 0x774 / 0x1AD / 0x150；
///   LHM 0.9.6 起后端改为 PawnIO，随包 IntelMSR 模块只有 ioctl_read_msr（只读），
///   直接升级会让「写入链路」凭空消失。本项目因此固定引用 0.9.4。
///
/// 能力探测顺序：管理员 → Ring0 接口 → Open 驱动 → 读 0x610 → 检查 lock 位。
/// 启动时不做任何写操作；写能力在首次应用时由回读校验确认（见 MsrGuard）。
/// </summary>
public sealed class MsrChannel : IDisposable
{
    private static readonly object ProbeGate = new();
    private static Type? _ring0Type;
    private static MethodInfo? _readMsr;
    private static MethodInfo? _writeMsr;
    private static MethodInfo? _open;
    private static PropertyInfo? _isOpen;
    private static bool _probed;
    private static string _probeError = string.Empty;

    private MsrChannel(MsrStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    public MsrStatus Status { get; private set; }
    public string Detail { get; private set; }
    public bool CanWrite => Status == MsrStatus.Writable;

    /// <summary>后端名称（诊断报告用）。</summary>
    public static string BackendName => "LHM 0.9.4 / WinRing0 (Ring0.WriteMsr)";

    /// <summary>运行期发现不可写（如驱动拒绝）时自我降级，UI 据此隐藏写入入口。</summary>
    public void MarkNotWritable(string reason)
    {
        Status = MsrStatus.ReadOnly;
        Detail = reason;
    }

    /// <summary>反射绑定 Ring0 的 MSR 读写接口（进程内只做一次）。</summary>
    private static bool EnsureProbed()
    {
        lock (ProbeGate)
        {
            if (_probed) return _readMsr is not null && _writeMsr is not null;
            _probed = true;

            try
            {
                var asm = typeof(Computer).Assembly;
                _ring0Type = asm.GetType("LibreHardwareMonitor.Hardware.Ring0");
                if (_ring0Type is null)
                {
                    _probeError = "当前 LHM 程序集不含 Ring0 类型（这是 0.9.6+ PawnIO 分支，需回退到 0.9.4）";
                    return false;
                }

                foreach (var m in _ring0Type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "ReadMsr" && ps.Length == 3 &&
                        ps[0].ParameterType == typeof(uint) && ps[1].IsOut && ps[2].IsOut)
                        _readMsr = m;
                    else if (m.Name == "WriteMsr" && ps.Length == 3 &&
                             ps.All(p => p.ParameterType == typeof(uint)))
                        _writeMsr = m;
                    else if (m.Name == "Open" && ps.Length == 0)
                        _open = m;
                }

                _isOpen = _ring0Type.GetProperty("IsOpen",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (_readMsr is null || _writeMsr is null)
                    _probeError = "LHM Ring0 未提供 MSR 读写接口（版本不兼容）";
            }
            catch (Exception ex)
            {
                _probeError = "探测 Ring0 失败：" + ex.GetBaseException().Message;
            }

            return _readMsr is not null && _writeMsr is not null;
        }
    }

    /// <summary>列出 Ring0 暴露的 MSR 相关方法（替代 PawnIO 模块函数探测）。</summary>
    public static string[] ListFunctions()
    {
        if (!EnsureProbed() || _ring0Type is null) return Array.Empty<string>();
        try
        {
            return _ring0Type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name.Contains("Msr", StringComparison.OrdinalIgnoreCase)
                            || m.Name is "Open" or "Close")
                .Select(m => $"{m.ReturnType.Name} {m.Name}(" +
                             string.Join(", ", m.GetParameters().Select(p =>
                                 $"{(p.IsOut ? "out " : "")}{p.ParameterType.Name} {p.Name}")) + ")")
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool Ring0IsOpen()
    {
        try { return _isOpen?.GetValue(null) is bool b && b; }
        catch { return false; }
    }

    public static MsrChannel Open()
    {
        if (!IsElevated())
            return new MsrChannel(MsrStatus.NoAdmin,
                "需要管理员权限（MSR 读写要求 ring0 内核驱动）");

        if (!EnsureProbed())
            return new MsrChannel(MsrStatus.ModuleUnavailable, _probeError);

        // Computer.Open() 会释放并加载内嵌的 WinRing0 驱动。HardwareTelemetry 通常已经 Open，
        // 这里只做 IsOpen 兜底，绝不调用 Computer.Close()（会关掉共享的 Ring0 句柄）。
        if (!Ring0IsOpen())
        {
            try
            {
                _open?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                return new MsrChannel(MsrStatus.DriverUnavailable,
                    "Ring0.Open 失败：" + ex.GetBaseException().Message +
                    "（检查内存完整性 HVCI / 安全软件是否拦截内核驱动）");
            }
        }

        if (!Ring0IsOpen())
            return new MsrChannel(MsrStatus.DriverUnavailable,
                "WinRing0 驱动未加载（检查管理员权限、HVCI 内存完整性、Defender 拦截）");

        var channel = new MsrChannel(MsrStatus.ReadOnly, "已就绪");

        if (!channel.TryRead(RaplPowerLimit.MsrPkgPowerLimit, out ulong current))
        {
            return new MsrChannel(MsrStatus.DriverUnavailable,
                "读取 MSR 0x610 失败（驱动未就绪或被虚拟化/安全软件拦截）");
        }

        var decoded = RaplPowerLimit.Decode(current);
        if (decoded.Locked)
            return new MsrChannel(MsrStatus.ReadOnly,
                $"0x610 的 lock 位已置位（固件锁定）：{RaplPowerLimit.Describe(current)}");

        // 刻意不在启动时做写探测：避免工具一启动就写 MSR。
        // 真实可写性由首次「应用」的回读校验确认，失败即自动回滚（G-03）。
        return new MsrChannel(MsrStatus.Writable,
            $"可读可写（{RaplPowerLimit.Describe(current)}；写入以回读校验确认）");
    }

    public bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        if (_readMsr is null || !Ring0IsOpen()) return false;
        try
        {
            var args = new object[] { msr, 0u, 0u };
            if (_readMsr.Invoke(null, args) is not bool ok || !ok) return false;
            value = ((ulong)(uint)args[2] << 32) | (uint)args[1];
            return true;
        }
        catch { return false; }
    }

    public bool TryWrite(uint msr, ulong value) => TryWrite(msr, value, out _);

    public bool TryWrite(uint msr, ulong value, out int hresult)
    {
        hresult = 0;
        if (_writeMsr is null || !Ring0IsOpen())
        {
            hresult = unchecked((int)0x80004005);
            LastWriteHResult = hresult;
            LastWriteReturned = 0;
            return false;
        }

        try
        {
            uint eax = (uint)(value & 0xFFFFFFFF);
            uint edx = (uint)(value >> 32);
            bool ok = _writeMsr.Invoke(null, new object[] { msr, eax, edx }) is bool r && r;
            hresult = ok ? 0 : unchecked((int)0x80004005);
            LastWriteHResult = hresult;
            LastWriteReturned = 0;
            return ok;
        }
        catch
        {
            hresult = unchecked((int)0x80004005);
            LastWriteHResult = hresult;
            LastWriteReturned = 0;
            return false;
        }
    }

    /// <summary>最近一次写入的 HRESULT 与返回条目数（诊断用；Ring0 后端固定为 0）。</summary>
    public int LastWriteHResult { get; private set; }
    public nuint LastWriteReturned { get; private set; }

    /// <summary>
    /// 兼容旧的函数探测入口：Ring0 后端没有 Pawn 模块函数概念。
    /// 返回 0x80070002 表示「不是模块函数」，真实能力请用 ListFunctions()。
    /// </summary>
    public int InvokeRaw(string function, ulong[] input) => unchecked((int)0x80070002);

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
        MsrStatus.NotInstalled => "驱动未加载",
        MsrStatus.DriverUnavailable => "驱动不可用",
        MsrStatus.ModuleUnavailable => "接口不可用",
        _ => "未知"
    };

    /// <summary>
    /// 不主动关闭 Ring0：驱动由 HardwareTelemetry 的 Computer 实例共享，
    /// 过早 Close 会让正在采样的温度/频率传感器失效。进程退出时由系统回收。
    /// </summary>
    public void Dispose() { /* no-op：共享静态 Ring0 句柄 */ }
}
