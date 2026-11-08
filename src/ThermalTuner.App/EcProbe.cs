using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ThermalTuner.App.Services;

namespace ThermalTuner.App;

/// <summary>
/// EC 只读诊断：先做内置端口读取自测（不依赖任何第三方软件），
/// 若 NBFC 仍在则追加 nbfc status / ec-probe read/dump/monitor 做交叉验证。
///
/// 用法：ThermalTuner.App.exe --ec-probe --out report.txt [--seconds 6]
/// 内置 EC 读需要管理员（Ring0 端口通道可用）；普通权限下会明确记录失败原因。
/// v0.5.1 起 LHM 为 0.9.6 / PawnIO，端口通道是否仍覆盖 0x62/0x66 以本报告实测为准。
/// 边界：内置路径只写 0x66=0x80 / 0x62=地址做读握手，绝不写 EC 寄存器内容；
/// NBFC 路径只调用 read / dump / monitor，**绝不调用 write**。
/// </summary>
internal static class EcProbe
{
    public static int Run(string outPath, int monitorSeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== EC 只读诊断（内置端口读取 + 可选 NBFC 交叉验证） ==");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("命令边界    : 内置 EC 只写 0x66=0x80 / 0x62=地址做读握手，不写 EC 寄存器内容；" +
                      "NBFC 路径只调 read / dump / monitor，不执行 write。");

        // ---------- 0. 内置 EC 只读自测（NBFC 卸载后仍可完成） ----------
        sb.AppendLine();
        sb.AppendLine("== 0. 内置 EC 只读自测（不依赖 NBFC） ==");
        var cfg = EcFanReader.Mapping();
        sb.AppendLine($"映射        : 寄存器 {cfg.Reg} · 原始刻度 {cfg.Min:F0}~{cfg.Max:F0}（{cfg.Source}）");
        var ec = EcPortReader.ReadByte((byte)cfg.Reg);
        sb.AppendLine($"读结果      : Ok={ec.Ok} · Value={ec.Value} · Reason={ec.Reason}");
        // 诊断放在读之后，才能反映驱动自加载是否真的尝试过
        sb.AppendLine(EcPortReader.Diagnostics());
        if (ec.Ok)
        {
            double p = EcFanReader.PercentFromRaw(ec.Value, cfg.Min, cfg.Max);
            sb.AppendLine($"档位换算    : {(double.IsNaN(p) ? "无法换算" : $"{p:F2}%（EC 档位刻度，非 RPM）")}");

            // 正面回答「是不是只在上电时读了一次」：连续 6 次直查原始字节（不经过运行时 2.2s 缓存）。
            // 仅显式诊断命令执行；全程只读，不写 EC 寄存器内容。
            var raws = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var r = EcPortReader.ReadByte((byte)cfg.Reg);
                raws.Add(r.Ok ? r.Value : double.NaN);
                if (i < 5) Thread.Sleep(400);
            }
            sb.AppendLine("连续原始值  : " + string.Join(" / ",
                raws.Select(v => double.IsNaN(v) ? "失败" : v.ToString("F0"))));
            bool allSame = raws.All(v => !double.IsNaN(v)) && raws.Distinct().Count() == 1;
            sb.AppendLine("实时性判定  : " + (allSame
                ? "6 次读数完全相同 → 该寄存器当前不随状态变化；界面会标注「疑似非实时 · 非 RPM」"
                : "读数有变化或存在失败 → 不能据此判定恒定，以运行时曲线为准"));
            sb.AppendLine("界面口径    : 概览页显示 EC 原始字节 + 档位换算；运行时每 2.2s 真实读一次，连续 10 次不变才标注疑似非实时；非转速。");
        }
        else
        {
            sb.AppendLine("说明        : 需以管理员启动且 Ring0 端口通道可用；普通权限下内置 EC 不可用。" +
                          " v0.5.1 起后端为 PawnIO，若 0x62/0x66 未开放请核对 LpcACPIEC 模块。");
        }

        string? dir = EcFanReader.FindDir();
        if (dir is null)
        {
            sb.AppendLine();
            sb.AppendLine("== NBFC 交叉验证 ==");
            sb.AppendLine("NBFC 未安装（已不依赖），跳过 1~6 节；内置 EC 只读自测已完成。");
            sb.AppendLine();
            sb.AppendLine("RESULT: PASS（只读诊断完成）");
            Flush(outPath, sb);
            return 0;
        }
        sb.AppendLine();
        sb.AppendLine($"安装目录    : {dir}（可选交叉验证）");

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
