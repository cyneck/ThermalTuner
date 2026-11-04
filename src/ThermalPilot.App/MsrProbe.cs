using System.IO;
using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;
using ThermalPilot.App.Services;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Msr;

namespace ThermalPilot.App;

/// <summary>
/// MSR 通道自检（只读）：报告 LHM 版本、Ring0 驱动后端、0x610 解码结果。不做任何写入。
/// 用法：ThermalPilot.App.exe --msr-probe --out msr-probe.txt
/// </summary>
internal static class MsrProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("== MSR 后端 ==");
        sb.AppendLine($"后端         : {MsrChannel.BackendName}");
        sb.AppendLine($"LHM 程序集   : {typeof(Computer).Assembly.FullName}");
        sb.AppendLine($"位置         : {typeof(Computer).Assembly.Location}");

        sb.AppendLine();
        sb.AppendLine("== Ring0 接口 ==");
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
            sb.AppendLine("0x610 读取   : 失败（需要管理员权限，或 WinRing0 驱动被拦截）");
        }

        // 0x774 / 0x1AD 的可读性（老版本可调 EPP / 睿频倍率）
        foreach (var (msr, name) in new[] { (0x774u, "HWP_REQUEST EPP"), (0x1ADu, "TURBO_RATIO_LIMIT") })
        {
            if (channel.TryRead(msr, out ulong raw))
                sb.AppendLine($"0x{msr:X3} {name,-20}: 0x{raw:X16}（可读）");
            else
                sb.AppendLine($"0x{msr:X3} {name,-20}: 不可读");
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
            sb.AppendLine("跳过：通道不可写（需管理员 + WinRing0 驱动已加载 + 0x610 未锁定）");
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
    /// 列出 Ring0 暴露的 MSR 方法，并做一次只读连通性验证，不做任何写入。
    /// </summary>
    public static int RunFunctionProbe(string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"后端: {MsrChannel.BackendName}");
        sb.AppendLine($"LHM: {typeof(Computer).Assembly.FullName}");
        sb.AppendLine();
        sb.AppendLine("== Ring0 MSR 接口 ==");
        var funcs = MsrChannel.ListFunctions();
        if (funcs.Length == 0)
            sb.AppendLine("  （未找到：通常是引用了 0.9.6+ PawnIO 分支，需回退到 0.9.4）");
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
            sb.AppendLine("跳过：通道不可写（需管理员 + WinRing0 驱动 + 0x610 未锁）");
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
