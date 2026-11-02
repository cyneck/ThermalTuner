using System.IO;
using System.Runtime.InteropServices;

namespace ThermalPilot.App.Services.Msr;

/// <summary>
/// PawnIOLib.dll 的动态加载封装。
/// 设计要点：
///  1. 不硬依赖安装路径，按 Program Files → x86 → PATH 顺序探测；
///  2. 找不到库/导出函数时返回 null，绝不抛异常（不能因为没装驱动就崩）；
///  3. 只暴露 open/load/execute/close 四个入口，与 PawnIOLib.h 一一对应。
/// 参考：C:\Program Files\PawnIO\PawnIOLib.h
/// </summary>
internal static class PawnIoNative
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoOpen(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoLoad(IntPtr handle, byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoExecute(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input,
        nuint inputSize,
        [Out] ulong[] output,
        nuint outputSize,
        out nuint returnSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoClose(IntPtr handle);

    private static readonly object Gate = new();
    private static bool _tried;
    private static IntPtr _library;
    private static PawnIoOpen? _open;
    private static PawnIoLoad? _load;
    private static PawnIoExecute? _execute;
    private static PawnIoClose? _close;

    public static bool IsAvailable => EnsureLoaded();
    public static string? LoadError { get; private set; }
    public static string? LibraryPath { get; private set; }

    private static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_tried) return _library != IntPtr.Zero;
            _tried = true;

            foreach (string candidate in Candidates())
            {
                try
                {
                    if (!File.Exists(candidate) && candidate.Contains('\\')) continue;
                    _library = NativeLibrary.Load(candidate);
                    _open = Bind<PawnIoOpen>("pawnio_open");
                    _load = Bind<PawnIoLoad>("pawnio_load");
                    _execute = Bind<PawnIoExecute>("pawnio_execute");
                    _close = Bind<PawnIoClose>("pawnio_close");
                    LibraryPath = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    LoadError = $"{candidate}: {ex.Message}";
                    _library = IntPtr.Zero;
                }
            }
            return false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf, "PawnIO", "PawnIOLib.dll");
        yield return Path.Combine(pf86, "PawnIO", "PawnIOLib.dll");
        yield return "PawnIOLib.dll"; // 依赖 PATH
    }

    private static T? Bind<T>(string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, export, out IntPtr addr) || addr == IntPtr.Zero)
            throw new MissingMethodException($"缺少导出函数 {export}");
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    public static int Open(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        return EnsureLoaded() && _open is not null ? _open(out handle) : unchecked((int)0x80004005);
    }

    public static int Load(IntPtr handle, byte[] blob) =>
        EnsureLoaded() && _load is not null ? _load(handle, blob, (nuint)blob.Length) : unchecked((int)0x80004005);

    public static int Execute(IntPtr handle, string function, ulong[] input, ulong[] output, out nuint written) =>
        EnsureLoaded() && _execute is not null
            ? _execute(handle, function, input, (nuint)input.Length, output, (nuint)output.Length, out written)
            : Fail(out written);

    public static int Close(IntPtr handle) =>
        EnsureLoaded() && _close is not null ? _close(handle) : 0;

    private static int Fail(out nuint written)
    {
        written = 0;
        return unchecked((int)0x80004005);
    }
}
