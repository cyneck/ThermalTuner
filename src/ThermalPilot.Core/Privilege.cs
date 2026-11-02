using System.Security.Principal;
using Microsoft.Win32;

namespace ThermalPilot.Core;

/// <summary>进程权限与内核隔离状态（集中判定，避免各处重复实现）。</summary>
public static class Privilege
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>HVCI（内存完整性）是否开启——开启则第三方内核驱动无法加载。</summary>
    public static bool HvciEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                return key?.GetValue("Enabled") is int v && v == 1;
            }
            catch { return false; }
        }
    }
}
