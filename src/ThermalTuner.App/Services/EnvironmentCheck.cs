using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using ThermalTuner.App.Services.Msr;
using ThermalTuner.Core.Environment;
using ThermalTuner.Core.Msr;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App.Services;

/// <summary>
/// 首次安装启动后的环境能力自检结果。落盘在 capability.json（独立于 settings.json，
/// 避免被界面保存设置时覆盖）。UI 只依据本快照决定「哪些参数项不可编辑」。
///
/// 重要口径：
/// · 自检决定「参数项能否获取」；能否真实写入由管理员 + 「允许写入 MSR」双开关决定，
///   普通权限下不把所有项判死，避免用户提权重启后仍被误禁。
/// · 后端兼容以实测为准：0.9.6+ / PawnIO 只有只读接口，可作为「读取降级」，
///   写入由独立的 PawnIO + IntelMSR 模块承担，与 LHM 传感器侧互不依赖。
/// </summary>
public sealed class CapabilitySnapshot
{
    /// <summary>快照结构版本。字段变化时递增，旧快照自动作废并重跑一次。</summary>
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;
    public DateTime CheckedAt { get; set; }
    /// <summary>产生快照的程序版本（只取 主.次）。升级后能力可能变化，需重新确认。</summary>
    public string AppVersion { get; set; } = string.Empty;
    /// <summary>机器标识：CPU 型号 + 逻辑核数 + 系统版本，用于判断是否换了机器/换了系统。</summary>
    public string MachineKey { get; set; } = string.Empty;
    /// <summary>指纹 = 版本 + 机器 + 权限。任一变化即视为快照失效。</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public bool Elevated { get; set; }
    public bool WriteProbeDone { get; set; }
    public string LhmVersion { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string MsrStatus { get; set; } = string.Empty;
    public string MsrDetail { get; set; } = string.Empty;
    public bool Hvci { get; set; }
    public bool Virtualized { get; set; }
    /// <summary>本次检测时调优通道是否具备写入能力（只表示通道，不含授权开关）。</summary>
    public bool ChannelWritable { get; set; }
    public string CpuName { get; set; } = string.Empty;
    public string CpuCapabilityNote { get; set; } = string.Empty;
    public string TelemetryNote { get; set; } = string.Empty;
    /// <summary>风扇数据源能力（运行时探测）。仅探测 EC / NBFC 只读链，不含 LHM 传感器通道。</summary>
    public string FanTransport { get; set; } = string.Empty;
    public string FanMeasure { get; set; } = string.Empty;
    public bool FanReadable { get; set; }
    public bool FanWritable { get; set; }
    public string FanDetail { get; set; } = string.Empty;
    public Dictionary<string, bool> Readable { get; set; } = new();
    public Dictionary<string, bool> Writable { get; set; } = new();
    public List<string> InstallHints { get; set; } = new();
    public List<string> DisabledItems { get; set; } = new();

    public bool CanRead(string key) => Readable.TryGetValue(key, out bool v) && v;
    public bool CanWrite(string key) => Writable.TryGetValue(key, out bool v) && v;
    public bool IsEditable(string key) => !DisabledItems.Contains(key);

    public string Summary =>
        $"LHM {LhmVersion} · {Backend} · {MsrStatus}" +
        $" · 可读 {Readable.Count(x => x.Value)}/{Readable.Count} 项" +
        $" · 禁用 {DisabledItems.Count} 项" +
        (WriteProbeDone ? " · 写入能力已实测" : " · 写入能力待管理员自检");

    public string DependencyText => InstallHints.Count == 0
        ? "✓ 外部依赖齐全，无需额外安装"
        : string.Join("\n", InstallHints.Select((h, i) => $"{i + 1}. {h}"));

    public string DisabledText => DisabledItems.Count == 0
        ? "✓ 所有参数项均可获取且未被实测判不可用，高级参数页全部可编辑"
        : "以下参数项本机不可获取或实测不可写，已在高级参数页禁用编辑：" + string.Join("、", DisabledItems);
}

public static class EnvironmentSelfCheck
{
    /// <summary>
    /// 命令行模式（--smoke / --env-check 等）。命令行可以跑自检并落盘能力快照，
    /// 但不得消耗「首次安装启动」标记，否则用户第一次双击界面时看不到自检提示。
    /// </summary>
    public static bool IsCommandLineMode { get; set; }

