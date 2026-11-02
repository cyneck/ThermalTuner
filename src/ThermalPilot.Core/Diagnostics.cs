using Microsoft.Win32;
using System.Management;

namespace ThermalPilot.Core;

/// <summary>
/// 环境体检：把"这台机器到底支持哪些能力、缺什么前置条件"一次说清楚，
/// 对应 UI 上的「环境检测」按钮（Windows 10 / 11 默认目标）。
/// </summary>
public static class EnvironmentCheck
{
    public static List<DiagItem> Run(HardwareService hw, PowerManager power, UndervoltService uv,
                                     CpuControlService? cpu = null)
    {
        var items = new List<DiagItem>();

        // 1. 操作系统
        string product = "未知", build = "?", display = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = key?.GetValue("ProductName") as string ?? Environment.OSVersion.VersionString;
            build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? "?";
            display = key?.GetValue("DisplayVersion")?.ToString() ?? "";
        }
        catch { }

        bool isWin10Plus = Environment.OSVersion.Version.Major >= 10;
        int buildNo = int.TryParse(build, out int b) ? b : 0;
        items.Add(new DiagItem
        {
            Name = "操作系统",
            Status = isWin10Plus && buildNo >= 14393 ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = $"{product}（Build {build} {display}）",
            Advice = isWin10Plus && buildNo >= 14393
                ? "满足要求（Windows 10 1607 及以上）。"
                : "建议使用 Windows 10 1607(14393) 及以上；.NET 8 自包含运行库在更老系统上可能无法启动。"
        });

        // 2. 管理员
        bool admin = UndervoltService.IsElevated;
        items.Add(new DiagItem
        {
            Name = "管理员权限",
            Status = admin ? DiagStatus.Ok : DiagStatus.Fail,
            Detail = admin ? "已以管理员身份运行" : "当前不是管理员",
            Advice = admin ? "" : "右键程序 → 以管理员身份运行；或安装后用桌面快捷方式（已内置 UAC 提权清单）启动。"
        });

        // 3. 内核隔离 HVCI
        bool hvci = UndervoltService.HvciEnabled;
        items.Add(new DiagItem
        {
            Name = "内核隔离（内存完整性）",
            Status = hvci ? DiagStatus.Warn : DiagStatus.Ok,
            Detail = hvci ? "已开启" : "未开启",
            Advice = hvci
                ? "开启时 Windows 会禁止加载第三方内核驱动，风扇调速与降压不可用。关闭路径：Windows 安全中心 → 设备安全性 → 内核隔离 → 内存完整性 → 关，重启。"
                : "驱动可正常加载。"
        });

        // 4. 硬件监控驱动
        items.Add(new DiagItem
        {
            Name = "硬件监控驱动",
            Status = hw.IsOpen ? DiagStatus.Ok : DiagStatus.Fail,
            Detail = hw.IsOpen ? $"已加载（识别 {hw.SensorCount} 个传感器）" : "未加载：" + hw.OpenError,
            Advice = hw.IsOpen
                ? ""
                : "先以管理员运行；若仍失败，检查杀软是否拦截了驱动释放目录（%TEMP%），或关闭内核隔离后重启。"
        });

