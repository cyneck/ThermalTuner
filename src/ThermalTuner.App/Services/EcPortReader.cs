using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace ThermalTuner.App.Services;

/// <summary>
/// 内置 EC 只读：复用 LHM 0.9.4 / WinRing0 已加载的 Ring0 端口接口读 ACPI EC 寄存器，
/// 不再依赖 NBFC 随包的 ec-probe.exe（NBFC 卸载后风扇指标仍可采集）。
///
/// 【边界，必须保持】
/// · 只向 EC 命令端口 0x66 写 READ_EC(0x80)、向数据端口 0x62 写「要读的寄存器地址」，
///   这是 EC 读操作必需的握手；**不写任何 EC 寄存器内容**，尤其不写风扇控制寄存器
///   （NBFC 生效配置里的 WriteRegister=148），也不提供任何写 EC 的公共入口。
/// · 绝不调用 Ring0.WriteIoPort 去写寄存器数据；端口写仅用于上述两个握手字节。
/// · 与 BIOS 共享 EC 端口存在竞态（LHM 0.9.4 官方 XML 对 WindowsEmbeddedControllerIO
///   明确提示 unsafe / race condition）。收敛措施：进程内互斥 + 上层 2.2s 低频 +
///   每步严格超时 + 连续失败自动降级并停止探询。
/// · 需要管理员：GUI 模式下 WinRing0 驱动由 HardwareTelemetry 的 Computer.Open() 加载；
///   命令行模式（--ec-probe）没有该实例，此时最多尝试一次反射调用 Ring0.Open()
///   自行加载（与 MsrChannel 既有做法一致），失败即降级，不反复尝试加载驱动。
///   普通权限下 Ring0.IsOpen=false，本类返回不可用，不触碰端口。
/// </summary>
internal static class EcPortReader
{
    private const uint StatusPort = 0x66;   // EC_SC：命令 / 状态
    private const uint DataPort = 0x62;     // EC_DATA：地址 / 数据
    private const byte ReadEcCommand = 0x80; // ACPI EC read command
    private const byte IbfMask = 0x02;      // Input Buffer Full
    private const byte ObfMask = 0x01;      // Output Buffer Full
    private const int WaitAttempts = 120;   // 每步最多 120 次自旋，毫秒级上限
    private const int MaxConsecutiveFailures = 3;

    private static readonly object ProbeGate = new();
    private static readonly object IoGate = new();
    private static bool _probed;
    private static Type? _ring0Type;
    private static MethodInfo? _readPort;
    private static MethodInfo? _writePort;
    private static MethodInfo? _open;
    private static PropertyInfo? _isOpen;
    private static bool _openAttempted;
    private static bool _usable = true;
    private static int _failures;
    private static string _reason = "尚未探测";

    /// <summary>诊断文本：列出实际绑定到的 Ring0 端口 API 与驱动状态，供 --ec-probe 与自检报告使用。</summary>
    public static string Diagnostics()
    {
        EnsureProbed();
        return string.Join(Environment.NewLine, new[]
        {
            "Ring0 类型 : " + (_ring0Type?.FullName ?? "未找到"),
            "驱动已打开 : " + (DriverOpen() ? "true" : "false（需管理员；命令行最多自加载一次）"),
            "驱动自加载 : " + (_open is null ? "Ring0.Open 不可用" : _openAttempted ? "已尝试" : "未尝试"),
            "读端口 API : " + Describe(_readPort),
            "写端口 API : " + Describe(_writePort),
            "链路状态   : " + (_usable ? "可用" : "已降级：" + _reason),
            "边界       : 只写 0x66=0x80 / 0x62=地址做读握手；不写 EC 寄存器内容、不写风扇控制寄存器 148"
        });
    }

    /// <summary>读一个 EC 寄存器字节。失败时返回原因，不抛异常。</summary>
    public static (bool Ok, byte Value, string Reason) ReadByte(byte address)
    {
        if (!_usable) return (false, 0, _reason);
        EnsureProbed();
        if (_readPort is null || _writePort is null)
            return Fail("LHM 0.9.4 Ring0 未提供端口读写接口");
        if (!DriverOpen() && !TryOpenDriver())
            return Fail("WinRing0 驱动未加载（需以管理员启动）");

        lock (IoGate)
        {
            // 标准 ACPI EC 读时序：IBF 清零 → 0x80 → IBF 清零 → 地址 → OBF 置位 → 读数据
            if (!WaitUntil(() => TryReadStatus(out byte s1) && (s1 & IbfMask) == 0))
                return Fail("等待 EC IBF 清零超时");
            if (!TryWritePort(StatusPort, ReadEcCommand))
                return Fail("写 READ_EC 命令失败");
            if (!WaitUntil(() => TryReadStatus(out byte s2) && (s2 & IbfMask) == 0))
                return Fail("EC 命令握手超时");
            if (!TryWritePort(DataPort, address))
                return Fail("写 EC 寄存器地址失败");
            if (!WaitUntil(() => TryReadStatus(out byte s3) && (s3 & ObfMask) != 0))
                return Fail("等待 EC OBF 置位超时");
            if (!TryReadPort(DataPort, out byte value))
                return Fail("读 EC 数据端口失败");

            _failures = 0;
            return (true, value, "ok");
        }
    }

