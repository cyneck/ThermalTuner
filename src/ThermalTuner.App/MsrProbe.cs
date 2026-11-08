using System.IO;
using System.Threading;
using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;
using ThermalTuner.App.Services;
using ThermalTuner.App.Services.Msr;
using ThermalTuner.Core.Environment;
using ThermalTuner.Core.Msr;
using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App;

/// <summary>
/// MSR 通道自检（只读）：报告 LHM 版本、PawnIO 后端与模块函数、0x610 解码结果。不做任何写入。
/// 用法：ThermalTuner.App.exe --msr-probe --out msr-probe.txt
/// </summary>
internal static class MsrProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("== MSR 后端 ==");
        sb.AppendLine($"后端         : {MsrChannel.BackendName}");
        sb.AppendLine($"LHM 程序集   : {typeof(Computer).Assembly.FullName}");
        // 单文件发布时程序集内嵌，Assembly.Location 恒为空（IL3000）；用程序目录代替
        sb.AppendLine($"程序目录     : {AppContext.BaseDirectory}（单文件发布时 LHM DLL 内嵌于 exe）");

        sb.AppendLine();
        sb.AppendLine("== PawnIO 模块函数 ==");
        var funcs = MsrChannel.ListFunctions();
        sb.AppendLine($"MSR 相关方法 : {funcs.Length} 个");
        foreach (var f in funcs) sb.AppendLine("  " + f);

        sb.AppendLine();
        sb.AppendLine("== 通道能力（只读，不写 MSR）==");
        using var channel = MsrChannel.Open();
        sb.AppendLine($"状态         : {channel.Status} / {MsrChannel.StatusText(channel.Status)}");
        sb.AppendLine($"详情         : {channel.Detail}");

        if (channel.TryReadLimits(out RaplLimits limits))
        {
            sb.AppendLine($"0x610 原始   : 0x{limits.Raw:X16}");
            sb.AppendLine($"PL1          : {limits.Pl1W:F3} W (enabled={limits.Pl1Enabled})");
            sb.AppendLine($"PL2          : {limits.Pl2W:F3} W (enabled={limits.Pl2Enabled}, clamp={limits.Pl2Clamp})");
            sb.AppendLine($"Tau          : {limits.TauS:F0} s");
            sb.AppendLine($"Lock         : {limits.Locked}");
        }
        else
        {
            sb.AppendLine("0x610 读取   : 失败（需要管理员权限，或 PawnIO 驱动 / 模块不可用）");
        }

        // 核心高级参数的寄存器可读性盘点（全部只读，不做任何写入）。
        // 覆盖：RAPL 功耗墙、HWP 选频、温度目标、睿频表、OC Mailbox 电压。
        sb.AppendLine();
        sb.AppendLine("== 核心参数寄存器实读（只读，不写 MSR）==");
        foreach (var (msr, name) in new[]
                 {
                     (0x606u, "RAPL_POWER_UNIT"),
                     (0x610u, "PKG_POWER_LIMIT"),
                     (0x611u, "PKG_ENERGY_STATUS"),
                     (0x770u, "HWP_CAPABILITIES"),
                     (0x772u, "HWP_REQUEST_PKG"),
                     (0x774u, "HWP_REQUEST"),
                     (0x1A0u, "MISC_ENABLE"),
                     (0x1A2u, "TEMPERATURE_TARGET"),
                     (0x1ADu, "TURBO_RATIO_LIMIT"),
                     (0x150u, "OC_MAILBOX/FIVR"),
                     (0x194u, "FLEX_RATIO")
                 })
        {
            sb.AppendLine(channel.TryRead(msr, out ulong raw)
                ? $"  0x{msr:X3} {name,-22}: 0x{raw:X16}（可读）"
                : $"  0x{msr:X3} {name,-22}: 不可读");
        }

        if (channel.TryRead(0x774, out ulong hwp))
        {
            sb.AppendLine();
            sb.AppendLine("== 0x774 HWP_REQUEST 位域解码 ==");
            sb.AppendLine($"  Min Perf       [7:0]   : {hwp & 0xFF}");
            sb.AppendLine($"  Max Perf       [15:8]  : {(hwp >> 8) & 0xFF}");
            sb.AppendLine($"  Desired Perf   [23:16] : {(hwp >> 16) & 0xFF}（0 = 硬件自主选频）");
            sb.AppendLine($"  EPP            [31:24] : {(hwp >> 24) & 0xFF}（0=性能 / 255=省电）");
            sb.AppendLine($"  Activity Win   [41:32] : {(hwp >> 32) & 0x3FF}");
            sb.AppendLine($"  Package Control  bit42 : {(hwp >> 42) & 1}");
        }

        if (channel.TryRead(0x1A2, out ulong tt))
        {
            sb.AppendLine();
            sb.AppendLine("== 0x1A2 TEMPERATURE_TARGET 位域解码 ==");
            sb.AppendLine($"  TjMax Target  [23:16] : {(tt >> 16) & 0xFF} °C");
            sb.AppendLine($"  TjMax Offset  [29:24] : {(tt >> 24) & 0x3F}");
        }

        if (channel.TryRead(0x150, out ulong oc))
        {
            long raw = (long)((oc >> 21) & 0x7FF);
            if ((raw & 0x400) != 0) raw -= 0x800;      // 11 位补码，单位 1/1024 V
            sb.AppendLine();
            sb.AppendLine("== 0x150 OC Mailbox 电压偏移（只读解析）==");
            sb.AppendLine($"  原始值         : 0x{oc:X16}");
            sb.AppendLine($"  Core/Cache 偏移: {Math.Round(raw * 1000.0 / 1024.0, 1):F1} mV（0 = 未施加降压）");
        }

        sb.AppendLine();
        sb.AppendLine($"数据目录     : {SettingsStore.DataDirectory}");
        string backup = Path.Combine(SettingsStore.DataDirectory, "backup", "msr-startup.json");
        sb.AppendLine($"启动备份     : {(File.Exists(backup) ? backup : "尚未创建（首次可读时自动创建）")}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠退出码 */ }

        return channel.Status == MsrStatus.Writable ? 0 : 2;
    }

    /// <summary>
    /// 写入自检：把「当前值」原样写回再恢复，验证 写入→回读→回滚 全链路，不改变实际功耗设置。
    /// 需要管理员权限。用法：--write-test --out wt.txt
    /// </summary>
    public static int RunWriteTest(string outPath)
    {
        var sb = new StringBuilder();
        using var channel = MsrChannel.Open();
        sb.AppendLine($"后端       : {MsrChannel.BackendName}");
        sb.AppendLine($"通道状态   : {channel.Status} / {channel.Detail}");

        if (channel.Status != MsrStatus.Writable)
        {
            sb.AppendLine("跳过：通道不可写（需管理员 + PawnIO 驱动已装 + 0x610 未锁定）");
            Flush(outPath, sb);
            return 3;
        }

        using var guard = new MsrGuard(channel, SettingsStore.DataDirectory);
        var before = guard.StartupBackup;
        if (before is null)
        {
            sb.AppendLine("跳过：未能读取启动备份");
            Flush(outPath, sb);
            return 4;
        }

        sb.AppendLine($"写入前     : {RaplPowerLimit.Describe(before.Value.Raw)}");

        // 同值写回：语义上是空操作，只验证驱动与回读链路
        var write = guard.Apply(before.Value.Pl1W, before.Value.Pl2W, before.Value.TauS);
        sb.AppendLine($"同值写入   : {write.Outcome} / {write.Message}");
        if (write.After is { } after)
            sb.AppendLine($"回读值     : {RaplPowerLimit.Describe(after.Raw)}");

        var restore = guard.Restore();
        sb.AppendLine($"恢复原值   : {restore.Outcome} / {restore.Message}");

        bool ok = write.Outcome == MsrWriteOutcome.Success && restore.Outcome == MsrWriteOutcome.Restored;
        sb.AppendLine();
        sb.AppendLine(ok ? "RESULT: PASS（写入→回读→恢复全链路可用）" : "RESULT: FAIL");

        Flush(outPath, sb);
        return ok ? 0 : 5;
    }

    /// <summary>
    /// 接口能力探测（替代旧的 Pawn 模块函数探测）：
    /// 列出 IntelMSR 模块暴露的函数，并做一次只读连通性验证，不做任何写入。
    /// </summary>
    public static int RunFunctionProbe(string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"后端: {MsrChannel.BackendName}");
        sb.AppendLine($"LHM: {typeof(Computer).Assembly.FullName}");
        sb.AppendLine();
        sb.AppendLine("== IntelMSR 模块函数 ==");
        var funcs = MsrChannel.ListFunctions();
        if (funcs.Length == 0)
            sb.AppendLine("  （未找到：IntelMSR 模块未加载，请检查驱动与模块资源）");
        foreach (var f in funcs) sb.AppendLine("  " + f);

        using var channel = MsrChannel.Open();
        sb.AppendLine();
        sb.AppendLine($"通道: {channel.Status} / {channel.Detail}");
        sb.AppendLine($"写能力: {(channel.CanWrite ? "可用（写入由 MsrGuard 回读校验兜底）" : "不可用")}");
        if (channel.TryReadLimits(out var lim))
            sb.AppendLine($"0x610: {RaplPowerLimit.Describe(lim.Raw)}");

        Flush(outPath, sb);
        return channel.CanWrite ? 0 : 2;
    }

    /// <summary>
    /// 睿频倍率表自检：读当前/出厂档位与锁位 → 写入探针档与出厂单核上限 → 回读校验 → 恢复出厂备份。
    /// 当前值若来自开机重放，会先写探针档确保真实走一次写入；结束时寄存器为固件默认倍率表。
    /// 用法：--turbo-probe --out turbo.txt（需管理员）。
    /// </summary>
    public static int RunTurboProbe(string outPath)
    {
        var sb = new StringBuilder();
        using var channel = MsrChannel.Open();
        sb.AppendLine("== 睿频倍率表（MSR 0x1AD）自检 ==");
        sb.AppendLine($"后端       : {MsrChannel.BackendName}");
        sb.AppendLine($"通道状态   : {channel.Status} / {MsrChannel.StatusText(channel.Status)}");
        sb.AppendLine($"详情       : {channel.Detail}");
        sb.AppendLine();

        if (channel.Status != MsrStatus.Writable)
        {
            sb.AppendLine("跳过：通道不可写（需管理员 + PawnIO 驱动 + 0x610 未锁）");
            Flush(outPath, sb);
            return 3;
        }

        sb.AppendLine("== 1. 出厂档位与硬件锁位 ==");
        foreach (var (msr, name) in new[]
                 {
                     (TurboRatioLimit.MsrPlatformInfo, "PLATFORM_INFO"),
                     (TurboRatioLimit.MsrFlexRatio, "FLEX_RATIO"),
                     (TurboRatioLimit.MsrMiscEnable, "MISC_ENABLE")
                 })
        {
            sb.AppendLine(channel.TryRead(msr, out ulong v)
                ? $"  0x{msr:X3} {name,-14}: 0x{v:X16}"
                : $"  0x{msr:X3} {name,-14}: 不可读");
        }
        if (channel.TryRead(TurboRatioLimit.MsrFlexRatio, out ulong flex))
            sb.AppendLine($"  Overclocking Lock (0x194 bit20): {(((flex >> 20) & 1) != 0 ? "已锁定" : "未锁定（倍频表可写）")}");
        if (channel.TryRead(TurboRatioLimit.MsrMiscEnable, out ulong misc))
            sb.AppendLine($"  Turbo Disable (0x1A0 bit38)   : {(((misc >> 38) & 1) != 0 ? "睿频已禁用" : "睿频开启")}");

        if (!channel.TryRead(TurboRatioLimit.MsrTurboRatioLimit, out ulong raw) || raw == 0)
        {
            sb.AppendLine("  0x1AD 读不到 / 全 0 → 该平台不暴露睿频倍率表，无法调节。");
            sb.AppendLine("RESULT: SKIP");
            Flush(outPath, sb);
            return 4;
        }
        var current = TurboRatioLimit.Decode(raw);
        using var guard = new MsrGuard(channel, SettingsStore.DataDirectory);

        // 当前值可能已被开机重放的手动微调改写（例如保存过 36x），
        // 真正出厂值以启动备份为准；没有备份时才退回当前值。
        ulong factoryRaw = guard.TurboSupported && guard.StartupTurboBackup is not null
            ? guard.StartupTurboBackup.Raw
            : raw;
        var factory = TurboRatioLimit.Decode(factoryRaw);

        sb.AppendLine($"  当前 0x1AD : {TurboRatioLimit.Describe(current)}" +
                      (raw == factoryRaw ? "" : "（含开机重放，非出厂值）"));
        sb.AppendLine($"  出厂默认   : {TurboRatioLimit.Describe(factory)}");
        sb.AppendLine($"  允许范围   : [{TurboRatioLimit.MinRatio}, {factory.MaxTier}] ×100MHz（不做超出出厂规格的超频）");
        sb.AppendLine();
        sb.AppendLine($"  启动备份   : {(guard.TurboSupported ? TurboRatioLimit.Describe(guard.StartupTurboBackup!) : "无")}");
        sb.AppendLine();
        sb.AppendLine("== 2. 写入 → 回读 → 恢复 ==");

        // 若当前已等于出厂上限，先写一个探针档位，确保真的走一次「写入→回读」，
        // 否则会出现「无需写入」却仍判 PASS 的假阳性。
        int targetRatio = factory.MaxTier;
        bool probeOk = true;
        if (TurboRatioLimit.AllTiersEqual(raw, targetRatio))
        {
            int probeRatio = Math.Max(TurboRatioLimit.MinRatio, targetRatio - 2);
            var wp = guard.ApplyTurbo(probeRatio);
            var probeState = guard.ReadTurbo();
            probeOk = wp.Ok && probeState is not null &&
                      TurboRatioLimit.AllTiersEqual(probeState.Raw, probeRatio);
            sb.AppendLine($"  探针写入 {probeRatio}x : {wp.Outcome} / {wp.Message}");
            sb.AppendLine($"  探针后实读   : {(probeState is null ? "--" : TurboRatioLimit.Describe(probeState))}");
        }

        var w = guard.ApplyTurbo(targetRatio);
        sb.AppendLine($"  写入 {targetRatio}x : {w.Outcome} / {w.Message}");
        var after = guard.ReadTurbo();
        sb.AppendLine($"  写入后实读 : {(after is null ? "--" : TurboRatioLimit.Describe(after))}");
        var r = guard.RestoreTurbo();
        sb.AppendLine($"  恢复出厂   : {r.Outcome} / {r.Message}");
        var final = guard.ReadTurbo();
        sb.AppendLine($"  最终实读   : {(final is null ? "--" : TurboRatioLimit.Describe(final))}");

        bool wroteTarget = w.Ok && after is not null &&
                           TurboRatioLimit.AllTiersEqual(after.Raw, targetRatio);
        bool ok = probeOk && wroteTarget && r.Ok && final is not null && final.Raw == factoryRaw;
        sb.AppendLine();
        sb.AppendLine(ok
            ? "RESULT: PASS（0x1AD 可写、可回读校验、可精确恢复出厂档位）"
            : "RESULT: FAIL（详见上方；如最终实读不等于出厂值，请重启机器）");
        Flush(outPath, sb);
        return ok ? 0 : 5;
    }

    /// <summary>
    /// 通用 HWP 参数写入自检：0x774 的 Min/Max Perf、EPP、Activity Window 各写一次探针值，
    /// 回读校验后整字恢复到出厂基线。Desired Perf / Package Control 不参与。
    /// 用法：--hwp-probe --out hwp.txt（需管理员）。
    /// </summary>
    public static int RunHwpProbe(string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 0x774 HWP 通用参数写入自检 ==");
        using var channel = MsrChannel.Open();
        sb.AppendLine($"通道状态   : {channel.Status} / {channel.Detail}");
        if (channel.Status != MsrStatus.Writable)
        {
            sb.AppendLine("RESULT: SKIP（需管理员 + PawnIO 驱动）");
            Flush(outPath, sb);
            return 3;
        }

        using var guard = new MsrGuard(channel, SettingsStore.DataDirectory);
        var before = guard.ReadHwp();
        if (before is null)
        {
            sb.AppendLine("RESULT: SKIP（0x774 不可读，本机不暴露 HWP 请求寄存器）");
            Flush(outPath, sb);
            return 4;
        }

        sb.AppendLine($"写入前     : {before.Value.Describe} / 0x{before.Value.Raw:X16}");
        ulong? factory = guard.StartupHwpRaw;
        sb.AppendLine($"出厂备份   : {(factory is { } f ? $"0x{f:X16}" : "无")}");

        int probeEpp = before.Value.Epp == 128 ? 64 : 128;
        int probeMin = Math.Max(0, before.Value.MinPerf - 1);
        int probeMax = Math.Max(probeMin, before.Value.MaxPerf - 1);
        int probeAw = before.Value.ActivityWindow == 0 ? 1 : before.Value.ActivityWindow - 1;
        sb.AppendLine($"探针目标   : Min {probeMin} / Max {probeMax} / EPP {probeEpp} / AW {probeAw}");

        var w = guard.ApplyHwp(probeMin, probeMax, probeEpp, probeAw, before.Value.MaxPerf);
        sb.AppendLine($"探针写入   : {w.Outcome} / {w.Message}");
        // 关键：0x774 可能被 Windows 电源计划 / Intel DTT 周期性覆盖。
        // 因此分「即时 / 250ms / 1.25s」三次实读，区分「写入失败」与「写入后被外部接管」。
        var immediate = guard.ReadHwp();
        sb.AppendLine($"即时实读   : {(immediate is null ? "--" : immediate.Value.Describe)}");
        Thread.Sleep(250);
        var after250 = guard.ReadHwp();
        sb.AppendLine($"250ms 实读 : {(after250 is null ? "--" : after250.Value.Describe)}");
        Thread.Sleep(1000);
        var after1250 = guard.ReadHwp();
        sb.AppendLine($"1.25s 实读 : {(after1250 is null ? "--" : after1250.Value.Describe)}");

        var r = guard.RestoreEpp();
        sb.AppendLine($"恢复出厂   : {r.Outcome} / {r.Message}");
        var final = guard.ReadHwp();
        sb.AppendLine($"最终实读   : {(final is null ? "--" : final.Value.Describe)}");

        static bool Matches(HwpRequestState? s, int min, int max, int epp, int aw) =>
            s is { } x && x.MinPerf == min && x.MaxPerf == max && x.Epp == epp && x.ActivityWindow == aw;

        bool instantOk = w.Ok && Matches(immediate, probeMin, probeMax, probeEpp, probeAw);
        bool persisted = instantOk
                         && Matches(after250, probeMin, probeMax, probeEpp, probeAw)
                         && Matches(after1250, probeMin, probeMax, probeEpp, probeAw);
        bool restored = r.Ok && final is not null
                        && (factory is null || final.Value.Raw == factory.Value);

        sb.AppendLine();
        if (!instantOk || !restored)
        {
            sb.AppendLine("RESULT: FAIL（写入或恢复失败；若最终实读不等于出厂备份，请重启机器）");
            Flush(outPath, sb);
            return 5;
        }
        if (!persisted)
        {
            sb.AppendLine("RESULT: WARN（0x774 瞬时写入 + 回读校验通过，但随后被系统电源策略 / DTT 覆盖回原值）；");
            sb.AppendLine("       本平台 HWP 项不保证持续生效，产品不应对其做自动推荐承诺；已恢复出厂基线。");
            Flush(outPath, sb);
            return 2;
        }
        sb.AppendLine("RESULT: PASS（0x774 全字段可写、1.25s 内持续生效、可精确恢复出厂值；Desired/PkgCtl 未改写）");
        Flush(outPath, sb);
        return 0;
    }

    /// <summary>
    /// 个性化电压偏移能力自检：型号预筛 + 0x150 可读性 + 一次极小降压(-10mV) → 回读 → 强制回零。
    /// 目的不是调参，而是判定「该 CPU 是否真的可写」，供 UI 决定是否禁用该可选微调项。
    /// 用法：--voltage-probe --out voltage.txt（需管理员）。
    /// </summary>
    public static int RunVoltageProbe(string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 0x150 电压偏移能力自检（个性化可选微调）==");
        var cap = CpuCapability.Probe(HardwareProbe.Probe().BrandString);
        sb.AppendLine(cap.MatrixText);
        sb.AppendLine();

        using var channel = MsrChannel.Open();
        sb.AppendLine($"通道状态   : {channel.Status} / {channel.Detail}");
        if (!channel.TryRead(FivrVoltage.Msr, out ulong startup))
        {
            sb.AppendLine("RESULT: SKIP（0x150 不可读，电压偏移不可用）");
            Flush(outPath, sb);
            return 4;
        }
        sb.AppendLine($"启动响应   : 0x{startup:X16}（OC Mailbox 响应，不是实时电压）");
        if (channel.Status != MsrStatus.Writable)
        {
            sb.AppendLine("RESULT: SKIP（需管理员 + PawnIO 驱动）");
            Flush(outPath, sb);
            return 3;
        }

        using var guard = new MsrGuard(channel, SettingsStore.DataDirectory);
        sb.AppendLine($"型号预筛   : {(cap.VoltageOffsetPlausible ? "可能可用" : "大概率不可用")}");
        sb.AppendLine("探针幅度   : -10 mV（极小降压，写入后立即回零）");

        var w = guard.ApplyVoltage(-10);
        sb.AppendLine($"探针写入   : {w.Outcome} / {w.Message}");
        var r = guard.RestoreVoltage();
        sb.AppendLine($"强制回零   : {r.Outcome} / {r.Message}");
        channel.TryRead(FivrVoltage.Msr, out ulong final);
        sb.AppendLine($"最终响应   : 0x{final:X16}");

        bool ok = w.Ok && r.Ok;
        // 把实机结论落盘，UI 据此决定该个性化项是开放还是禁用（型号预筛只作提示）。
        try
        {
            var s = SettingsStore.Load();
            s.VoltageWritable = ok;
            SettingsStore.Save(s);
            sb.AppendLine($"自检结论落盘: VoltageWritable={ok}（settings.json；重跑本命令可刷新）");
        }
        catch { sb.AppendLine("自检结论落盘失败（不影响本次判定）"); }
        sb.AppendLine();
        sb.AppendLine(ok
            ? "RESULT: PASS（本机 0x150 可写；电压偏移可作为 L3 个性化可选微调，范围 [-150,0] mV）"
            : "RESULT: SKIP（本机不可写或回读被拒，已强制回零；UI 应把电压项标为不可用）");
        Flush(outPath, sb);
        return ok ? 0 : 2;   // 2 = 不支持，不是故障
    }

    private static void Flush(string outPath, StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 靠退出码 */ }
    }
}