        // 5. 温度源
        var snap = hw.IsOpen ? hw.Read() : new SensorSnapshot();
        // 平台能力只探一次，后面 9 / 9b 共用（顺序在 9 之前，因为降压判定也要用它）
        var caps = CpuCapabilities.Refresh(cpu?.Msr);
        items.Add(new DiagItem
        {
            Name = "温度读数口径（与任务管理器/OEM 工具对照用）",
            Status = snap.CpuTempValid ? DiagStatus.Ok : DiagStatus.Fail,
            Detail = snap.CpuTempValid
                ? $"CPU {snap.CpuTempShown:F1}℃（Package）"
                  + (Math.Abs(snap.CpuTemp - snap.CpuTempShown) >= 1 ? $"（原始 {snap.CpuTemp:F1}℃）" : "")
                  + (snap.CpuCoreMaxTemp > 0 ? $" / 最热核心 {snap.CpuCoreMaxTemp:F1}℃" : "")
                  + $"　·　GPU {snap.GpuTempShown:F1}℃（Core）"
                  + (snap.GpuHotSpotTemp > 0 ? $" / 热点 {snap.GpuHotSpotTemp:F1}℃" : "")
                  + $"　·　内存 {snap.RamPct:F0}%（{snap.RamUsedMb / 1024:F1}/{snap.RamTotalMb / 1024:F1} GB）"
                : "未取到温度",
            Advice = snap.CpuTempValid
                ? "界面上的大数字一律取 CPU Package 与 GPU Core，且已过 5 点中值抗毛刺 —— "
                  + "DTS 偶发会跳高 20~40℃ 而功率不变，那是读数毛刺不是真温升。"
                  + "最热单核（Core Max）通常高 1~3℃，GPU 热点（Hot Spot）通常高 8~12℃ —— "
                  + "它们只是参考，拿来当主指标就会显得「比 OEM 工具高」。"
                : "老主板可在 BIOS 打开 CPU 传感器/ACPI；也可升级主板厂商驱动后再试。"
        });

        // 6. 风扇转速通道
        MachineProbe.Load();
        var (fanDetail, fanAdvice, fanUrl) = MachineProbe.FanAdvice(hw.LhmRpmSupported || hw.LhmFanControlSupported);
        int rpmChannels = hw.FanChannels.Count(c => c.RpmSensor != null);
        bool nbfc = hw.Nbfc.Installed;
        items.Add(new DiagItem
        {
            Name = "风扇转速通道（RPM）",
            Status = rpmChannels > 0 ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = rpmChannels > 0
                ? $"{rpmChannels} 个：" + string.Join("、", hw.FanChannels.Where(c => c.RpmSensor != null).Select(c => $"{c.Name} {ReadRpm(c)} RPM"))
                : nbfc ? $"无转速传感器。NBFC 只能回读占空比（EC 的 FG 测速信号未接入），"
                         + $"机型：{MachineProbe.Summary}"
                : !hw.IsOpen ? "未初始化（自检模式或以非管理员运行，尚未探测硬件）"
                : $"未检测到转速传感器（机型：{MachineProbe.Summary}）",
            Advice = rpmChannels > 0
                ? ""
                : nbfc ? "这是笔记本 EC 的常见情况，不影响调速：程序改用「占空比 → 噪声估计」替代 RPM，"
                         + "并在策略模型里用占空比作散热能力变量。"
                : !hw.IsOpen ? "启动引擎后才会真正探测。"
                : fanDetail
        });

        // 7. 风扇调速通道
        int ctrlChannels = hw.FanChannels.Count(c => c.Controllable);
        items.Add(new DiagItem
        {
            Name = "风扇调速通道（PWM）",
            Status = ctrlChannels > 0 || nbfc ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = ctrlChannels > 0
                ? $"{ctrlChannels} 个：" + string.Join("、", hw.FanChannels.Where(c => c.Controllable).Select(c => c.Name))
                : nbfc ? "经 NBFC 后端接管（EC 直写）"
                : hw.IsOpen ? "无可用调速通道"
                : "未初始化（自检模式或以非管理员运行，尚未探测硬件）",
            Advice = ctrlChannels > 0 ? "风扇曲线可生效。"
                : nbfc ? "风扇曲线由 NBFC 写入 EC；退出/卸载会自动交还固件策略。"
                : !hw.IsOpen ? "启动引擎后才会真正探测，此处结论不代表硬件不支持。"
                : fanAdvice + (fanUrl is null ? "" : $"（下载：{fanUrl}）")
        });

