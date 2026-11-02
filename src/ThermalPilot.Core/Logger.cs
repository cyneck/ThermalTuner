using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ThermalPilot.Core;

/// <summary>轻量日志：写文件（%ProgramData%\ThermalPilot\logs）+ 推给 UI。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Pending = new();
    private static bool _started;

    public static string LogDirectory { get; } = Path.Combine(Paths.DataDirectory, "logs");

    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERR ", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                EnsureStarted();
                File.AppendAllText(CurrentFile(), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }

        try { LineWritten?.Invoke(line); } catch { }
    }

    private static void EnsureStarted()
    {
        if (_started) return;
        Directory.CreateDirectory(LogDirectory);
        _started = true;
    }

    private static string CurrentFile() => Path.Combine(LogDirectory, $"thermaltpilot-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>清理 14 天前的日志。</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (var f in Directory.GetFiles(LogDirectory, "thermaltpilot-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
            }
        }
        catch { }
    }
}

/// <summary>统一的运行期路径。</summary>
public static class Paths
{
    static Paths()
    {
        // 默认 %ProgramData%\ThermalPilot；不可写时退化到 %LOCALAPPDATA%\ThermalPilot
        string programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ThermalPilot");
        try
        {
            Directory.CreateDirectory(programData);
            string probe = Path.Combine(programData, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            DataDirectory = programData;
        }
        catch
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalPilot");
            Directory.CreateDirectory(DataDirectory);
        }
    }

    public static string DataDirectory { get; }
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string ReportFile => Path.Combine(DataDirectory, "support-report.txt");
}

internal static class Shell
{
    /// <summary>执行外部命令并返回 stdout+stderr。</summary>
    public static string Run(string fileName, string arguments, int timeoutMs = 8000)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return sb.ToString();
        }
        return sb.ToString();
    }
}
