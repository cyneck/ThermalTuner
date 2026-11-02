using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ThermalPilot.Core.Environment;

/// <summary>
/// 硬件环境探测器（Hardware Probe）
/// 目标：在 Windows / Linux / macOS 上以零特权、零三方依赖方式识别 CPU 平台画像。
/// 探测失败时静默降级到保守默认值，绝不抛出异常 —— 广泛兼容优先于精确识别。
/// </summary>
public static class HardwareProbe
{
    /// <summary>执行一次环境探测。幂等且只读。</summary>
    public static CpuEnvironment Probe()
    {
        var notes = new List<string>();
        string brand = DetectBrandString(notes);
        var vendor = ParseVendor(brand);
        int generation = 0;
        string suffix = string.Empty;
        bool hybrid = false;
        var form = CpuFormFactor.Unknown;

        try
        {
            if (vendor == CpuVendor.Intel)
            {
                (generation, suffix) = ParseIntelModel(brand, notes);
                hybrid = generation >= 12 || generation >= 100; // 12 代起混合架构，Core Ultra 全系混合
                form = InferFormFactor(suffix);
            }
            else if (vendor == CpuVendor.Amd)
            {
                suffix = ParseAmdSuffix(brand, notes);
                form = InferFormFactor(suffix);
            }
        }
        catch (Exception ex)
        {
            notes.Add($"型号解析异常，回退保守识别: {ex.Message}");
        }

        int logical = System.Environment.ProcessorCount;
        int physical = DetectPhysicalCores(logical, notes);

        return new CpuEnvironment
        {
            BrandString = brand,
            Vendor = vendor,
            Generation = generation,
            Suffix = suffix,
            FormFactor = form,
            IsHybridArchitecture = hybrid,
            LogicalCores = logical,
            PhysicalCores = physical,
            OsPlatform = DetectOsPlatform(),
            IsVirtualized = DetectVirtualization(brand),
            Notes = notes
        };
    }

    // ---------------- 品牌字符串采集 ----------------

    private static string DetectBrandString(List<string> notes)
    {
        // 1. Windows 注册表（最准确，普通用户可读）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string regName && !string.IsNullOrWhiteSpace(regName))
                    return regName.Trim();
                notes.Add("注册表 ProcessorNameString 缺失，尝试环境变量回退");
            }
            catch (Exception ex)
            {
                notes.Add($"注册表读取失败({ex.GetType().Name})，尝试环境变量回退");
            }