        // 7b. NBFC 后端"是否真的接管" —— 不是"装没装"，而是"写进去有没有生效"。
        // 这一项是用户报"风扇声音变了但数值不动"之后加的：装好 NBFC 并且写了值，
        // 与"风扇真的听我们的"之间还差一步（Auto control enabled 必须为 False）。
        hw.Nbfc.DetectOnce();      // 没启动引擎时诊断页也要能说清"风扇到底谁在管"
        if (hw.Nbfc.Installed)
        {
            var ns = hw.Nbfc.TryReadStatus();
            bool manual = ns.ManualActive;
            items.Add(new DiagItem
            {
                Name = "NBFC 接管状态（写入是否真的生效）",
                Status = !ns.Ready ? DiagStatus.Fail : manual ? DiagStatus.Ok : DiagStatus.Warn,
                Detail = $"服务 enabled={ns.ServiceEnabled} · 配置「{ns.ConfigName}」"
                         + $"　Auto control={(ns.AutoControl == true ? "开（风扇仍由 NBFC 自动曲线控制）" : ns.AutoControl == false ? "关（手动接管中）" : "未知")}"
                         + $"　Target={ns.TargetFanPct?.ToString("F0") ?? "—"}%"
                         + $"　EC 寄存器={ns.CurrentRaw?.ToString("F1") ?? "—"}/{ns.SpeedSteps?.ToString() ?? "—"} 步",
                Advice = manual
                    ? $"手动接管已生效，最近一次成功写入 {hw.Nbfc.LastAppliedPct}%"
                      + (hw.Nbfc.LastAppliedAt == DateTime.MinValue ? "" : $"（{hw.Nbfc.LastAppliedAt.ToLocalTime():HH:mm:ss}）")
                      + "。注意：本机没有转速传感器，界面上的百分比是**下发值**，不是回读的转速。"
                    : "风扇目前仍由 NBFC 自动曲线（或 BIOS）控制，程序下发的百分比没有生效。"
                      + (string.IsNullOrEmpty(hw.Nbfc.LastError) ? "" : "最近一次失败原因：" + hw.Nbfc.LastError)
                      + " 常见原因：`nbfc set -s` 的值必须是 0–100 的**百分比**（不是 EC 步进），越界会被 NBFC 静默忽略并退回自动。"
            });
        }

        // 8. GPU
        bool hasGpu = snap.GpuTempValid;
        items.Add(new DiagItem
        {
            Name = "独立显卡温度",
            Status = hasGpu ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = hasGpu ? $"{snap.GpuTempShown:F1} °C，负载 {snap.GpuLoad:F0}%" : "未检测到 GPU 温度（核显或未安装驱动）",
            Advice = hasGpu ? "" : "无独显时以 CPU 温度为唯一控制输入即可。"
        });

        // 9. CPU 与降压（判定交给能力矩阵，避免"Intel 就一定能降压"的误判）
        items.Add(new DiagItem
        {
            Name = "CPU / 降压可行性",
            Status = caps.Uv.Hard ? DiagStatus.Warn : DiagStatus.Ok,
            Detail = $"{uv.Cpu.Name}（{uv.Cpu.Manufacturer}，{uv.Cpu.Cores}核{uv.Cpu.LogicalProcessors}线程）"
                     + Environment.NewLine + $"降压：{caps.Uv.Badge} —— {caps.Uv.Detail}",
            Advice = caps.Uv.Advice
        });

