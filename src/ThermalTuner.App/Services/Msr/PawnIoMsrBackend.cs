using System.IO;
using System.Reflection;
using System.Text;

namespace ThermalTuner.App.Services.Msr;

/// <summary>
/// PawnIO 的 MSR 语义层：负责「找到模块 → 加载 → 读/写 MSR」，
/// 并把底层 Win32 错误码翻译成调用方能理解的 HRESULT。
///
/// 模块来源（按优先级）：
///   1. 嵌入资源 <c>Resources/IntelMSR.bin</c>（随产品发布的 PawnIO.Modules 0.2.11 构建）
///   2. <c>%LOCALAPPDATA%\ThermalTuner\driver\IntelMSR.bin</c>（用户可替换为更新版本）
///
/// 注意：LibreHardwareMonitor 0.9.6 内嵌的 IntelMSR 模块**只有** ioctl_read_msr，
/// 不能用于写入。本类加载前会校验模块字符串表是否含 ioctl_write_msr，
/// 不含则直接报告「只读模块」，避免出现「能读不能写」的静默降级。
/// </summary>
internal sealed class PawnIoMsrBackend : IDisposable
{
    /// <summary>模块里不存在目标函数。</summary>
    public const int HrFunctionMissing = unchecked((int)0x80070002);
    /// <summary>函数存在但被模块白名单拒绝（该 MSR 不允许写）。</summary>
    public const int HrAccessDenied = unchecked((int)0x80070005);
    public const int HrDeviceMissing = unchecked((int)0x80070006);

    private static readonly object Gate = new();
    private static PawnIoMsrBackend? _shared;
    private static bool _attempted;
    private static string _failure = string.Empty;
    private static string _moduleSource = "未知";
    private static string[] _functions = Array.Empty<string>();

    private readonly PawnIoDevice _device;

    private PawnIoMsrBackend(PawnIoDevice device) => _device = device;

    /// <summary>后台不可用时的原因（供诊断页展示，不要说「驱动不可用」这种笼统话）。</summary>
    public static string Failure => _failure;
    public static string ModuleSource => _moduleSource;
    public static IReadOnlyList<string> Functions => _functions;
    public static bool SupportsWrite => _functions.Contains("ioctl_write_msr", StringComparer.Ordinal);

    /// <summary>
    /// 取共享后端（进程内只初始化一次）。设备未打开 / 模块未加载时返回 null，
    /// 具体原因见 <see cref="Failure"/>。
    /// </summary>
    public static PawnIoMsrBackend? Shared()
    {
        lock (Gate)
        {
            if (_attempted) return _shared;
            _attempted = true;

            var device = PawnIoDevice.TryOpen(out int openError);
            if (device is null)
            {
                _failure = openError == 2
                    ? "PawnIO 设备对象不存在（驱动未安装或设备节点未创建）"
                    : $"无法打开 \\\\.\\PawnIO（Win32 {openError}）";
                return null;
            }

            byte[]? blob = ResolveModule(out string source, out string moduleError);
            if (blob is null)
            {
                device.Dispose();
                _failure = moduleError;
                return null;
            }
            _moduleSource = source;

            _functions = EnumerateFunctions(blob);
            if (!device.LoadModule(blob, out int loadError))
            {
                device.Dispose();
                _failure = loadError == 5
                    ? "模块加载被拒（签名校验失败或安全软件拦截）"
                    : $"模块加载失败（Win32 {loadError}）";
                return null;
            }

            if (!SupportsWrite)
            {
                device.Dispose();
                _failure = $"模块不含 ioctl_write_msr（只读版本）：{source}";
                _functions = Array.Empty<string>();
                return null;
            }

            _shared = new PawnIoMsrBackend(device);
            _failure = string.Empty;
            return _shared;
        }
    }

    // ---------------- MSR 读写 ----------------

    public bool ReadMsr(uint index, out ulong value, out int error)
    {
        value = 0;
        if (_device.Execute("ioctl_read_msr", new[] { (ulong)index }, 1, out ulong result, out error))
        {
            value = result;
            return true;
        }
        error = error == 2 ? HrFunctionMissing : unchecked((int)0x80070000) | error;
        return false;
    }

    public bool WriteMsr(uint index, ulong value, out int error)
    {
        if (_device.Execute("ioctl_write_msr", new[] { (ulong)index, value }, 0, out _, out error))
        {
            error = 0;
            return true;
        }
        error = error switch
        {
            2 => HrFunctionMissing,
            5 => HrAccessDenied,
            _ => unchecked((int)0x80070000) | error
        };
        return false;
    }

    /// <summary>执行任意模块函数（诊断用）。返回 0 表示成功。</summary>
    public int ExecuteRaw(string function, ulong[] input, out ulong output)
    {
        output = 0;
        if (!_device.Execute(function, input, 1, out output, out int error)) return error switch
        {
            2 => HrFunctionMissing,
            5 => HrAccessDenied,
            _ => unchecked((int)0x80070000) | error
        };
        return 0;
    }

