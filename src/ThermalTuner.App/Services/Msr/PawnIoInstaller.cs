using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ThermalTuner.App.Services.Msr;

public enum PawnIoInstallState
{
    /// <summary>设备已可用，无需安装。</summary>
    Ready,
    /// <summary>安装成功（可能需重启后才生效）。</summary>
    Installed,
    /// <summary>需要重启才能完成。</summary>
    NeedsReboot,
    /// <summary>缺少管理员权限。</summary>
    NeedAdmin,
    /// <summary>安装失败。</summary>
    Failed
}

public sealed record PawnIoInstallResult(PawnIoInstallState State, string Message);

/// <summary>
/// PawnIO 驱动自管安装器。
///
/// 为什么必须自己做：
///   官方安装器 `PawnIO_setup.exe -install -silent` 在本机恒返回 31（ERROR_GEN_FAILURE），
///   且不落任何文件；而 PawnIO 是**唯一** MSR 通道（WinRing0 已废弃，无降级方案），
///   所以驱动必须由产品自己保证可安装。
///
/// 安装分三步（与已验证的 DriverProbe 流程一致）：
///   ① 释放内嵌驱动包（pawnio.inf / PawnIO.sys / PawnIO.cat）到 %LOCALAPPDATA%
///   ② `pnputil /add-driver ... /install` 把驱动包导入 DriverStore
///   ③ 创建 Root 枚举设备节点 —— **必须由 LocalSystem 执行**（Enum\Root 管理员也只读），
///      因此用临时 Win32 服务承载（`--pawnio-svc`），完成后自删
///
/// 已知坑（都是实测踩出来的，改动前请先确认）：
///   · SetupDiSetDeviceRegistryProperty 的 P/Invoke **必须显式 CharSet.Unicode**，
///     否则绑定 ANSI 版会把 REG_MULTI_SZ 按单字节拆成逐字符条目，硬件 ID 永久损坏；
///   · SPDRP_HARDWAREID 必须以**双 NULL** 结尾；
///   · SetupDiCreateDeviceInfo 的 DeviceName **不能含反斜杠**；
///   · DriverStore 里的 INF 不是源 INF，直接传给 UpdateDriverForPlugAndPlayDevices 会失败，
///     必须靠 CM_Reenumerate_DevNode 让 PnP 自己匹配。
/// </summary>
public static class PawnIoInstaller
{
    public const string HelperServiceName = "ThermalTunerPawnIoHelper";
    public const string DriverServiceName = "PawnIO";
    private const string HardwareId = @"Root\PawnIO";

    /// <summary>设备是否已可打开（不看驱动服务状态，只看设备对象）。</summary>
    public static bool IsDeviceAvailable()
    {
        var device = PawnIoDevice.TryOpen(out _);
        if (device is null) return false;
        device.Dispose();
        return true;
    }

    /// <summary>驱动包释放目录（同时是 IntelMSR.bin 的用户可替换位置）。</summary>
    public static string DriverDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermalTuner", "driver");

    /// <summary>
    /// 安装驱动。必须在管理员进程中调用。
    /// 若当前进程不是管理员，直接返回 <see cref="PawnIoInstallState.NeedAdmin"/>。
    /// </summary>
    public static PawnIoInstallResult Install(Action<string>? log = null)
    {
        void Log(string s) => log?.Invoke(s);

        if (!IsElevated())
            return new PawnIoInstallResult(PawnIoInstallState.NeedAdmin,
                "安装 PawnIO 驱动需要管理员权限：请以管理员身份重启本程序后重试。");

        if (IsDeviceAvailable())
            return new PawnIoInstallResult(PawnIoInstallState.Ready, "PawnIO 设备已可用，无需安装。");

        // ① 释放驱动包
        string inf;
        try
        {
            inf = ExtractDriverPackage();
            Log($"驱动包已释放：{Path.GetDirectoryName(inf)}");
        }
        catch (Exception ex)
        {
            return new PawnIoInstallResult(PawnIoInstallState.Failed,
                "释放驱动包失败：" + ex.GetBaseException().Message);
        }

        // ② 导入 DriverStore（已导入时 pnputil 会提示已存在，同样算成功）
        Log(RunTool("pnputil.exe", $"/add-driver \"{inf}\" /install", out string addOut));
        Log(addOut.Trim());

        if (!addOut.Contains("成功", StringComparison.Ordinal)
            && !addOut.Contains("success", StringComparison.OrdinalIgnoreCase)
            && !addOut.Contains("已存在", StringComparison.Ordinal)
            && !addOut.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return new PawnIoInstallResult(PawnIoInstallState.Failed,
                "驱动包导入失败：" + FirstLine(addOut) +
                "（常见原因：安全软件拦截驱动安装，或驱动签名不被本机策略信任）");
        }

        // ③ 服务已注册（例如重启后设备节点仍在）→ 只需启动驱动服务
        if (DriverServiceRegistered())
        {
            Log("检测到 PawnIO 驱动服务已注册，尝试直接启动。");
            StartDriverService();
            if (WaitForDevice(TimeSpan.FromSeconds(4)))
                return new PawnIoInstallResult(PawnIoInstallState.Installed, "PawnIO 设备已就绪。");
        }

        // ④ 创建设备节点（需要 SYSTEM 身份）
        Log("正在以 LocalSystem 身份创建设备节点…");
        var (serviceOk, serviceDetail) = RunInstallHelperService();
        Log(serviceDetail);

        if (WaitForDevice(TimeSpan.FromSeconds(5)))
            return new PawnIoInstallResult(PawnIoInstallState.Installed,
                "PawnIO 驱动安装完成，设备已就绪。");

        // 设备还没出来：重枚举 Root 总线让 PnP 绑定
        ReenumerateRootBus();
        if (WaitForDevice(TimeSpan.FromSeconds(8)))
            return new PawnIoInstallResult(PawnIoInstallState.Installed,
                "PawnIO 驱动安装完成（PnP 重枚举后生效）。");

        return serviceOk
            ? new PawnIoInstallResult(PawnIoInstallState.NeedsReboot,
                "驱动与设备节点已创建，但设备尚未激活：请重启电脑后再次打开本程序。")
            : new PawnIoInstallResult(PawnIoInstallState.Failed,
                "设备节点创建失败：" + serviceDetail +
                "（如安全软件弹出拦截提示，请选择「允许」后重试）");
    }