        // 9b. CPU 精细调控（MSR：PL1/PL2/Time/EPP/睿频）
        {
            string detail;
            DiagStatus status;
            string advice;

            if (cpu == null)
            {
                status = DiagStatus.Warn;
                detail = "未初始化（启动引擎后探测）";
                advice = "启动引擎后可检测 MSR 是否可写。";
            }
            else if (!cpu.EnsureAvailable())
            {
                status = DiagStatus.Fail;
                detail = "不可用：" + cpu.LastError;
                advice = "需管理员 + 关闭内存完整性(HVCI) + LHM 驱动成功加载；部分 BIOS 会锁死 PL/EPP。";
            }
            else
            {
                var st = cpu.ReadCurrent();
                if (st == null)
                {
                    status = DiagStatus.Warn;
                    detail = "MSR 可访问，但读取 0x610 失败";
                    advice = "该平台可能不暴露包功率限制寄存器。";
                }
                else
                {
                    status = DiagStatus.Ok;
                    detail = $"PL1={st.Pl1W:F0}W / PL2={st.Pl2W:F0}W / τ={CpuControlService.TauText(st.TauS)} / EPP={st.Epp}"
                           + $" / 睿频 单核 {st.TurboRatio1Core}·全核 {st.TurboRatioAllCore}（×100MHz）";
                    advice = "可精调降压、PL1/PL2/Time、EPP 与睿频倍率；写入失败的那一项会被读回校验拦下。";
                }
            }

            items.Add(new DiagItem
            {
                Name = "CPU 精细调控（MSR）",
                Status = status,
                Detail = detail,
                Advice = advice
            });

            // 9b-2. 能力矩阵：高级页哪些旋钮会被禁用，就看这一条。
            // 这是"平台有没有这个寄存器"，和上面"现在能不能写"是两件事，必须分开看。
            bool anyHard = caps.Items.Any(x => x.Hard);
            bool allKnown = caps.Items.All(x => x.State == CapState.Supported || x.Hard);
            items.Add(new DiagItem
            {
                Name = "CPU 能力矩阵（决定高级参数是否可编辑）",
                // 全支持才算通过；有真·不支持项给警告；只是"还没探测"也给警告（不能显示成通过）
                Status = anyHard || !allKnown ? DiagStatus.Warn : DiagStatus.Ok,
                Detail = caps.CpuLine + Environment.NewLine + caps.BadgeLine,
                Advice = caps.Details
            });
        }

        // 9b-2a. 平台功率上限：PL1/PL2 的可用区间从哪来。
        // 这一项不报"好/坏"，只回答"这两个数是不是真的"——写得清才能让人信任上面的区间。
        items.Add(new DiagItem
        {
            Name = "平台功率上限（决定 PL1/PL2 的可用区间）",
            Status = PlatformPower.Cpu.Measured && (!PlatformPower.Gpu.HasValue || PlatformPower.Gpu.Measured)
                ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = PlatformPower.Summary + Environment.NewLine
                     + string.Join(Environment.NewLine, PlatformPower.Detail),
            Advice = PlatformPower.Cpu.Measured && PlatformPower.Gpu.Measured
                ? "两个数都来自本机（MSR / NVML）；换机器或换显卡后会自动重新探测。"
                : "有数值是查型号表或内置默认得到的，不是这台机器的实测。"
                  + "「高级 → ② CPU 精细微调 → 平台功率」可以把两个数改成你确认过的值（0 = 自动）。"
        });

        // 9b-2b. 整机热预算：CPU 与 GPU 共用热管，GPU 满载时 CPU 的可用功率必须让位。
        {
            double gpuW = snap.GpuPower > 0.5 ? snap.GpuPower : 0;
            double cpuW = snap.CpuPower > 0.5 ? snap.CpuPower : 0;
            bool gpuLoaded = gpuW > 20;
            double cpuDesign = PlatformPower.CpuDesignW;
            double gpuDesign = PlatformPower.GpuDesignW;   // NaN = 未知
            string designText = double.IsNaN(gpuDesign)
                ? $"CPU {cpuDesign:F0}W + GPU 未知（按 {PlatformPower.DefaultGpuDesignW:F0}W 估）"
                : $"CPU {cpuDesign:F0}W + GPU {gpuDesign:F0}W";
            items.Add(new DiagItem
            {
                Name = "整机热预算（CPU 与 GPU 共用散热）",
                Status = gpuLoaded ? DiagStatus.Warn : DiagStatus.Ok,
                Detail = $"设计：{designText}　当前：CPU {cpuW:F0}W / GPU {gpuW:F0}W（GPU 负载 {snap.GpuLoad:F0}%）",
                Advice = gpuLoaded
                    ? "GPU 正在吃散热：热阻 R 是在 CPU 单负载下标定的，GPU 同时满载时同一套热管要带走更多热量，"
                      + "CPU 的可持续功率会明显低于 (上限−室温)/R 的估算。游戏时不要把 CPU PL1 顶到设计功耗，"
                      + "让 GPU 先拿到它需要的散热。"
                    : "当前只有 CPU 在发热，热阻估计与功率预算的假设成立（R 是 CPU 单负载下标定的）。"
            });
        }