    private static readonly string FilePath =
        Path.Combine(SettingsStore.DataDirectory, "capability.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string CapabilityPath => FilePath;

    /// <summary>快照有效期：超过这个天数即使指纹不变，也在后台静默复核一次（不阻塞启动）。</summary>
    public static readonly TimeSpan CacheSoftLimit = TimeSpan.FromDays(30);

    /// <summary>程序版本（主.次），用于判断升级后能力是否可能变化。</summary>
    public static string CurrentAppVersion
    {
        get
        {
            var v = typeof(EnvironmentSelfCheck).Assembly.GetName().Version;
            return v is null ? "0.0" : $"{v.Major}.{v.Minor}";
        }
    }

    /// <summary>机器指纹字段：CPU 型号 + 逻辑核数 + 系统版本。全部是无需驱动、无特权的读取。</summary>
    public static string CurrentMachineKey
    {
        get
        {
            string cpu;
            try { cpu = HardwareProbe.Probe().BrandString; }
            catch { cpu = "unknown"; }
            return $"{cpu}|{System.Environment.ProcessorCount}|{System.Environment.OSVersion.Version}";
        }
    }

    public static string ComputeFingerprint(string appVersion, string machineKey, bool elevated) =>
        $"{appVersion}|{machineKey}|{(elevated ? "admin" : "user")}";

    /// <summary>当前进程对应的指纹（与 <see cref="Run"/> 写入的保持一致）。</summary>
    public static string CurrentFingerprint() =>
        ComputeFingerprint(CurrentAppVersion, CurrentMachineKey, IsElevated());

    /// <summary>
    /// 判断一份快照能否直接用于本次启动。宁可多跑一次检查，也不要把过期能力当成现状：
    /// 版本升级、换机器、换系统、提权/降权都可能改变可读可写清单。
    /// </summary>
    public static bool IsCacheValid(CapabilitySnapshot? snap, out string reason)
    {
        reason = string.Empty;
        if (snap is null) { reason = "尚未检测过"; return false; }
        if (snap.Schema != CapabilitySnapshot.CurrentSchema) { reason = "检测结果的格式已更新"; return false; }
        if (!string.Equals(snap.Fingerprint, CurrentFingerprint(), StringComparison.Ordinal))
        {
            reason = snap.Elevated != IsElevated()
                ? "运行权限与上次检测不同"
                : "程序版本或硬件环境发生变化";
            return false;
        }
        if (snap.CheckedAt == default) { reason = "检测时间缺失"; return false; }
        if (!snap.Readable.ContainsKey("Temp") || !snap.Readable.ContainsKey("PL1"))
        {
            reason = "检测结果不含关键参数";
            return false;
        }
        return true;
    }

    /// <summary>读取并校验磁盘上的快照；失败时给出「为什么要重新检测」的产品化原因。</summary>
    public static bool TryLoadValid(out CapabilitySnapshot? snap, out string reason)
    {
        snap = Load();
        return IsCacheValid(snap, out reason);
    }

    public static CapabilitySnapshot? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CapabilitySnapshot>(File.ReadAllText(FilePath));
        }
        catch { /* 损坏视为未自检 */ }
        return null;
    }

