using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThermalPilot.Core;

/// <summary>
/// 笔记本风扇"外挂后端"：NoteBook FanControl（NBFC）CLI 桥接。
///
/// 背景：笔记本/品牌整机的风扇由 EC（嵌入式控制器）私有固件管理，主板 SuperIO 上
/// 并没有标准的 PWM 通道，所以监控库读不到转速、也控不了速。这类机器的通用解法是用
/// NBFC —— 它自带 WinRing0 驱动直接读写 EC，并为每个机型维护一份配置文件
/// （HP OMEN 系列有社区验证过的 "HP OMEN Notebook PC 15" 配置）。
///
/// 设计原则：
///   · 只做"探测 + 可选的桥接调用"，任何一步失败都只记录日志、不影响主引擎；
///   · 不替用户安装驱动（涉及内核驱动与 EC 写入，必须由用户显式确认）。
/// </summary>
public sealed class NbfcBridge
{
    public string? ExePath { get; private set; }
    public bool Installed => ExePath != null;
    public string ConfigName { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public string LastRawOutput { get; private set; } = "";

    /// <summary>把 0–100 的百分比映射到配置的写入寄存器量程（NBFC 常见 0–255）。</summary>
    public int WriteRange { get; set; } = 100;

    // ---------------- EC 写保护 ----------------
    //
    // 每次 `nbfc set -s N` 都会新起一个进程，并通过 WinRing0 向 EC 发一次写事务。
    // 本机实测：风扇曲线以 1 Hz 连续写 EC 时，Windows 的 ACPI EC 驱动会被打到超时，
    // 事件日志出现 ACPI 13「在指定的超时期限内，嵌入式控制器(EC)未响应」；
    // 而**电池电量、是否插电也是经同一个 EC 上报的** —— EC 被写崩的窗口里，
    // 电源驱动读不到电池，用户看到的就是「检测不到电池」。
    //
    // 所以这里必须节流，而不是"只要目标值变了就写"：
    //   · 默认最小间隔 3 s（可在高级页调）；
    //   · 寄存器值没变就不写（消除抖动造成的重复事务）；
    //   · 连续失败 3 次退避 30 s（EC 已经在挣扎时不要再压它）。
    /// <summary>两次 EC 写入之间的最小间隔（毫秒）。低于 1000 会被夹到 1000。</summary>
    public int MinWriteIntervalMs
    {
        get => _minIntervalMs;
        set => _minIntervalMs = Math.Clamp(value, 1000, 10000);
    }
    private int _minIntervalMs = 3000;

    private DateTime _lastSetUtc = DateTime.MinValue;
    private int _lastRaw = -1;
    private int _failStreak;
    private DateTime _backoffUntil = DateTime.MinValue;

    public int WriteCount { get; private set; }
    public int SkippedCount { get; private set; }
    /// <summary>最近一次写入是否真的落到了 EC（false = 被节流/去重跳过）。</summary>
    public bool LastWriteApplied { get; private set; }
    public string LastSkipReason { get; private set; } = "";

    private static readonly string[] CandidatePaths =
    {
        @"C:\Program Files\NoteBook FanControl\nbfc.exe",
        @"C:\Program Files (x86)\NoteBook FanControl\nbfc.exe",
        @"C:\Program Files\NoteBook FanControl\service\nbfc.exe"
    };

    /// <summary>是否已经探测过（避免诊断页每次刷新都起一个进程）。</summary>
    public bool DetectedOnce { get; private set; }

    /// <summary>只探测一次；已探测过直接返回结果。</summary>
    public bool DetectOnce()
    {
        if (DetectedOnce) return Installed;
        return Detect();
    }

    public bool Detect()
    {
        DetectedOnce = true;
        ExePath = CandidatePaths.FirstOrDefault(File.Exists);
        if (ExePath == null)
        {
            // 兜底：PATH 里找
            try
            {
                var p = RunOnPath("nbfc.exe", "--version", 4000);
                if (p.ok) ExePath = "nbfc.exe";
            }
            catch { }
        }

        if (ExePath == null)
        {
            LastError = "未安装 NBFC";
            return false;
        }

        try
        {
            var st = Run("status -a", 6000);
            LastRawOutput = st.output;
            var s = ParseStatus(st.output);
            ConfigName = s.ConfigName;
            // EC 步进数只用于解释回读（Current fan speed 是 0..steps 的原始值）。
            // **写入单位是百分比**：`nbfc set -s` 收 0–100，不是步进 —— 这个区别写错过一次，
            // 结果 75% 被发成 147，越界后 NBFC 静默退回自动曲线。
            if (s.SpeedSteps is int steps && steps > 0) WriteRange = steps;
            Log.Info($"检测到 NBFC：{ExePath}；配置「{(ConfigName.Length > 0 ? ConfigName : "未选择")}」；"
                     + $"服务 {(s.ServiceEnabled == true ? "已启用" : "未启用")}；EC 步进 {WriteRange}"
                     + $"（写入用百分比，回读 Current 用步进）；当前 Auto control="
                     + (s.AutoControl == true ? "开" : s.AutoControl == false ? "关" : "未知") + "。");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读 NBFC 状态。字段来自 `nbfc status -a` 的固定模板（已从 nbfc.exe 的 .NET
    /// 用户字符串里核对，见下面的标签）：
    ///   Service enabled\t\t: {0} / Read-only\t\t: {0} / Selected config name\t: {0}
    ///   Temperature\t\t: {0} / Fan display name\t: {0}
    ///   Current fan speed\t: {0:0.00} / Target fan speed\t: {0:0.00} / Fan speed steps\t\t: {0}
    /// </summary>
    public NbfcStatus TryReadStatus()
    {
        var st = new NbfcStatus();
        if (!Installed) return st;
        var r = Run("status -a", 5000);
        st.Raw = r.output;
        if (!r.ok) { LastError = r.output; return st; }
        return ParseStatus(r.output);
    }

    internal static NbfcStatus ParseStatus(string text)
    {
        var s = new NbfcStatus { Raw = text };

        string? Field(string label)
        {
            var m = Regex.Match(text, @"^\s*" + Regex.Escape(label) + @"\s*:\s*(.*)$",
                                RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        if (bool.TryParse(Field("Service enabled") ?? "", out bool en)) s.ServiceEnabled = en;
        if (bool.TryParse(Field("Read-only") ?? "", out bool ro)) s.ReadOnly = ro;
        s.ConfigName = Field("Selected config name") ?? Field("Selected config") ?? "";
        if (int.TryParse(Field("Temperature") ?? "", out int t)) s.Temperature = t;
        s.FanDisplayName = Field("Fan display name") ?? "";

        int steps = 0;
        if (int.TryParse(Field("Fan speed steps") ?? "", out steps)) s.SpeedSteps = steps;
        double span = steps > 0 ? steps : 100.0;

        // 两个字段的口径**不一样**，这是实测出来的，不是猜的：
        //   · Target fan speed  = 百分比（0–100）。`nbfc set -s 30` 之后它显示 30.00；
        //     如果它是步进，30% 应该显示 59.10。
        //   · Current fan speed = EC 寄存器原始步进（本机 0–197），112.69 对应 57%。
        //     它在本机的 20/79/100 三次写入后都不变，所以它既不是"转速"也不是"实际占空比"，
        //     只能当"EC 里那个寄存器当前的值"，不足以作为控制是否生效的证据。
        // 旧代码把两者都除以 197：Target 79 被读成 40%，于是"指令 75%、实际 79%"变成"指令 75%、回读 40%"，
        // 看起来像"写进去了但没生效"。
        if (double.TryParse(Field("Target fan speed") ?? "",
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double tgt))
            s.TargetFanPct = tgt;                       // 已是百分比，不再换算
        if (double.TryParse(Field("Current fan speed") ?? "",
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double cur))
            s.CurrentRaw = cur;
        if (bool.TryParse(Field("Auto control enabled") ?? "", out bool auto)) s.AutoControl = auto;

        return s;
    }

    // 说明：旧版这里有 TryReadFanPercent()，用来把 NBFC 的 "Current fan speed" 当占空比回读。
    // 已删除：那个值就是 NBFC 自己上次写入 EC 的数，等于我们的指令，不是独立测量。
    // 风扇输出统一用 EngineState.AppliedFanPct（我们下发的值）。

    /// <summary>
    /// 设置风扇输出（0–100%）。成功返回 true —— 这里的"成功"是**校验过**的成功：
    /// 写完立刻回读 NBFC 状态，必须 Auto=false 且 Target ≈ 请求值，否则算失败。
    ///
    /// 为什么必须回读而不是只看退出码：`nbfc set -s 147` 的退出码是 **0**，但它做的事是
    /// 把风扇**交回自动曲线**（Auto=true / Target=0）。也就是说"命令成功执行"和
    /// "风扇被我们接管"是两件事，只看退出码会把后者误判成成功——这正是用户听到风扇
    /// 声音变大、界面数字却不动的原因：他听的是 NBFC 自动曲线，我们显示的是自己的目标值。
    ///
    /// 单位教训：`set -s` 收的是**百分比**（0–100），不是 EC 步进。本机 Fan speed steps=197，
    /// 旧代码按步进换算，于是 75% 被写成 147 —— 越界后被 NBFC 静默退回自动。
    ///
    /// <paramref name="force"/> = true 时跳过节流（仅在超温应急/用户手动点应用时使用）。
    /// </summary>
    public bool TrySetPercent(double percent, out string message, bool force = false)
    {
        message = "";
        LastWriteApplied = false;
        LastSkipReason = "";
        if (!Installed) { message = "未安装 NBFC"; return false; }

        int target = (int)Math.Round(Math.Clamp(percent, 0, 100));   // 百分比，不换算成步进
        var now = DateTime.UtcNow;

        if (!force)
        {
            if (now < _backoffUntil)
            {
                SkippedCount++;
                LastSkipReason = $"EC 退避中（还有 {(_backoffUntil - now).TotalSeconds:F0}s）";
                message = "NBFC " + LastSkipReason;
                return true;
            }
            // 值没变 → 不产生 EC 事务（风扇在目标值附近抖动时这一条能省掉绝大多数写入）
            if (target == _lastRaw)
            {
                SkippedCount++;
                LastSkipReason = "目标值未变";
                return true;
            }
            int since = (int)(now - _lastSetUtc).TotalMilliseconds;
            if (since < MinWriteIntervalMs)
            {
                SkippedCount++;
                LastSkipReason = $"距上次写入 {since}ms < {MinWriteIntervalMs}ms";
                return true;
            }
        }

        var st = Run($"set -s {target}", 8000);
        LastRawOutput = st.output;
        if (!st.ok)
        {
            // 进程层面就失败（服务没起、权限不足）：直接报，附带原始输出
            Fail($"NBFC 设置失败：{st.output.Trim()}", now);
            message = LastError;
            return false;
        }

        // —— 关键：校验"是否真的接管" ——
        var after = TryReadStatus();
        double? got = after.TargetFanPct;
        bool auto = after.AutoControl ?? true;

        if (auto)
        {
            Fail($"NBFC 未接管：写入 {target}% 后仍是自动曲线（Auto control enabled=True）"
                 + "。常见原因：数值越界被 NBFC 忽略、或服务处于只读/未选配置状态。", now);
            message = LastError;
            return false;
        }
        if (got is double g && Math.Abs(g - target) > 2.0)
        {
            Fail($"NBFC 未按指令执行：请求 {target}%，回读 Target={g:F0}%", now);
            message = LastError;
            return false;
        }

        _lastSetUtc = now;
        _lastRaw = target;
        _failStreak = 0;
        WriteCount++;
        LastWriteApplied = true;
        LastAppliedPct = target;
        LastAppliedAt = now;
        LastError = "";
        message = $"NBFC 已设置 {target}%（回读 Target={(got?.ToString("F0") ?? "—")}%，Auto=off）";
        return true;
    }

    private void Fail(string why, DateTime now)
    {
        LastError = why;
        // 失败也要占用"时间闸"：否则引擎每拍都会重试，每次两个进程 + 两次 EC 事务，
        // 在 NBFC 出问题的时候反而把 EC 压得更狠。
        _lastSetUtc = now;
        _failStreak++;
        if (_failStreak >= 3)
        {
            _backoffUntil = now.AddSeconds(30);
            _failStreak = 0;
            Log.Warn(why + "（连续失败，暂停写 EC 30 秒）");
        }
        else Log.Warn(why);
    }

    /// <summary>最近一次**真正生效**的百分比与时刻。界面显示"已生效"时用它，不用我们的目标值。</summary>
    public int LastAppliedPct { get; private set; } = -1;
    public DateTime LastAppliedAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 自检：NBFC 状态字段的口径（纯解析，不碰 EC）。
    /// 用的是真机上抓下来的原始文本，包括"越界写被退回自动"那一次。
    /// </summary>
    public static List<string> SelfTest()
    {
        var errors = new List<string>();

        // A) 正常手动接管：Target 是百分比 79，不能被当成 79/197
        const string manual = "Service enabled         : True\n" +
                              "Read-only               : False\n" +
                              "Selected config name    : HP OMEN Notebook PC 15\n" +
                              "Temperature             : 61\n" +
                              "Fan display name        : CPU fan\n" +
                              "Auto control enabled    : False\n" +
                              "Critical mode enabled   : False\n" +
                              "Current fan speed       : 112.69\n" +
                              "Target fan speed        : 79.00\n" +
                              "Fan speed steps         : 197\n";
        var a = ParseStatus(manual);
        if (a.TargetFanPct is not double ta || Math.Abs(ta - 79) > 0.5)
            errors.Add($"Target 79 应解析为 79%，实际 {a.TargetFanPct}");
        if (a.AutoControl != false) errors.Add("Auto=false 应解析为手动接管");
        if (a.CurrentRaw is not double ca || Math.Abs(ca - 112.69) > 0.01)
            errors.Add($"Current 112.69 应保留为原始步进，实际 {a.CurrentRaw}");
        if (!a.ManualActive) errors.Add("ManualActive 应为 true");

        // B) 越界写被退回自动：这正是"听起来变吵、看起来没变"的真实状态
        string reverted = manual.Replace("Auto control enabled    : False", "Auto control enabled    : True")
                                .Replace("Target fan speed        : 79.00", "Target fan speed        : 0.00");
        var b = ParseStatus(reverted);
        if (b.AutoControl != true) errors.Add("Auto=true 应解析为未接管");
        if (b.ManualActive) errors.Add("Auto=true 时 ManualActive 必须为 false");
        if (b.TargetFanPct is double tb && Math.Abs(tb) > 0.01)
            errors.Add($"退回自动后 Target 应为 0，实际 {b.TargetFanPct}");

        // C) 老配置没有 steps 字段时也不能崩
        var c = ParseStatus("Selected config name : X\nTarget fan speed : 40.00\n");
        if (c.TargetFanPct is not double tc || Math.Abs(tc - 40) > 0.5)
            errors.Add($"缺 steps 字段时 Target 应为 40%，实际 {c.TargetFanPct}");

        return errors;
    }

    /// <summary>重载配置 / 交还自动后，允许下一次写入不受"值未变"影响。</summary>
    public void InvalidateCache()
    {
        _lastRaw = -1;
        _lastSetUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 把风扇交回 NBFC 自己的自动曲线（= EC 固件策略的等效物），服务保持运行。
    ///
    /// 这里**不能**用 `nbfc stop`：那个命令会停掉 NBFC 服务（status 里 Service enabled
    /// 变 False），之后所有 set/status 全部失效，风扇彻底脱离控制。
    /// 实测：实验计划里凡是没有显式风扇档位的段落都会调本方法，第一次调用就把 NBFC
    /// 关掉了，导致后 18 分钟的风扇占空比全部采不到。
    /// </summary>
    public bool TrySetAuto(out string message)
    {
        message = "";
        if (!Installed) { message = "未安装 NBFC"; return false; }
        var st = Run("set -a", 8000);
        LastRawOutput = st.output;
        InvalidateCache();
        if (st.ok) { message = "风扇已交回 NBFC 自动曲线（服务保持运行）。"; Log.Info(message); return true; }
        message = $"NBFC 设自动失败：{st.output.Trim()}";
        LastError = message;
        Log.Warn(message);
        return false;
    }

    /// <summary>把风扇交还 EC 固件（停止 NBFC 接管）。会停掉 NBFC 服务，只用于彻底退出。</summary>
    public bool TryRestore(out string message)
    {
        message = "";
        if (!Installed) return false;
        var st = Run("stop", 8000);
        message = st.ok ? "NBFC 已停止接管，风扇交还 EC 固件。" : $"NBFC 停止失败：{st.output.Trim()}";
        Log.Info(message);
        return st.ok;
    }

    private (bool ok, string output) Run(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "超时"); }
            string text = (stdout + "\n" + stderr).Trim();
            return (p.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool ok, string output) RunOnPath(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.ExitCode == 0, (so + se).Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

/// <summary>
/// `nbfc status -a` 的解析结果。
///
/// 重要事实：NBFC 的 `Current fan speed` 是**占空比百分比**（模板 `{0:0.00}`），
/// 不是转速。NBFC 不读 EC 的 FG 测速信号，所以在这类笔记本上
/// "风扇 RPM" 这个物理量在软件层是不可观测的。可靠性判断必须改用占空比。
/// </summary>
public sealed class NbfcStatus
{
    public bool? ServiceEnabled { get; set; }
    public bool? ReadOnly { get; set; }
    public string ConfigName { get; set; } = "";
    public int Temperature { get; set; }
    public string FanDisplayName { get; set; } = "";
    /// <summary>EC 寄存器里的原始步进（0–SpeedSteps）。不是转速，也不是百分比。</summary>
    public double? CurrentRaw { get; set; }
    /// <summary>目标速度，**百分比**（0–100）。NBFC 的 `set -s` 收的就是这个单位。</summary>
    public double? TargetFanPct { get; set; }
    /// <summary>是否仍由 NBFC 自动曲线控制。true = 我们的手动指令没被接受。</summary>
    public bool? AutoControl { get; set; }
    public int? SpeedSteps { get; set; }
    public string Raw { get; set; } = "";

    /// <summary>服务已启用且已选配置，才可能真正接管风扇。</summary>
    public bool Ready => ServiceEnabled == true && ConfigName.Length > 0;

    /// <summary>手动接管是否真的生效：必须 Auto=false 且 Target 与请求一致。</summary>
    public bool ManualActive => AutoControl == false;
}