        // 9b-3. EC 访问健康：笔记本风扇只能直写 EC，而电池也挂在同一条 EC 上。
        // 写得太勤会把 EC 打到超时（ACPI 13），表现就是"检测不到电池"。
        items.Add(EcHealth.Check(30));

        // 9c. 交付性能指标（有效频率 / 包功率 / 谁在限制）
        {
            string detail;
            DiagStatus status;
            const string advice = "温度是结果不是目标：真正决定快不快的是有效频率，以及它是被温度墙、功率墙还是睿频上限卡住。";

            if (cpu == null || !cpu.Available)
            {
                status = DiagStatus.Warn;
                detail = "未采样（需要 MSR 可用）";
            }
            else
            {
                var perf = new PerfMetrics(cpu.Msr);
                perf.Sample(0, 0, 90, 0);
                System.Threading.Thread.Sleep(1100);
                var p = perf.Sample(0, 0, 90, 0);
                if (p.Valid)
                {
                    status = p.Limit == BindingLimit.Thermal ? DiagStatus.Warn : DiagStatus.Ok;
                    detail = $"{p.Detail} → {p.LimitText}"
                             + (p.TurboLimitMhz > 0 ? $"（睿频上限 {p.TurboLimitMhz:F0}MHz）" : "");
                }
                else
                {
                    status = DiagStatus.Warn;
                    detail = "读取 APERF/MPERF 失败，无法计算有效频率";
                }
            }

            items.Add(new DiagItem
            {
                Name = "CPU 交付指标（有效频率/限制原因）",
                Status = status,
                Detail = detail,
                Advice = advice
            });
        }

        // 10. 电源方案
        items.Add(new DiagItem
        {
            Name = "电源方案（降频通道）",
            Status = power.Available ? DiagStatus.Ok : DiagStatus.Fail,
            Detail = power.Available
                ? $"{power.ActiveScheme}（原始最大处理器状态 {power.OriginalProcMax}%，Turbo 策略 {power.OriginalBoostMode}）"
                : "powercfg 不可用",
            Advice = power.Available ? "降频可生效，退出/卸载会自动还原。" : "请确认系统未禁用 powercfg 或电源服务。"
        });

        // 11. 数据目录
        bool writable = false;
        try
        {
            string probe = Path.Combine(Paths.DataDirectory, ".probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            writable = true;
        }
        catch { }
        items.Add(new DiagItem
        {
            Name = "配置与日志目录",
            Status = writable ? DiagStatus.Ok : DiagStatus.Warn,
            Detail = Paths.DataDirectory,
            Advice = writable ? "标定数据/日志将写入此处。" : "目录不可写，采样日志与配置保存会失败。"
        });

        // 12. 结论
        bool core = hw.IsOpen && power.Available && snap.CpuTempValid;
        bool fan = ctrlChannels > 0;
        items.Add(new DiagItem
        {
            Name = "总体结论",
            Status = core ? DiagStatus.Ok : DiagStatus.Fail,
            Detail = core
                ? ((fan || hw.Nbfc.Installed) ? "核心能力（监控 + 降频 + 风扇调速）全部可用" : "监控 + 降频可用；风扇调速不可用（将自动降级）")
                : "核心能力不可用，请先解决上面标红的项目",
            Advice = core
                ? "建议先跑一次「自动标定」，让程序按你的机器实测生成曲线。"
                : "按上面每一条的修复建议处理后重新检测。"
        });

        return items;
    }

    private static string ReadRpm(FanChannel c)
    {
        try { return c.RpmSensor?.Value is float v ? v.ToString("F0") : "--"; }
        catch { return "--"; }
    }

