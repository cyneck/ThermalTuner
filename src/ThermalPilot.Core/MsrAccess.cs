using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace ThermalPilot.Core;

/// <summary>
/// 64 位 MSR 读写（复用 LibreHardwareMonitor 自带的已签名内核驱动 Ring0）。
///
/// LHM 0.9.4 的真实签名（反射确认）：
///   static bool ReadMsr(uint index, out uint eax, out uint edx)
///   static bool WriteMsr(uint index, uint eax, uint edx)
///   static void Open() / static bool IsOpen
/// 之前的 UndervoltService 按「1 参/2 参」去找，签名永远匹配不上，所以降压校验必然失败——
/// 本类按真实签名重写，并对「驱动是否已打开」做兜底。
///
/// 注意：Ring0 依赖 Computer.Open() 释放并加载驱动，因此本类必须在 HardwareService.Open()
/// 之后调用。非管理员或开启 HVCI 时不可用。
/// </summary>
public sealed class MsrAccess
{
    private Type? _ring0;
    private MethodInfo? _read;
    private MethodInfo? _write;
    private MethodInfo? _open;
    private PropertyInfo? _isOpen;
    private bool _probed;
    private readonly object _probeGate = new();

    public bool Available { get; private set; }
    public string LastError { get; private set; } = "未探测";

    public MsrAccess() { }

    /// <summary>
    /// 自检用：构造一个"确定不可用、原因已知"的实例，让上层判定走 NeedsPrerequisite 分支，
    /// 而不必真的去加载驱动（CI / 非管理员环境跑不了 Ring0）。
    /// </summary>
    internal MsrAccess(string unavailableReason)
    {
        _probed = true;
        Available = false;
        LastError = unavailableReason;
    }

    /// <summary>探测 Ring0 是否可用（幂等，线程安全）。</summary>
    public bool Probe()
    {
        lock (_probeGate)
        {
            if (_probed) return Available;
            _probed = true;
            return ProbeCore();
        }
    }

    private bool ProbeCore()
    {
        if (!Privilege.IsElevated)
        {
            LastError = "需要管理员权限（MSR 写入要求 ring0 驱动）";
            return false;
        }
        if (Privilege.HvciEnabled)
        {
            LastError = "系统已开启内存完整性(HVCI)，内核驱动被禁止加载";
            return false;
        }

        try
        {
            var asm = typeof(Computer).Assembly;
            _ring0 = asm.GetType("LibreHardwareMonitor.Hardware.Ring0");
            if (_ring0 == null)
            {
                LastError = "未找到 Ring0 类型（LibreHardwareMonitor 版本不兼容）";
                return false;
            }

            foreach (var m in _ring0.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "ReadMsr" && ps.Length == 3 &&
                    ps[0].ParameterType == typeof(uint) && ps[1].IsOut && ps[2].IsOut)
                {
                    _read = m;
                }
                else if (m.Name == "WriteMsr" && ps.Length == 3 &&
                         ps[0].ParameterType == typeof(uint) &&
                         ps[1].ParameterType == typeof(uint) &&
                         ps[2].ParameterType == typeof(uint))
                {
                    _write = m;
                }
                else if (m.Name == "Open" && ps.Length == 0)
                {
                    _open = m;
                }
            }

            _isOpen = _ring0.GetProperty("IsOpen",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (_read == null || _write == null)
            {
                LastError = "Ring0 未提供 MSR 读写接口（版本不兼容）";
                return false;
            }

            // Computer.Open() 通常已经打开驱动；这里兜底再尝试一次
            bool open = false;
            try { open = _isOpen?.GetValue(null) is bool b && b; } catch { }
            if (!open)
            {
                try { _open?.Invoke(null, null); }
                catch (Exception ex)
                {
                    LastError = "Ring0.Open 失败：" + (ex.InnerException?.Message ?? ex.Message);
                }
            }

            // 连通性测试：读只读的 IA32_MISC_ENABLE (0x1A0)
            if (!TryRead(0x1A0, out _))
            {
                LastError = "MSR 读测试失败（驱动未加载或被安全软件拦截）";
                return false;
            }

            Available = true;
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = "探测 MSR 失败：" + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }
    }

    /// <summary>读 64 位 MSR。失败返回 false 且 value=0。</summary>
    public bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        if (_read == null) return false;
        try
        {
            var args = new object[] { msr, 0u, 0u };
            bool ok = _read.Invoke(null, args) is bool b && b;
            if (!ok) return false;
            uint eax = (uint)args[1];
            uint edx = (uint)args[2];
            value = ((ulong)edx << 32) | eax;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写 64 位 MSR。失败返回 false。</summary>
    public bool TryWrite(uint msr, ulong value)
    {
        if (_write == null) return false;
        try
        {
            uint eax = (uint)(value & 0xFFFFFFFF);
            uint edx = (uint)(value >> 32);
            return _write.Invoke(null, new object[] { msr, eax, edx }) is bool b && b;
        }
        catch
        {
            return false;
        }
    }
}
