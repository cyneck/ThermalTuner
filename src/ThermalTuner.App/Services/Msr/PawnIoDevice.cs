using System.Runtime.InteropServices;
using System.Text;

namespace ThermalTuner.App.Services.Msr;

/// <summary>
/// PawnIO 内核驱动的用户态通道 —— **直连 IOCTL，不依赖 PawnIOLib.dll**。
///
/// 为什么不用 PawnIOLib.dll：
///   官方安装器在本机不可用（`PawnIO_setup.exe -install -silent` 恒返回 31），
///   因此 `C:\Program Files\PawnIO\PawnIOLib.dll` 根本不存在；
///   而驱动本身（`PawnIO.sys`，微软 WHQL 签名）已由自管安装流程装入系统并在运行。
///   只要设备对象 \\.\PawnIO 存在，就可以直接用 IOCTL 与它对话。
///
/// 协议（源码 `PawnIO/include/pawnio_um.h`，CTL_CODE(FILE_DEVICE_UNKNOWN=41394, ...)）：
///   IOCTL_PIO_LOAD_BINARY = 0xA1B22084  输入 = 模块字节，输出无
///   IOCTL_PIO_EXECUTE_FN  = 0xA1B22104  输入 = 32 字节 ANSI 函数名（零填充）+ ulong64 参数数组
///                                       输出 = ulong64 返回值
///
/// 安全边界：驱动只执行**已签名白名单模块**，模块内部再按白名单校验具体 MSR。
/// 也就是说「能写哪些寄存器」由模块决定，不由本类决定。
/// </summary>
internal sealed class PawnIoDevice : IDisposable
{
    private const uint IoctlLoadBinary = 0xA1B22084;
    private const uint IoctlExecuteFn = 0xA1B22104;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private const int FunctionNameBytes = 32;

    private IntPtr _handle = InvalidHandle;
    private bool _disposed;

    private PawnIoDevice(IntPtr handle) => _handle = handle;

    public bool IsOpen => _handle != InvalidHandle && _handle != IntPtr.Zero && !_disposed;

    /// <summary>最近一次底层失败的 Win32 错误码（诊断用）。</summary>
    public int LastWin32Error { get; private set; }

    /// <summary>
    /// 打开设备对象。驱动已在内核中运行但设备对象不存在时返回 null，
    /// 并给出 <paramref name="error"/> = ERROR_FILE_NOT_FOUND(2)。
    /// </summary>
    public static PawnIoDevice? TryOpen(out int error)
    {
        error = 0;
        foreach (string path in new[] { @"\\.\PawnIO", @"\\?\GLOBALROOT\Device\PawnIO" })
        {
            IntPtr h = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero,
                OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (h != InvalidHandle && h != IntPtr.Zero)
                return new PawnIoDevice(h);
            error = Marshal.GetLastWin32Error();
        }
        return null;
    }

    /// <summary>加载已签名模块。模块本身由驱动校验签名，失败即拒绝。</summary>
    public bool LoadModule(byte[] blob, out int error)
    {
        if (!IsOpen) { error = unchecked((int)0x80070006); return false; }   // ERROR_INVALID_HANDLE

        IntPtr buffer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, buffer, blob.Length);
            bool ok = DeviceIoControl(_handle, IoctlLoadBinary, buffer, (uint)blob.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            LastWin32Error = error;
            return ok;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// 执行模块函数。缓冲区布局：32 字节函数名（零填充）+ ulong64 入参；
    /// 有返回值时输出 8 字节。
    /// </summary>
    public bool Execute(string function, ulong[] args, int outputCount, out ulong result, out int error)
    {
        result = 0;
        error = unchecked((int)0x80070006);
        if (!IsOpen || _disposed) return false;

        int inputSize = FunctionNameBytes + args.Length * sizeof(ulong);
        int outputSize = Math.Max(outputCount, 1) * sizeof(ulong);

        IntPtr input = Marshal.AllocHGlobal(inputSize);
        IntPtr output = Marshal.AllocHGlobal(outputSize);
        try
        {
            byte[] name = Encoding.ASCII.GetBytes(function);
            if (name.Length >= FunctionNameBytes) { error = unchecked((int)0x800700CE); return false; }

            for (int i = 0; i < FunctionNameBytes; i++) Marshal.WriteByte(input, i, 0);
            Marshal.Copy(name, 0, input, name.Length);

            for (int i = 0; i < args.Length; i++)
                Marshal.WriteInt64(input, FunctionNameBytes + i * sizeof(ulong), unchecked((long)args[i]));

            for (int i = 0; i < outputSize; i++) Marshal.WriteByte(output, i, 0);

            bool ok = DeviceIoControl(_handle, IoctlExecuteFn, input, (uint)inputSize,
                outputCount > 0 ? output : IntPtr.Zero,
                (uint)(outputCount > 0 ? outputSize : 0), out _, IntPtr.Zero);

            error = ok ? 0 : Marshal.GetLastWin32Error();
            LastWin32Error = error;
            if (ok && outputCount > 0) result = unchecked((ulong)Marshal.ReadInt64(output, 0));
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != InvalidHandle && _handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = InvalidHandle;
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code,
        IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
        out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