    public static string ToText(IEnumerable<DiagItem> items)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"===== ThermalPilot 环境检测 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        foreach (var it in items)
        {
            string mark = it.Status switch { DiagStatus.Ok => "[通过]", DiagStatus.Warn => "[提示]", _ => "[不符合]" };
            sb.AppendLine($"{mark} {it.Name}：{it.Detail}");
            if (!string.IsNullOrWhiteSpace(it.Advice)) sb.AppendLine($"        建议：{it.Advice}");
        }
        sb.AppendLine("================================================");
        return sb.ToString();
    }
}

/// <summary>
/// EC（嵌入式控制器）访问健康检查。
///
/// 为什么必须有它：笔记本风扇没有标准 PWM 通道，只能由 NBFC / WinRing0 直接写 EC 寄存器。
/// EC 是一条很慢的单总线（典型几毫秒一次事务，同一时刻只能有一个 owner），
/// 而 Windows 的电池电量、是否插电、盖子状态**全部经由同一个 EC 上报**。
/// 所以"风扇写得太勤"不是一个抽象的性能问题，它会把 ACPI EC 驱动打到超时
/// （事件日志 ACPI 13：「在指定的超时期限内，嵌入式控制器(EC)未响应」），
/// 用户看到的现象就是「检测不到电池」——电池本身没坏，是 EC 被抢崩了。
///
/// 本程序 v1.9.1 起把写 EC 的最小间隔限制为 3 秒（可调），这条检查用来验证效果。
/// </summary>
public static class EcHealth
{
    /// <summary>近 N 分钟内 ACPI EC 超时事件数；-1 表示读不到事件日志（权限/服务问题）。</summary>
    public static int RecentTimeouts(out string raw, int minutes = 30)
    {
        raw = "";
        try
        {
            string ms = ((long)minutes * 60 * 1000).ToString();
            string q = "*[System[(EventID=13) and Provider[@Name='ACPI'] and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";
            string outp = Shell.Run("wevtutil", "qe System /q:\"" + q + "\" /rd:true /c:25 /f:text", 8000);
            raw = outp ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            int n = 0;
            foreach (var line in raw.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Event[") || t.StartsWith("事件[")) n++;
            }
            return n;
        }
        catch (Exception ex)
        {
            raw = ex.Message;
            return -1;
        }
    }

    /// <summary>给诊断页用的一条结论。</summary>
    public static DiagItem Check(int minutes = 30)
    {
        int n = RecentTimeouts(out string raw, minutes);
        if (n < 0)
            return new DiagItem
            {
                Name = "EC 访问健康（风扇写 EC 的副作用）",
                Status = DiagStatus.Warn,
                Detail = "读不到系统事件日志：" + raw,
                Advice = "以管理员运行后可读；不影响其它功能。"
            };

        if (n == 0)
            return new DiagItem
            {
                Name = "EC 访问健康（风扇写 EC 的副作用）",
                Status = DiagStatus.Ok,
                Detail = $"近 {minutes} 分钟没有 ACPI EC 超时（事件 13）。",
                Advice = "风扇写 EC 已被限制为每 " + "≥3 秒一次（高级页可调）。"
            };

        return new DiagItem
        {
            Name = "EC 访问健康（风扇写 EC 的副作用）",
            Status = n <= 2 ? DiagStatus.Warn : DiagStatus.Fail,
            Detail = $"近 {minutes} 分钟出现 {n} 次 ACPI 13「嵌入式控制器(EC)未响应」。",
            Advice = "电池电量与插电状态同样由 EC 上报，EC 超时的窗口里系统会短暂『检测不到电池』"
                   + "（电池本身通常没坏）。处理顺序：① 高级页把风扇交回自动 / 关闭风扇调速；"
                   + "② 关机并拔掉电源适配器，长按电源键 30 秒做 EC 复位；③ 开机后若电池仍缺失，"
                   + "设备管理器 → 电池 → 卸载『Microsoft ACPI 兼容的控制方法电池』→ 扫描硬件改动。"
        };
    }
}