    public static void Save(CapabilitySnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snap, Json), new UTF8Encoding(false));
        }
        catch { /* 目录不可写时不影响运行 */ }
    }

    /// <summary>
    /// 跑一次自检。allowWriteProbe=true 时做无害写入探测（同值写回 / −10mV 后回零），
    /// 需管理员；false 时只做只读探测，适合首次启动自动执行。
    /// </summary>
    public static CapabilitySnapshot Run(bool allowWriteProbe, string telemetryNote = "",
        bool tempAvailable = false, bool powerAvailable = false)
    {
        var snap = new CapabilitySnapshot
        {
            Schema = CapabilitySnapshot.CurrentSchema,
            CheckedAt = DateTime.Now,
            AppVersion = CurrentAppVersion,
            MachineKey = CurrentMachineKey,
            Elevated = IsElevated(),
            TelemetryNote = telemetryNote
        };
        snap.Fingerprint = ComputeFingerprint(snap.AppVersion, snap.MachineKey, snap.Elevated);

        var env = HardwareProbe.Probe();
        snap.CpuName = env.BrandString;
        snap.Virtualized = env.IsVirtualized;
        snap.CpuCapabilityNote = CpuCapability.Probe(env.BrandString).MatrixText;
        snap.Hvci = ReadHvci();

        // ---- 1. 外部依赖：LHM 程序集版本与后端 ----
        var asm = typeof(Computer).Assembly;
        snap.LhmVersion = asm.GetName().Version?.ToString() ?? "未知";
        snap.Backend = MsrChannel.BackendName;
        var funcs = MsrChannel.ListFunctions();
        bool hasWriteMsr = funcs.Contains("ioctl_write_msr", StringComparer.Ordinal);

        Version? v = asm.GetName().Version;
        if (v is null || v.Major != 0 || v.Minor != 9 || v.Build < 6)
        {
            snap.InstallHints.Add(
                $"LibreHardwareMonitorLib 版本为 {snap.LhmVersion}，本项目要求 0.9.6 及以上；" +
                "请把 NuGet 包升级到 0.9.6（PawnIO 后端），以保证传感器与驱动链路一致。");
        }
        if (!hasWriteMsr)
        {
            snap.InstallHints.Add(
                "当前 IntelMSR 模块不含 ioctl_write_msr（只读版本）：参数只能查看不能下发。" +
                "请使用完整安装包（内置 PawnIO.Modules 0.2.11 模块），" +
                "或在 %LOCALAPPDATA%\\ThermalTuner\\driver\\ 下放置可写版本的 IntelMSR.bin。" +
                "注意：LibreHardwareMonitor 自带的同模块是只读的，不能替代。");
        }

        // ---- 2. MSR 通道与逐寄存器可读性 ----
        using var channel = MsrChannel.Open();
        snap.MsrStatus = $"{channel.Status} / {MsrChannel.StatusText(channel.Status)}";
        snap.MsrDetail = channel.Detail;
        snap.ChannelWritable = channel.CanWrite;

        switch (channel.Status)
        {
            case MsrStatus.NoAdmin:
                snap.InstallHints.Add("缺少管理员权限：真实功耗读取与 MSR 读写都需要管理员，请点界面上的「以管理员重启」。");
                break;
            case MsrStatus.NotInstalled:
                snap.InstallHints.Add(
                    "PawnIO 驱动未安装：请以管理员运行本程序，在诊断页点「安装 MSR 驱动」。" +
                    "安装会在系统内注册 PawnIO 内核驱动（微软 WHQL 签名）并创建设备节点。");
                break;
            case MsrStatus.DriverUnavailable:
                snap.InstallHints.Add(
                    "PawnIO 驱动已装但设备打不开：请重启电脑后重试；若仍失败，检查安全软件是否拦截了 PawnIO.sys。");
                break;
            case MsrStatus.ModuleUnavailable:
                snap.InstallHints.Add(
                    "IntelMSR 模块不可用：内置模块缺失或为只读版本（只含 ioctl_read_msr）。" +
                    "请使用完整安装包，或把 PawnIO.Modules 0.2.11+ 的 IntelMSR.bin 放到 " +
                    "%LOCALAPPDATA%\\ThermalTuner\\driver\\ 后重启程序。");
                break;
            case MsrStatus.ReadOnly:
                snap.InstallHints.Add("MSR 通道只读：可能被固件锁定；参数项只能查看不能微调。");
                break;
        }
        if (snap.Hvci)
            snap.InstallHints.Add("系统已开启内存完整性(HVCI)：第三方内核驱动可能被禁止加载，必要时临时关闭后重试。");
        if (snap.Virtualized)
            snap.InstallHints.Add("检测到虚拟化环境：功耗寄存器与遥测可能不可信，写入能力会被收紧。");
        snap.InstallHints.Add(
            "运行时依赖：框架依赖版需目标机安装 .NET 8 桌面运行时；自包含版免安装（推荐用自包含包）。");

        bool pl610 = channel.TryReadLimits(out _);
        bool hwp = channel.TryRead(HwpRequest.Msr, out _);
        bool turbo = channel.TryRead(TurboRatioLimit.MsrTurboRatioLimit, out ulong turboRaw) && turboRaw != 0;
        bool voltage = channel.TryRead(FivrVoltage.Msr, out _);
        bool raplUnit = channel.TryRead(0x606, out _);
        bool raplEnergy = channel.TryRead(0x611, out _);

        snap.Readable["PL1"] = pl610;
        snap.Readable["PL2"] = pl610;
        snap.Readable["Tau"] = pl610;
        snap.Readable["EPP"] = hwp;
        snap.Readable["MinPerf"] = hwp;
        snap.Readable["MaxPerf"] = hwp;
        snap.Readable["ActivityWindow"] = hwp;
        snap.Readable["Turbo"] = turbo;
        snap.Readable["Voltage"] = voltage;
        snap.Readable["Rapl"] = raplUnit && raplEnergy;
        snap.Readable["Temp"] = tempAvailable;
        snap.Readable["Power"] = powerAvailable;

        // ---- 3. UI 禁用清单：以「能否获取」为准 ----
        if (!pl610) snap.DisabledItems.AddRange(new[] { "PL1", "PL2", "Tau" });
        if (!hwp) snap.DisabledItems.AddRange(new[] { "EPP", "MinPerf", "MaxPerf", "ActivityWindow" });
        if (!turbo) snap.DisabledItems.Add("Turbo");
        if (!voltage) snap.DisabledItems.Add("Voltage");
        // 电压即使可读，也可能实测不可写：沿用 --voltage-probe 落盘的结论
        if (voltage && SettingsStore.Load().VoltageWritable == false) snap.DisabledItems.Add("Voltage");
        if (!snap.DisabledItems.Contains("Voltage") && voltage
            && !CpuCapability.Probe(env.BrandString).VoltageOffsetPlausible)
            snap.DisabledItems.Add("Voltage");

        // 去重
        snap.DisabledItems = snap.DisabledItems.Distinct().ToList();

        // ---- 3.5 风扇数据源能力（只读探测，绝不写 EC） ----
        // 这里不持有 HardwareTelemetry 实例，故只探测 EC / NBFC 链；报告会如实标注范围，
        // 避免把「本机无 LHM 传感器」误写成「所有机型都没有风扇数据源」。
        try
        {
            var fan = FanSources.Read(null);
            snap.FanTransport = fan.Capability.Transport.ToString();
            snap.FanMeasure = fan.Capability.Measure.ToString();
            snap.FanReadable = fan.HasValue;
            snap.FanWritable = fan.Capability.CanWrite;
            snap.FanDetail = fan.HasValue
                ? $"{fan.Capability.Name}｜{fan.Reason}"
                : fan.Reason;
        }
        catch (Exception ex)
        {
            snap.FanTransport = "Unavailable";
            snap.FanMeasure = "None";
            snap.FanDetail = $"风扇能力探测异常：{ex.GetType().Name}";
        }

        // ---- 4. 写能力：通道状态给初值，实测仅在授权且管理员时补 ----
        bool channelWritable = channel.CanWrite;
        foreach (string key in new[] { "PL1", "PL2", "Tau", "EPP", "MinPerf", "MaxPerf", "ActivityWindow", "Turbo" })
            snap.Writable[key] = false;   // 未实测前不宣称可写
        snap.Writable["Voltage"] = false;

        if (allowWriteProbe && snap.Elevated && channelWritable)
        {
            using var guard = new MsrGuard(channel, SettingsStore.DataDirectory);
            if (channel.TryReadLimits(out RaplLimits lim))
            {
                var w = guard.Apply(lim.Pl1W, lim.Pl2W, lim.TauS);   // 同值写回，零净效果
                snap.Writable["PL1"] = snap.Writable["PL2"] = snap.Writable["Tau"] = w.Ok;
            }
            if (hwp)
            {
                var cur = HwpRequest.Decode(channel.TryRead(HwpRequest.Msr, out ulong hr) ? hr : 0);
                int probeEpp = cur.Epp == 128 ? 64 : 128;
                var wh = guard.ApplyHwp(-1, -1, probeEpp, -1, cur.MaxPerf);
                snap.Writable["EPP"] = snap.Writable["MinPerf"]
                    = snap.Writable["MaxPerf"] = snap.Writable["ActivityWindow"] = wh.Ok;
                guard.RestoreEpp();
            }
            if (turbo)
            {
                var wt = guard.ApplyTurbo(TurboRatioLimit.Decode(turboRaw).AllCore);
                snap.Writable["Turbo"] = wt.Ok;
                guard.RestoreTurbo();
            }
            if (voltage)
            {
                var wv = guard.ApplyVoltage(-10);      // 极小降压，失败也强制回零
                snap.Writable["Voltage"] = wv.Ok;
                guard.RestoreVoltage();
                try
                {
                    var s = SettingsStore.Load();
                    s.VoltageWritable = wv.Ok;
                    SettingsStore.Save(s);
                }
                catch { /* 落盘失败不影响本次探测 */ }
                if (!wv.Ok && !snap.DisabledItems.Contains("Voltage"))
                    snap.DisabledItems.Add("Voltage");
            }
            snap.WriteProbeDone = true;
        }

        Save(snap);
        return snap;
    }

    /// <summary>可读报告，用于 --env-check 与界面复制。</summary>
    public static string Report(CapabilitySnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==========================================================");
        sb.AppendLine(" ThermalTuner 环境自检 / 参数能力报告");
        sb.AppendLine($" 检查时间: {s.CheckedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("==========================================================");
        sb.AppendLine();
        sb.AppendLine("== 1. 环境 ==");
        sb.AppendLine($"CPU            : {s.CpuName}");
        sb.AppendLine($"管理员         : {(s.Elevated ? "是" : "否（真实读写需提权）")}");
        sb.AppendLine($"虚拟化 / HVCI  : {(s.Virtualized ? "是" : "否")} / {(s.Hvci ? "已开启" : "未开启")}");
        sb.AppendLine($"LHM 版本       : {s.LhmVersion}（要求 0.9.6+，PawnIO 后端）");
        sb.AppendLine($"MSR 后端       : {s.Backend}");
        sb.AppendLine($"通道状态       : {s.MsrStatus}");
        sb.AppendLine($"通道详情       : {s.MsrDetail}");
        sb.AppendLine($"写能力实测     : {(s.WriteProbeDone ? "已完成" : "未做（需管理员跑 --env-check）")}");
        if (s.MsrStatus.Contains("NoAdmin", StringComparison.OrdinalIgnoreCase) && s.CanRead("PL1"))
            sb.AppendLine("说明           : 当前进程非管理员，只读探测可能仍可用；这是「能看不能写」状态，" +
                          "真实写入需点界面「以管理员重启」，否则请求会走演练或被驱动拒绝。");
        sb.AppendLine();
        sb.AppendLine("== 1.1 风扇数据源（EC / NBFC 只读链探测，不含 LHM 传感器通道） ==");
        sb.AppendLine($"通道 / 量纲    : {s.FanTransport} / {s.FanMeasure}");
        sb.AppendLine($"可读 / 可写    : {(s.FanReadable ? "是" : "否（UI 显示 N/A）")} / {(s.FanWritable ? "是" : "否（全通道只读）")}");
        sb.AppendLine($"详情           : {s.FanDetail}");
        sb.AppendLine("配置优先级     : LhmSensor → EcPortIo → NbfcService → Unavailable（OemAcpi 预留，未接入）");
        sb.AppendLine("口径           : 档位百分比 ≠ RPM；语义未确认的 EC 原始字节不得换算成转速展示");
        sb.AppendLine();
        sb.AppendLine("== 2. 参数项可获取性 / 可写性 ==");
        foreach (var key in new[] { "PL1", "PL2", "Tau", "EPP", "MinPerf", "MaxPerf",
                    "ActivityWindow", "Turbo", "Voltage", "Rapl", "Temp", "Power" })
        {
            bool metering = key is "Rapl" or "Temp" or "Power";
            sb.AppendLine($"  {key,-16} 可读 {(s.CanRead(key) ? "✓" : "✗")}" +
                          $" · 可写 {(s.CanWrite(key) ? "✓" : s.WriteProbeDone ? "✗" : "未实测")}" +
                          (metering
                              ? " · 只读计量项，不涉及编辑"
                              : $" · 编辑 {(s.IsEditable(key) ? "开放" : "禁用")}"));
        }
        sb.AppendLine();
        sb.AppendLine("== 3. 外部依赖与处置建议 ==");
        sb.AppendLine(s.DependencyText);
        sb.AppendLine();
        sb.AppendLine("== 4. UI 禁用结论 ==");
        sb.AppendLine(s.DisabledText);
        sb.AppendLine();
        sb.AppendLine("== 5. CPU 能力预筛（型号只作提示，以实测为准） ==");
        sb.AppendLine(s.CpuCapabilityNote);
        if (!string.IsNullOrWhiteSpace(s.TelemetryNote))
        {
            sb.AppendLine();
            sb.AppendLine("== 6. 遥测 ==");
            sb.AppendLine(s.TelemetryNote);
        }
        sb.AppendLine();
        sb.AppendLine($"能力快照       : {FilePath}");
        sb.AppendLine("RESULT: 环境自检完成");
        return sb.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>读 WMI DeviceGuard 判断 HVCI，失败返回 false（不编造）。</summary>
    private static bool ReadHvci()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, CodeIntegrityPolicyEnforcementStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    int status = Convert.ToInt32(mo["VirtualizationBasedSecurityStatus"] ?? 0,
                        CultureInfo.InvariantCulture);
                    int ci = Convert.ToInt32(mo["CodeIntegrityPolicyEnforcementStatus"] ?? 0,
                        CultureInfo.InvariantCulture);
                    if (status >= 1 || ci >= 1) return true;
                }
            }
        }
        catch { /* WMI 不可用时按未知处理 */ }
        return false;
    }
}