    // ---------------- 反射绑定（只读一次） ----------------

    private static void EnsureProbed()
    {
        lock (ProbeGate)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var asm = typeof(Computer).Assembly;
                // 0.9.4 的 MSR 接口在 Hardware.Ring0；端口接口可能在同一类型或 Interop.Ring0，
                // 两个都扫一遍，以实际找到的方法为准，不假设命名空间。
                foreach (string name in new[]
                         {
                             "LibreHardwareMonitor.Hardware.Ring0",
                             "LibreHardwareMonitor.Interop.Ring0"
                         })
                {
                    Type? t = asm.GetType(name, throwOnError: false);
                    if (t is null) continue;
                    _ring0Type ??= t;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name == "ReadIoPort" && _readPort is null) _readPort = m;
                        else if (m.Name == "WriteIoPort" && _writePort is null) _writePort = m;
                        else if (m.Name == "Open" && m.GetParameters().Length == 0 && _open is null) _open = m;
                    }
                    _isOpen ??= t.GetProperty("IsOpen",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_readPort is null || _writePort is null)
                    _reason = "Ring0 未暴露 ReadIoPort / WriteIoPort";
            }
            catch (Exception ex)
            {
                _reason = "探测 Ring0 失败：" + ex.GetBaseException().Message;
            }
        }
    }

    private static bool DriverOpen()
    {
        try { return _isOpen?.GetValue(null) is bool b && b; }
        catch { return false; }
    }

    /// <summary>
    /// 命令行模式下没有 HardwareTelemetry 的 Computer.Open()，最多尝试一次
    /// 反射调用 Ring0.Open() 自行加载 WinRing0（与 MsrChannel.cs 既有做法一致）。
    /// 普通权限下 Open 会失败，捕获后降级，不反复尝试加载内核驱动。
    /// </summary>
    private static bool TryOpenDriver()
    {
        if (_openAttempted) return false;
        _openAttempted = true;
        if (_open is null) return false;
        try { _open.Invoke(null, null); }
        catch { return false; }
        return DriverOpen();
    }

    /// <summary>
    /// 兼容两种常见签名：(uint port, out byte/uint value) 与 (uint port) => byte/uint。
    /// 反射调用只做数值转换，不触碰驱动以外的任何资源。
    /// </summary>
    private static bool TryReadPort(uint port, out byte value)
    {
        value = 0;
        if (_readPort is null) return false;
        try
        {
            var ps = _readPort.GetParameters();
            if (ps.Length == 1)
            {
                object?[] args = { Coerce(port, ps[0].ParameterType) };
                object? r = _readPort.Invoke(null, args);
                if (r is null) return false;
                value = Convert.ToByte(r);
                return true;
            }
            if (ps.Length == 2 && ps[1].IsOut)
            {
                Type outType = ps[1].ParameterType.GetElementType() ?? typeof(uint);
                object?[] args = { Coerce(port, ps[0].ParameterType), Coerce(0u, outType) };
                object? r = _readPort.Invoke(null, args);
                value = Convert.ToByte(args[1]);
                return r is null || r is not bool ok || ok;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>兼容 (uint port, byte/uint value) 签名；只用于写 EC 命令与地址两个握手字节。</summary>
    private static bool TryWritePort(uint port, byte value)
    {
        if (_writePort is null) return false;
        try
        {
            var ps = _writePort.GetParameters();
            if (ps.Length != 2) return false;
            object?[] args = { Coerce(port, ps[0].ParameterType), Coerce(value, ps[1].ParameterType) };
            object? r = _writePort.Invoke(null, args);
            return r is null || r is not bool ok || ok;
        }
        catch { return false; }
    }

    private static object Coerce(object value, Type target)
    {
        if (target == typeof(uint)) return Convert.ToUInt32(value);
        if (target == typeof(int)) return Convert.ToInt32(value);
        if (target == typeof(ushort)) return Convert.ToUInt16(value);
        if (target == typeof(short)) return Convert.ToInt16(value);
        if (target == typeof(byte)) return Convert.ToByte(value);
        return value;
    }

    private static bool TryReadStatus(out byte status) => TryReadPort(StatusPort, out status);

    private static bool WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < WaitAttempts; i++)
        {
            if (condition()) return true;
            Thread.SpinWait(200);
        }
        return false;
    }

    private static (bool Ok, byte Value, string Reason) Fail(string reason)
    {
        if (++_failures >= MaxConsecutiveFailures)
        {
            _usable = false;
            _reason = reason + $"（连续 {_failures} 次失败，已停止 EC 探询以避免与 BIOS 争用端口）";
        }
        return (false, 0, reason);
    }

    private static string Describe(MethodInfo? m)
    {
        if (m is null) return "未找到";
        var ps = new List<string>();
        foreach (var p in m.GetParameters())
            ps.Add((p.IsOut ? "out " : "") + p.ParameterType.Name);
        return $"{m.DeclaringType?.Name}.{m.Name}({string.Join(", ", ps)}) : {m.ReturnType.Name}";
    }
}