            var envId = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(envId))
                return envId.Trim();
        }

        // 2. Linux /proc/cpuinfo
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf(':');
                        if (idx >= 0 && idx + 1 < line.Length)
                        {
                            var name = line[(idx + 1)..].Trim();
                            if (!string.IsNullOrWhiteSpace(name))
                                return name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"/proc/cpuinfo 读取失败({ex.GetType().Name})");
        }

        // 3. macOS sysctl
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/sbin/sysctl",
                    Arguments = "-n machdep.cpu.brand_string",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(1000);
                    if (!string.IsNullOrWhiteSpace(output))
                        return output;
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"sysctl 读取失败({ex.GetType().Name})");
        }

        notes.Add("品牌字符串探测完全失败，使用 Unknown 兜底");
        return "Unknown CPU";
    }

    private static string DetectOsPlatform()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }

    private static bool DetectVirtualization(string brand)
    {
        string b = brand.ToUpperInvariant();
        return b.Contains("VIRTUAL") || b.Contains("KVM") || b.Contains("VMWARE") ||
               b.Contains("HYPER-V") || b.Contains("VIRTUALBOX") || b.Contains("XEN") ||
               b.Contains("QEMU") || b.Contains("PARALLELS");
    }

    private static int DetectPhysicalCores(int logical, List<string> notes)
    {
        // 环境变量常见约定（部分容器/虚拟化平台会注入）；失败则假设无超线程做保守估计
        var env = System.Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS");
        if (int.TryParse(env, out int n) && n > 0 && n <= logical)
            return n;
        // 无更多信息时按超线程 2:1 估计下界
        return Math.Max(1, logical / 2);
    }

    // ---------------- 型号解析 ----------------

    private static CpuVendor ParseVendor(string brand)
    {
        string b = brand.ToUpperInvariant();
        if (b.Contains("INTEL")) return CpuVendor.Intel;
        if (b.Contains("AMD") || b.Contains("RYZEN") || b.Contains("ATHLON") || b.Contains("EPYC")) return CpuVendor.Amd;
        if (b.Contains("ARM") || b.Contains("APPLE") || b.Contains("SNAPDRAGON") || b.Contains("CORTEX")) return CpuVendor.Arm;
        return CpuVendor.Unknown;
    }

    /// <summary>
    /// Intel 型号解析：
    ///   i7-7700HQ → 代数 7, 后缀 HQ
    ///   i7-12700H → 代数 12, 后缀 H
    ///   Core Ultra 7 255H → 代数 200 (Arrow Lake), 后缀 H
    ///   Core Ultra 7 155H → 代数 100 (Meteor Lake), 后缀 H
    /// </summary>
    private static (int Generation, string Suffix) ParseIntelModel(string brand, List<string> notes)
    {
        // Core Ultra 系列（Meteor Lake / Arrow Lake / Lunar Lake）
        var ultra = Regex.Match(brand, @"Ultra\s+\d+\s+(\d{3})\s*([A-Za-z]*)");
        if (ultra.Success)
        {
            int model = int.Parse(ultra.Groups[1].Value);
            string suf = ultra.Groups[2].Value.Trim().ToUpperInvariant();
            int gen = model >= 200 ? 200 : 100;
            notes.Add($"识别为 Core Ultra 系列 (model={model}) → 代数编码 {gen}");
            return (gen, suf);
        }

        // 经典 i3/i5/i7/i9-XXXX / iX-XXXXX
        var core = Regex.Match(brand, @"i[3579][\s\-]*(\d{4,5})\s*([A-Za-z]*)");
        if (core.Success)
        {
            string digits = core.Groups[1].Value;
            int gen = digits.Length == 4
                ? digits[0] - '0'            // 7700 → 7
                : int.Parse(digits[..^3]);   // 12700 → 12; 10750 → 10
            string suf = core.Groups[2].Value.Trim().ToUpperInvariant();
            return (gen, suf);
        }

        // Xeon / 其他：尝试 E5-vN 或 4 位数字
        var generic = Regex.Match(brand, @"(\d{4,5})\s*([A-Za-z]*)$");
        if (generic.Success)
        {
            string digits = generic.Groups[1].Value;
            int gen = digits.Length == 4 ? digits[0] - '0' : int.Parse(digits[..^3]);
            return (Math.Max(0, gen), generic.Groups[2].Value.Trim().ToUpperInvariant());
        }

        notes.Add("Intel 型号未能解析代数，按保守未知处理");
        return (0, string.Empty);
    }

    private static string ParseAmdSuffix(string brand, List<string> notes)
    {
        var m = Regex.Match(brand, @"(\d{4})\s*([A-Za-z]*)");
        if (m.Success)
            return m.Groups[2].Value.Trim().ToUpperInvariant();
        notes.Add("AMD 型号未能解析后缀");
        return string.Empty;
    }

    private static CpuFormFactor InferFormFactor(string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return CpuFormFactor.Unknown;
        if (suffix is "U" or "UE" or "Y" or "G7" or "G4")
            return CpuFormFactor.MobileUltraLowPower;
        if (suffix is "P" or "G1" or "G2" or "G3" or "G5" or "G6")
            return CpuFormFactor.MobileMainstream;
        if (suffix.StartsWith("H") || suffix is "HS" or "HX" or "HK")
            return CpuFormFactor.MobilePerformance;
        if (suffix is "K" or "KF" or "F" or "T" or "S" or "X" or "XE" or "E")
            return CpuFormFactor.Desktop;
        return CpuFormFactor.Unknown;
    }
}
