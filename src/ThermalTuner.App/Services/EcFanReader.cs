using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App.Services;

/// <summary>
/// 风扇 EC 只读采集层。
///
/// 优先级（当前实现）：
///   ① 内置 EC 只读（EcPortReader，不依赖任何第三方软件）→
///   ② NBFC / ec-probe（若仍安装，用于交叉验证或普通权限回退）→
///   ③ N/A。
///
/// 设计边界（重要）：
/// · 只读：绝不写 EC 寄存器内容，绝不写风扇控制寄存器（WriteRegister=148）。
///   内置端口读只包含向 0x66/0x62 写「读命令 + 地址」的必要握手，不写数据。
/// · NBFC 路径只调用 nbfc.exe status 与 ec-probe.exe 的 read / dump / monitor，
///   **绝不调用 ec-probe.exe write**。
/// · 后期的「风扇微调」属于控制层，应单独实现并显式授权；这里只负责采集与自动判断。
/// · 内置 EC 读与 ec-probe 一样需要管理员（Ring0 端口通道可用）；普通权限下优雅回退。
/// · 读到的值是 EC 档位刻度（0~255 原始值归一化），**不是物理转速 RPM**。
/// · 采集层同时做原始值恒定检测（FanStagnationDetector）：连续 10 次真实采样不变
///   即随读数上报「疑似非实时」，界面必须显示原始字节并加标注，不得当成实时转速。
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
    /// EC 原始值在 [MinSpeedValue, MaxSpeedValue] 之间线性映射到 0~100%。
    /// 常见机型是反向编码（如 Min=255 停转、Max=58 满速），公式对反向同样成立。
    /// </summary>
    public static double PercentFromRaw(double raw, double min, double max) =>
        ThermalTuner.Core.Taxonomy.FanPercentCodec.FromRaw(raw, min, max);

    /// <summary>从 ec-probe 输出里取第一个十进制或 0x 十六进制数。</summary>
    public static double FirstNumber(string text) =>
        ThermalTuner.Core.Taxonomy.FanPercentCodec.ParseFirstNumber(text);

    // ---------------- 内置 EC 只读映射 ----------------

    private static (int Reg, double Min, double Max, string Source)? _mapping;

    /// <summary>
    /// 内置 EC 读的寄存器与刻度映射。默认值来自本机 HP OMEN Notebook PC 15 的
    /// NBFC 生效配置（读寄存器 149 / Min=255 / Max=58），仅作为档位刻度换算，
    /// 不是瓦数常量；换机型可用 settings.json 的三个 FanEc* 字段覆盖，
    /// 或用 --ec-probe 报告核对。进程内只读取一次配置。
    /// </summary>
    public static (int Reg, double Min, double Max, string Source) Mapping()
    {
        if (_mapping is not null) return _mapping.Value;
        var s = SettingsStore.Load();
        bool custom = s.FanEcRegister > 0 || s.FanEcMinRaw > 0 || s.FanEcMaxRaw > 0;
        var v = (
            Reg: Math.Clamp(s.FanEcRegister > 0 ? s.FanEcRegister : 149, 0, 255),
            Min: s.FanEcMinRaw > 0 ? s.FanEcMinRaw : 255.0,
            Max: s.FanEcMaxRaw > 0 ? s.FanEcMaxRaw : 58.0,
            Source: custom ? "settings.json 自定义" : "本机校准默认 · HP OMEN Notebook PC 15");
        _mapping = v;
        return v;
    }

    // ---------------- 运行时只读采集（低频缓存） ----------------

    private static readonly object Gate = new();
    private static FanReading _cached = new(double.NaN, "", double.NaN, "尚未采集");
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromMilliseconds(2200);
    private static bool _ecUsable = true;

    /// <summary>
    /// 原始值恒定检测。只在缓存失效、真正读了一次 EC 时推进（约 2.2s 一拍），
    /// 缓存命中直接返回上次结果，不会把界面刷新次数当成新的采样。
    /// </summary>
    private static readonly FanStagnationDetector Stagnation = new();

    private static (int Count, bool Flag) Track(double raw)
    {
        Stagnation.Observe(raw);
        return (Stagnation.Consecutive, Stagnation.IsStagnant);
    }

    /// <summary>
    /// 风扇档位（只读，非 RPM）。优先内置 EC 直读（不依赖 NBFC），
    /// 再回退 NBFC ec-probe / status，全不可用时显示 N/A。
    /// 每 2.2s 最多刷新一次，降低与 BIOS 争用 EC 端口的概率。
    /// </summary>
    public static FanReading ReadRuntime()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cachedAt < Ttl) return _cached;
            _cachedAt = DateTime.UtcNow;

            // ① 内置 EC 只读：NBFC 卸载后仍可用；只做读握手，不写 EC 寄存器内容
            var cfg = Mapping();
            var ec = EcPortReader.ReadByte((byte)cfg.Reg);
            if (ec.Ok)
            {
                double pct = PercentFromRaw(ec.Value, cfg.Min, cfg.Max);
                if (!double.IsNaN(pct))
                {
                    var t = Track(ec.Value);
                    _cached = new FanReading(pct, "内置 EC 只读 · 档位刻度", ec.Value,
                        $"EC 寄存器 {cfg.Reg} = {ec.Value}，按 {cfg.Min:F0}/{cfg.Max:F0} 换算（{cfg.Source}）；非转速",
                        t.Count, t.Flag);
                    return _cached;
                }
            }

            // ② 可选 NBFC 交叉验证 / 普通权限回退
            string? dir = FindDir();
            if (dir is not null)
            {
                string status = Status(dir);
                var def = Definition(dir, status);
                if (def is { } d && d.ReadReg >= 0 && _ecUsable)
                {
                    var r = ReadRegister(dir, d.ReadReg);
                    bool ok = r.Exit == 0 &&
                              !r.Out.Contains("Could not load EC plugin", StringComparison.OrdinalIgnoreCase);
                    double raw = ok ? FirstNumber(r.Out) : double.NaN;
                    double nbPct = PercentFromRaw(raw, d.MinValue, d.MaxValue);
                    if (!double.IsNaN(nbPct))
                    {
                        var t = Track(raw);
                        _cached = new FanReading(nbPct, "NBFC ec-probe · 只读", raw,
                            $"寄存器 {d.ReadReg} = {raw:F0}，按 {d.MinValue:F0}/{d.MaxValue:F0} 换算",
                            t.Count, t.Flag);
                        return _cached;
                    }
                    _ecUsable = false;   // 普通权限下不再反复尝试
                }

                string reported = Value(status, "Current fan speed") ?? "";
                if (double.TryParse(reported, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && v >= 0 && v <= 100)
                {
                    // 服务只给换算后的百分比、没有原始字节，无法做恒定判定 → 重置检测。
                    Track(double.NaN);
                    _cached = new FanReading(v, "NBFC 服务 · 只读", double.NaN,
                        "服务按生效配置读 EC 并换算（与内置 EC 直读互为交叉验证）", 0, false);
                    return _cached;
                }
            }

            Track(double.NaN);   // 数据源中断，恢复后从 0 重新计数
            _cached = new FanReading(double.NaN, "", double.NaN,
                "EC 只读不可用：" + (ec.Ok ? "刻度换算失败" : ec.Reason) +
                "；NBFC " + (dir is null ? "未安装（已不依赖）" : "状态不可解析"), 0, false);
            return _cached;
        }
    }
}

/// <summary>
/// 一次只读风扇读数：档位百分比（非 RPM）、来源、EC 原始值、说明，
/// 以及「原始值连续不变次数」与「疑似非实时」判定（只在真实采样时推进）。
/// </summary>
public sealed record FanReading(
    double Percent,
    string Source,
    double RawValue,
    string Detail,
    int StagnantReads = 0,
    bool Stagnant = false);
