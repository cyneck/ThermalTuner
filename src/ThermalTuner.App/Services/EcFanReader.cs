using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ThermalTuner.App.Services;

/// <summary>
/// NBFC / ec-probe 只读访问层。
///
/// 设计边界（重要）：
/// · 本类只调用 nbfc.exe status 与 ec-probe.exe 的 read / dump / monitor，
///   **绝不调用 ec-probe.exe write**，绝不写 EC 寄存器。
/// · 后期的「风扇微调」属于控制层，应单独实现并显式授权；这里只负责采集与自动判断，
///   同时把生效配置里的 Read/Write/Min/Max 寄存器信息暴露出来供控制层复用。
/// · ec-probe 需要管理员才能加载 EC 插件；普通权限下只返回提示，不阻断主链路。
/// </summary>
internal static class EcFanReader
{
    private static readonly string[] Candidates =
    {
        @"C:\Program Files (x86)\NoteBook FanControl",
        @"C:\Program Files\NoteBook FanControl",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "NoteBook FanControl")
    };

    /// <summary>自动定位 NBFC 安装目录（nbfc.exe + ec-probe.exe 同时存在才算可用）。</summary>
    public static string? FindDir() => Candidates.FirstOrDefault(d =>
        File.Exists(Path.Combine(d, "nbfc.exe")) && File.Exists(Path.Combine(d, "ec-probe.exe")));

    public static string NbfcExe(string dir) => Path.Combine(dir, "nbfc.exe");
    public static string ProbeExe(string dir) => Path.Combine(dir, "ec-probe.exe");

    /// <summary>同步执行外部只读命令，超时即终止；任何异常都转成文本，不抛出。</summary>
    public static (int Exit, string Out) Run(string exe, string args, int timeoutMs = 6000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "进程启动失败");

            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* 已被系统回收 */ }
                return (-2, "执行超时");
            }
            return (p.ExitCode, so + (string.IsNullOrWhiteSpace(se) ? "" : "\n[stderr]\n" + se));
        }
        catch (Exception ex)
        {
            return (-3, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>NBFC 服务状态原始文本（只读）。</summary>
    public static string Status(string dir) => Run(NbfcExe(dir), "status", 3000).Out;

    /// <summary>从 "Key : Value" 格式的状态文本里取一项。</summary>
    public static string? Value(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(':');
            if (i < 0) continue;
            if (line[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(i + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// 自动判断：从 status 里的「Selected config name」定位生效配置 XML，
    /// 解析风扇读/写寄存器与 Min/Max 速度值。不硬编码寄存器号。
    /// </summary>
    public static (string ConfigPath, string Model, int ReadReg, int WriteReg,
        double MinValue, double MaxValue, string FanName, int PollMs)? Definition(string dir, string statusText)
    {
        try
        {
            string name = Value(statusText, "Selected config name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return null;
            string path = Path.Combine(dir, "Configs", name + ".xml");
            if (!File.Exists(path)) return null;

            var doc = XDocument.Load(path);
            var fan = doc.Descendants("FanConfiguration").FirstOrDefault();
            if (fan is null) return null;

            static int Int(XElement e, string n, int d) =>
                int.TryParse(e.Element(n)?.Value, out int v) ? v : d;
            static double Dbl(XElement e, string n, double d) =>
                double.TryParse(e.Element(n)?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : d;

            return (
                path,
                doc.Root?.Element("NotebookModel")?.Value ?? "未知机型",
                Int(fan, "ReadRegister", -1),
                Int(fan, "WriteRegister", -1),
                Dbl(fan, "MinSpeedValue", double.NaN),
                Dbl(fan, "MaxSpeedValue", double.NaN),
                fan.Element("FanDisplayName")?.Value ?? "CPU fan",
                Int(doc.Root!, "EcPollInterval", 2222));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读单个 EC 寄存器（只读）；需要管理员。</summary>
    public static (int Exit, string Out) ReadRegister(string dir, int reg) =>
        Run(ProbeExe(dir), $"read {reg}", 4000);

    /// <summary>dump 全部 EC 寄存器（只读）；需要管理员。</summary>
    public static (int Exit, string Out) Dump(string dir) =>
        Run(ProbeExe(dir), "dump", 6000);

    /// <summary>短时监控 EC 变化寄存器并落盘 CSV（只读）；用于自动判断哪些寄存器活跃。</summary>
    public static (int Exit, string Out, string CsvPath) Monitor(string dir, int seconds, string csvPath)
    {
        var r = Run(ProbeExe(dir), $"monitor -t {seconds} -i 1 -d -r \"{csvPath}\"",
            (seconds + 4) * 1000);
        return (r.Exit, r.Out, csvPath);
    }

    /// <summary>
    /// NBFC 的百分比换算：EC 原始值在 [MinSpeedValue, MaxSpeedValue] 之间线性映射到 0~100%。
    /// 配置里 Min=255 / Max=58 是反向编码（值越小风扇越快），公式对反向同样成立。
    /// </summary>
    // 换算与解析放在 Core，便于单元测试守护（防止把错误的百分比显示给用户）。
    public static double PercentFromRaw(double raw, double min, double max) =>
        ThermalTuner.Core.Taxonomy.FanPercentCodec.FromRaw(raw, min, max);

    /// <summary>从 ec-probe 输出里取第一个十进制或 0x 十六进制数。</summary>
    public static double FirstNumber(string text) =>
        ThermalTuner.Core.Taxonomy.FanPercentCodec.ParseFirstNumber(text);

    // ---------------- 运行时只读采集（低频缓存） ----------------

    private static readonly object Gate = new();
    private static FanReading _cached = new(double.NaN, "", double.NaN, "尚未采集");
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromMilliseconds(2200);
    private static bool _ecUsable = true;

    /// <summary>
    /// 风扇运行百分比（只读）。优先 EC 直读（管理员，最贴近真实），
    /// 回退 NBFC 服务已换算的 status（普通权限可用），再回退 N/A。
    /// 每 2.2s 最多刷新一次（与 NBFC 的 EcPollInterval 对齐），避免每拍启动外部进程。
    /// </summary>
    public static FanReading ReadRuntime()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cachedAt < Ttl) return _cached;
            _cachedAt = DateTime.UtcNow;

            string? dir = FindDir();
            if (dir is null)
            {
                _cached = new FanReading(double.NaN, "", double.NaN, "未安装 NBFC");
                return _cached;
            }

            string status = Status(dir);
            var def = Definition(dir, status);
            if (def is { } d && d.ReadReg >= 0 && _ecUsable)
            {
                var r = ReadRegister(dir, d.ReadReg);
                bool ok = r.Exit == 0 &&
                          !r.Out.Contains("Could not load EC plugin", StringComparison.OrdinalIgnoreCase);
                double raw = ok ? FirstNumber(r.Out) : double.NaN;
                double pct = PercentFromRaw(raw, d.MinValue, d.MaxValue);
                if (!double.IsNaN(pct))
                {
                    _cached = new FanReading(pct, "EC 直读 · 只读", raw,
                        $"寄存器 {d.ReadReg} = {raw:F0}，按 {d.MinValue:F0}/{d.MaxValue:F0} 换算");
                    return _cached;
                }
                _ecUsable = false;   // 普通权限下不再反复尝试；提权重启后进程内静态字段会重置
            }

            string reported = Value(status, "Current fan speed") ?? "";
            if (double.TryParse(reported, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && v >= 0 && v <= 100)
            {
                _cached = new FanReading(v, "NBFC 服务 · 只读", double.NaN,
                    "服务按生效配置读 EC 并换算（与 ec-probe 直读一致）");
                return _cached;
            }

            _cached = new FanReading(double.NaN, "", double.NaN, "NBFC 状态不可解析");
            return _cached;
        }
    }
}

/// <summary>一次只读风扇读数：百分比、来源、EC 原始值、说明。</summary>
public sealed record FanReading(double Percent, string Source, double RawValue, string Detail);