    // ---------------- 服务模式（LocalSystem 执行设备安装） ----------------

    private static (bool Ok, string Detail) RunInstallHelperService()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, "无法定位自身 exe，无法以系统身份安装设备节点。");

        IntPtr scm = OpenSCManager(null, null, ScManagerCreateService);
        if (scm == IntPtr.Zero)
            return (false, "打开服务管理器失败（需要管理员权限）。");

        try
        {
            IntPtr stale = OpenService(scm, HelperServiceName, Delete | ServiceStop);
            if (stale != IntPtr.Zero) { DeleteService(stale); CloseServiceHandle(stale); Thread.Sleep(300); }

            IntPtr svc = CreateService(scm, HelperServiceName, HelperServiceName, ServiceAllAccess,
                ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal,
                $"\"{exe}\" --pawnio-svc", null, IntPtr.Zero, null, null, null);
            if (svc == IntPtr.Zero)
                return (false, $"创建安装服务失败（Win32 {Marshal.GetLastWin32Error()}）。");

            try
            {
                if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                    return (false, $"启动安装服务失败（Win32 {Marshal.GetLastWin32Error()}）。");

                for (int i = 0; i < 120; i++)
                {
                    Thread.Sleep(500);
                    var st = new ServiceStatus();
                    if (QueryServiceStatus(svc, ref st) && st.CurrentState == ServiceStopped) break;
                }
                return (true, "安装服务已执行完毕。");
            }
            finally
            {
                DeleteService(svc);
                CloseServiceHandle(svc);
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// 服务模式入口：由 SCM 以 LocalSystem 启动，创建 Root 设备节点后退出。
    /// 在主程序启动流程里拦截 `--pawnio-svc` 调用本方法。
    /// </summary>
    public static int RunServiceMode()
    {
        var serviceMain = new ServiceTableEntryDelegate(ServiceMainCallback);
        IntPtr table = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceTableEntry>() * 2);
        try
        {
            var entry0 = new ServiceTableEntry
            {
                Name = Marshal.StringToHGlobalUni(HelperServiceName),
                Proc = Marshal.GetFunctionPointerForDelegate(serviceMain)
            };
            var entry1 = new ServiceTableEntry { Name = IntPtr.Zero, Proc = IntPtr.Zero };
            Marshal.StructureToPtr(entry0, table, false);
            Marshal.StructureToPtr(entry1, IntPtr.Add(table, Marshal.SizeOf<ServiceTableEntry>()), false);

            if (!StartServiceCtrlDispatcher(table)) return 30;
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            GC.KeepAlive(serviceMain);
        }
    }

    private static ServiceControlHandler? _handlerDelegate;

    private static void ServiceMainCallback(int argc, IntPtr argv)
    {
        IntPtr status = RegisterServiceCtrlHandlerEx(HelperServiceName,
            _handlerDelegate = (c, t, d, x) => 0, IntPtr.Zero);
        ReportStatus(status, 2);   // START_PENDING
        ReportStatus(status, 4);   // RUNNING

        try
        {
            // 清掉历史幽灵实例（早期 HWID 结构错误会留下多个 000N 节点）
            for (int i = 0; i < 16; i++)
            {
                RunTool("pnputil.exe", $"/remove-device \"ROOT\\PAWNIO\\{i:D4}\"", out _);
            }
            Thread.Sleep(400);

            CreateRootDevice();
            Thread.Sleep(600);
            ReenumerateRootBus();
            StartDriverService();
        }
        catch { /* 失败由上层用「设备是否可打开」判定 */ }
        finally
        {
            ReportStatus(status, 1);   // STOPPED
        }
    }

    /// <summary>创建 Root 枚举设备节点并绑定驱动（必须在 SYSTEM 下运行）。</summary>
    private static bool CreateRootDevice()
    {
        string inf = Path.Combine(DriverDirectory, "pawnio.inf");
        if (!File.Exists(inf)) return false;

        var classGuid = Guid.Empty;
        var className = new StringBuilder(256);
        if (!SetupDiGetINFClass(inf, ref classGuid, className, 256, out _)) return false;

        bool created = false;
        foreach (string name in new[] { "PawnIO" })
        {
            IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;

            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            if (!SetupDiCreateDeviceInfo(set, name, ref classGuid, "PawnIO", IntPtr.Zero, 1, ref data))
            {
                int err = Marshal.GetLastWin32Error();
                SetupDiDestroyDeviceInfoList(set);
                // ERROR_DEVINST_ALREADY_EXISTS(293)：节点已存在，直接进入重枚举绑定
                if (err == 293) { created = true; }
                if (!created) continue;
                break;
            }

            // REG_MULTI_SZ 必须双 NULL 结尾；本 P/Invoke 已显式 CharSet.Unicode。
            byte[] hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            SetupDiSetDeviceRegistryProperty(set, ref data, 0x00000001, hwid, (uint)hwid.Length);
            SetupDiCallClassInstaller(0x00000019, set, ref data);   // DIF_REGISTERDEVICE
            SetupDiDestroyDeviceInfoList(set);
            created = true;
            break;
        }

        return created;
    }

    // ---------------- 工具方法 ----------------

    private static string ExtractDriverPackage()
    {
        Directory.CreateDirectory(DriverDirectory);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string file in new[] { "pawnio.inf", "PawnIO.sys", "PawnIO.cat" })
        {
            string target = Path.Combine(DriverDirectory, file);
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("driver." + file, StringComparison.OrdinalIgnoreCase));
            if (name is null) throw new FileNotFoundException($"内置资源缺少 {file}（安装包不完整）");
            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException($"无法读取内置资源 {file}");
            using var fs = File.Create(target);
            stream.CopyTo(fs);
        }
        return Path.Combine(DriverDirectory, "pawnio.inf");
    }

    private static bool DriverServiceRegistered()
    {
        IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, DriverServiceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    private static bool StartDriverService()
    {
        IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, DriverServiceName, ServiceStart | ServiceQueryStatus);
            if (svc == IntPtr.Zero) return false;
            try
            {
                if (!StartService(svc, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) return false;
                Thread.Sleep(400);
                return true;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static bool WaitForDevice(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (IsDeviceAvailable()) return true;
            Thread.Sleep(300);
        }
        return IsDeviceAvailable();
    }

    private static void ReenumerateRootBus()
    {
        if (CM_Locate_DevNodeW(out uint node, @"HTREE\ROOT\0", 0) == 0)
            CM_Reenumerate_DevNode(node, 0x1 | 0x2);
    }

    private static string RunTool(string exe, string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p is null) { output = string.Empty; return $"{exe} {args} → 无法启动"; }
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return $"{exe} {args} → 退出码 {p.ExitCode}";
        }
        catch (Exception ex)
        {
            output = string.Empty;
            return $"{exe} {args} → {ex.GetBaseException().Message}";
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "（无输出）";

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static void ReportStatus(IntPtr handle, int state)
    {
        if (handle == IntPtr.Zero) return;
        var status = new ServiceStatus { CurrentState = (uint)state, Win32ExitCode = 0 };
        SetServiceStatus(handle, ref status);
    }

    // ---------------- P/Invoke ----------------

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceStopped = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private delegate void ServiceTableEntryDelegate(int argc, IntPtr argv);
    private delegate int ServiceControlHandler(int control, int eventType, IntPtr data, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access,
        uint type, uint start, uint error, string path, string? group, IntPtr tag,
        string? deps, string? account, string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name,
        ServiceControlHandler handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher(IntPtr table);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetINFClass(string inf, ref Guid classGuid,
        StringBuilder className, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr owner);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classGuid,
        string description, IntPtr owner, uint flags, ref SpDevInfoData data);

    // 关键：必须显式 CharSet.Unicode，否则绑定 ANSI 版会把 REG_MULTI_SZ 拆成逐字符条目。
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SpDevInfoData data,
        uint property, byte[] buffer, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SpDevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);
}