    /// <summary>
    /// 写白名单预判（来源：PawnIO.Modules 0.2.11 `IntelMSR.p` 的 `is_allowed_msr_write`）。
    ///
    /// 名单内只有 6 个寄存器：
    ///   0x601 VR_CURRENT_CONFIG / 0x610 PKG_POWER_LIMIT / 0x150 OC_MAILBOX /
    ///   0x607 VR_MAILBOX_INTERFACE / 0x608 VR_MAILBOX_DATA / 0x1A4 MISC_FEATURE_CONTROL
    ///
    /// **读**白名单同样不含 0x770 / 0x772 / 0x774 / 0x1A0 / 0x194 ——
    /// 也就是说 HWP 请求寄存器（EPP、Min/Max Perf、Activity Window）在本后端下**完全不可访问**，
    /// 睿频倍率表 0x1AD 只能读、不能写。这是模块的硬边界，不是本机故障。
    ///
    /// 该判定仅用于**提前给出准确提示与 UI 禁用**；真正生效仍以写入返回码为准。
    /// </summary>
    public static bool IsWriteAllowed(uint msr) => msr is
        0x601u or   // MSR_VR_CURRENT_CONFIG
        0x610u or   // MSR_PKG_POWER_LIMIT（PL1 / PL2 / Tau）
        0x150u or   // MSR_OC_MAILBOX
        0x607u or   // MSR_VR_MAILBOX_INTERFACE
        0x608u or   // MSR_VR_MAILBOX_DATA
        0x1A4u;     // MSR_MISC_FEATURE_CONTROL

    /// <summary>读白名单预判（同上，来源 `is_allowed_msr_read`）。</summary>
    public static bool IsReadAllowed(uint msr) => IsWriteAllowed(msr) || msr is
        0x8Bu or    // IA32_BIOS_SIGN_ID
        0x1B1u or   // IA32_PACKAGE_THERM_STATUS
        0x198u or   // IA32_PERF_STATUS
        0x1A2u or   // IA32_TEMPERATURE_TARGET
        0x19Cu or   // IA32_THERM_STATUS
        0xE7u or    // IA32_MPERF
        0xE8u or    // IA32_APERF
        0x611u or 0x613u or 0x614u or          // PKG 能量 / 性能 / 能力
        0x618u or 0x619u or 0x61Bu or 0x61Cu or // DRAM
        0x620u or 0x621u or                    // UNC 全局计数
        0x638u or 0x639u or 0x63Au or 0x63Bu or // PP0
        0x640u or 0x641u or 0x642u or          // PP1
        0x64Du or 0x606u or 0xCEu or 0x2Au or  // PLATFORM / RAPL_UNIT / PLATFORM_INFO / EBL
        0x1ADu or 0x650u;                      // TURBO_RATIO_LIMIT / SECONDARY

    // ---------------- 模块来源 ----------------

    private static byte[]? ResolveModule(out string source, out string error)
    {
        source = "未知";
        error = string.Empty;

        // ① 嵌入资源（随产品发布）
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("IntelMSR.bin", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    source = $"内置资源 {name}";
                    return ms.ToArray();
                }
            }
        }
        catch { /* 继续尝试外部文件 */ }

        // ② 用户可替换的外部文件（便于手动升级模块）
        try
        {
            string external = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermalTuner", "driver", "IntelMSR.bin");
            if (File.Exists(external))
            {
                source = external;
                return File.ReadAllBytes(external);
            }
        }
        catch { /* 忽略 */ }

        error = "找不到 IntelMSR 模块（内置资源缺失且 %LOCALAPPDATA% 下无 IntelMSR.bin）";
        return null;
    }

    /// <summary>
    /// 从模块二进制里扫出可打印符号名。
    /// PawnIO 模块是 Pawn 编译产物，函数名以明文 ASCII 存在，因此可用字符串扫描判定能力，
    /// 无需真的调用一次才知道有没有写接口。
    /// </summary>
    private static string[] EnumerateFunctions(byte[] blob)
    {
        var found = new List<string>();
        var sb = new StringBuilder();
        foreach (byte b in blob)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                sb.Append((char)b);
                continue;
            }
            if (sb.Length >= 5)
            {
                string token = sb.ToString();
                if (token.StartsWith("ioctl_", StringComparison.Ordinal)
                    || token.StartsWith("msr_", StringComparison.Ordinal)
                    || token is "cpuid" or "get_arch")
                    found.Add(token);
            }
            sb.Clear();
        }
        return found.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        // 共享实例：设备句柄随进程退出释放，主动 Close 会让后续调用失败。
    }
}
