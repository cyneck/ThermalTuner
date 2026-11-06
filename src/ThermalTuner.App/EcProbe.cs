using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ThermalTuner.App.Services;

namespace ThermalTuner.App;

/// <summary>
/// EC 自动判断（只读）：自动定位 NBFC → 读取生效配置 → 取风扇寄存器 →
/// 用 ec-probe.exe read/dump/monitor 读 EC → 判定读数是否活跃、配置是否可信。
///
/// 用法：ThermalTuner.App.exe --ec-probe --out report.txt [--seconds 6]
/// 需要管理员（ec-probe 加载 EC 插件要求提权）；普通权限下会明确记录失败原因。
/// 本命令只调用 ec-probe 的 read / dump / monitor，**绝不调用 write**。
/// </summary>
internal static class EcProbe
{
    public static int Run(string outPath, int monitorSeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== NBFC / EC 只读自动判断 ==");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"命令边界    : 只执行 nbfc status 与 ec-probe read / dump / monitor；不执行 write，不写 EC。");

        string? dir = EcFanReader.FindDir();
        if (dir is null)
        {
            sb.AppendLine("RESULT: SKIP（未找到 NBFC 安装目录，风扇指标回退 LHM 传感器）");
            Flush(outPath, sb);
            return 2;
        }
        sb.AppendLine($"安装目录    : {dir}");

        // ---------- 1. 服务状态 ----------
        string status = EcFanReader.Status(dir);
        sb.AppendLine();
        sb.AppendLine("== 1. NBFC 服务状态（nbfc.exe status，只读） ==");
        sb.AppendLine(status.TrimEnd());

        string configName = EcFanReader.Value(status, "Selected config name") ?? "未知";
        string readOnly = EcFanReader.Value(status, "Read-only") ?? "未知";
        string target = EcFanReader.Value(status, "Target fan speed") ?? "未知";
        string reported = EcFanReader.Value(status, "Current fan speed") ?? "未知";

        // ---------- 2. 读数活跃性 ----------
        sb.AppendLine();
        sb.AppendLine("== 2. 读数活跃性（6 次 / 间隔 1s） ==");
        var fanValues = new List<string>();
        for (int i = 1; i <= 6; i++)
        {
            string s = i == 1 ? status : EcFanReader.Status(dir);
            string t = EcFanReader.Value(s, "Temperature") ?? "--";
            string f = EcFanReader.Value(s, "Current fan speed") ?? "--";
            sb.AppendLine($"  {i}: 温度 {t} °C · Current fan speed {f}");
            fanValues.Add(f);
            if (i < 6) Thread.Sleep(1000);
        }
        bool fanConstant = fanValues.Distinct().Count() == 1;
        sb.AppendLine($"判定        : Current fan speed {(fanConstant ? "6 次完全不变（不可作为实时转速依据）" : "有变化（可能是实时读数）")}；" +
                      $"Read-only={readOnly} · Target={target}");

        // ---------- 3. 自动定位生效配置 ----------
        sb.AppendLine();
        sb.AppendLine("== 3. 自动判断生效配置与 EC 寄存器 ==");
        var def = EcFanReader.Definition(dir, status);
        if (def is not { } d)
        {
            sb.AppendLine("无法定位或解析生效配置 XML，跳过 EC 读取。");
        }
        else
        {
            sb.AppendLine($"配置文件    : {d.ConfigPath}");
            sb.AppendLine($"配置机型    : {d.Model}");
            sb.AppendLine($"风扇名      : {d.FanName}");
            sb.AppendLine($"读寄存器    : {d.ReadReg}     写寄存器: {d.WriteReg}（本命令不写）");
            sb.AppendLine($"速度编码    : Min={d.MinValue} / Max={d.MaxValue} · EC 轮询 {d.PollMs} ms");
            sb.AppendLine($"NBFC 报告值 : {reported}%（换算所需原始值由下方 read 给出）");

            // ---------- 4. EC 原值 ----------
            sb.AppendLine();
            sb.AppendLine($"== 4. ec-probe read {d.ReadReg}（只读） ==");
            var rr = EcFanReader.ReadRegister(dir, d.ReadReg);
            sb.AppendLine($"退出码 {rr.Exit}:");
            sb.AppendLine(string.IsNullOrWhiteSpace(rr.Out) ? "(无输出)" : rr.Out.TrimEnd());

            bool ecOk = rr.Exit == 0 && !rr.Out.Contains("Could not load EC plugin", StringComparison.OrdinalIgnoreCase);
            if (ecOk)
            {
                double raw = EcFanReader.FirstNumber(rr.Out);
                double pct = EcFanReader.PercentFromRaw(raw, d.MinValue, d.MaxValue);
                sb.AppendLine($"解析原始值  : {(double.IsNaN(raw) ? "无法解析" : raw.ToString("F0"))}");
                sb.AppendLine($"按配置换算  : {(double.IsNaN(pct) ? "无法换算" : pct.ToString("F2") + "%")}");
            }
            else
            {
                sb.AppendLine("EC 插件未加载：请以管理员运行本命令（普通权限无法访问 EC）。");
            }
        }

        // ---------- 5. EC 全量快照 ----------
        sb.AppendLine();
        sb.AppendLine("== 5. ec-probe dump（只读快照，截取前 20 行） ==");
        var dump = EcFanReader.Dump(dir);
        var dumpLines = dump.Out.Split('\n').Take(20).Select(x => x.TrimEnd()).ToArray();
        foreach (var l in dumpLines) sb.AppendLine("  " + l);
        sb.AppendLine($"退出码 {dump.Exit} · 总行数 {dump.Out.Split('\n').Length}");

        // ---------- 6. 变化寄存器 ----------
        sb.AppendLine();
        sb.AppendLine($"== 6. ec-probe monitor {monitorSeconds}s（只读，自动列出变化寄存器） ==");
        string csv = Path.Combine(Path.GetTempPath(), $"thermaltuner-ec-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var mon = EcFanReader.Monitor(dir, monitorSeconds, csv);
        sb.AppendLine($"退出码 {mon.Exit} · CSV: {mon.CsvPath}");
        if (File.Exists(mon.CsvPath))
        {
            var lines = File.ReadAllLines(mon.CsvPath);
            sb.AppendLine($"变化寄存器  : {(lines.Length > 0 ? lines[0] : "(空)")}");
            sb.AppendLine($"采样行数    : {Math.Max(0, lines.Length - 1)}");
            foreach (var l in lines.Skip(1).Take(4)) sb.AppendLine("  " + l);
        }
        if (!string.IsNullOrWhiteSpace(mon.Out))
            sb.AppendLine("控制台输出  : " + mon.Out.Split('\n')[0].Trim());

        // ---------- 结论 ----------
        sb.AppendLine();
        sb.AppendLine("== 结论（供人工判断，不自动改任何参数） ==");
        sb.AppendLine($"· 生效配置为「{configName}」，配置机型见上；与本机主板是否匹配需人工核对。");
        sb.AppendLine($"· NBFC 报告的 Current fan speed = {reported}%；" +
                      (fanConstant
                          ? "多次采样恒定：NBFC 当前为只读模式（Target=0），风扇由 EC/BIOS 自身策略固定在该档，属真实读数而非静态占位。"
                          : "采样中存在变化，可作为实时观测依据。"));
        sb.AppendLine("· 该值应由 ec-probe 直读 EC 原始值、按生效配置 Min/Max 换算来交叉验证；" +
                      "两者一致说明配置里的寄存器映射正确（read 失败时需管理员运行）。");
        sb.AppendLine("· 后期风扇微调属于控制层（需写 WriteRegister），本命令与运行时采集均不写 EC。");
        sb.AppendLine();
        sb.AppendLine("RESULT: PASS（只读自动判断完成）");

        Flush(outPath, sb);
        return 0;
    }

    private static void Flush(string path, StringBuilder sb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(Path.GetFullPath(path), sb.ToString(), new UTF8Encoding(true));
    }
}
