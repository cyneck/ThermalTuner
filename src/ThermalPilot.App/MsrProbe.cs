using System.IO;
using System.Text;
using ThermalPilot.App.Services;
using ThermalPilot.App.Services.Msr;
using ThermalPilot.Core.Msr;

namespace ThermalPilot.App;

/// <summary>
/// MSR 通道自检（只读）：
/// 报告 PawnIO 库、LHM 模块、驱动句柄、0x610 解码结果。不做任何写入。
/// 用法：ThermalPilot.App.exe --msr-probe --out msr-probe.txt
/// </summary>
internal static class MsrProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("== PawnIO ==");
        sb.AppendLine($"Lib 可用    : {PawnIoNative.IsAvailable}");
        sb.AppendLine($"Lib 路径    : {PawnIoNative.LibraryPath ?? "(未找到)"}");
        if (PawnIoNative.LoadError is not null)
            sb.AppendLine($"加载错误    : {PawnIoNative.LoadError}");

        sb.AppendLine();
        sb.AppendLine("== LibreHardwareMonitor 内嵌模块 ==");
        var names = LhmPawnModule.AvailableModuleNames();
        sb.AppendLine($"资源总数    : {names.Length}");
        foreach (var n in names.Where(x => x.Contains("msr", StringComparison.OrdinalIgnoreCase)
                                        || x.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("  " + n);

        byte[]? blob = LhmPawnModule.TryReadIntelMsr();
        sb.AppendLine($"IntelMSR blob: {(blob is null ? "未找到" : $"{blob.Length} 字节")}");

        sb.AppendLine();
        sb.AppendLine("== 通道能力（只读，不写 MSR）==");
        using var channel = MsrChannel.Open();
        sb.AppendLine($"状态        : {channel.Status} / {MsrChannel.StatusText(channel.Status)}");
        sb.AppendLine($"详情        : {channel.Detail}");

        if (channel.TryReadLimits(out RaplLimits limits))
        {
            sb.AppendLine($"0x610 原始  : 0x{limits.Raw:X16}");
            sb.AppendLine($"PL1         : {limits.Pl1W:F3} W (enabled={limits.Pl1Enabled})");
            sb.AppendLine($"PL2         : {limits.Pl2W:F3} W (enabled={limits.Pl2Enabled}, clamp={limits.Pl2Clamp})");
            sb.AppendLine($"Tau         : {limits.TauS:F0} s");
            sb.AppendLine($"Lock        : {limits.Locked}");
        }
        else
        {
            sb.AppendLine("0x610 读取  : 失败（需要管理员权限，或本机被虚拟化拦截）");
        }

        sb.AppendLine();
        sb.AppendLine($"数据目录    : {SettingsStore.DataDirectory}");
        string backup = Path.Combine(SettingsStore.DataDirectory, "backup", "msr-startup.json");
        sb.AppendLine($"启动备份    : {(File.Exists(backup) ? backup : "尚未创建（首次可读时自动创建）")}");

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
        sb.AppendLine($"通道状态: {channel.Status} / {channel.Detail}");

        if (channel.Status != MsrStatus.Writable)
        {
            sb.AppendLine("跳过：通道不可写（需管理员 + PawnIO + 未锁定）");
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

        sb.AppendLine($"写入前: {RaplPowerLimit.Describe(before.Value.Raw)}");

        // 同值写回：语义上是空操作，只验证驱动与回读链路
        var write = guard.Apply(before.Value.Pl1W, before.Value.Pl2W, before.Value.TauS);
        sb.AppendLine($"同值写入: {write.Outcome} / {write.Message}");
        if (write.After is { } after)
            sb.AppendLine($"回读值  : {RaplPowerLimit.Describe(after.Raw)}");

        var restore = guard.Restore();
        sb.AppendLine($"恢复原值: {restore.Outcome} / {restore.Message}");

        bool ok = write.Outcome == MsrWriteOutcome.Success && restore.Outcome == MsrWriteOutcome.Restored;
        sb.AppendLine();
        sb.AppendLine(ok ? "RESULT: PASS（写入→回读→恢复全链路可用）" : "RESULT: FAIL");

        Flush(outPath, sb);
        return ok ? 0 : 5;
    }

    /// <summary>
    /// 模块函数探测：以「零参数」调用候选函数名。
    ///   HRESULT 0x80070002 = 函数不存在；
    ///   其它返回值（如 0xC000000D 参数无效）= 函数存在但参数不对。
    /// 零参数调用不会产生任何写入，可安全用于列出模块能力。
    /// </summary>
    public static int RunFunctionProbe(string outPath)
    {
        var sb = new StringBuilder();
        using var channel = MsrChannel.Open();
        sb.AppendLine($"通道: {channel.Status} / {channel.Detail}");
        if (channel.Status is not (MsrStatus.Writable or MsrStatus.ReadOnly))
        {
            sb.AppendLine("跳过：通道不可用（需管理员 + PawnIO）");
            Flush(outPath, sb);
            return 3;
        }

        string[] candidates =
        {
            "ioctl_read_msr",
            "ioctl_write_msr",
            "ioctl_write_msr_safe",
            "ioctl_set_power_limit",
            "ioctl_write_power_limit",
            "ioctl_pkg_power_limit",
            "__definitely_not_a_function__"
        };

        sb.AppendLine();
        sb.AppendLine("函数名                              HRESULT      判定");
        foreach (var name in candidates)
        {
            int hr = channel.InvokeRaw(name, Array.Empty<ulong>());
            string verdict = hr switch
            {
                unchecked((int)0x80070002) => "不存在",
                unchecked((int)0x80070057) => "存在（参数无效）",
                unchecked((int)0xC000000D) => "存在（参数无效）",
                _ when hr >= 0 => "存在（调用成功）",
                _ => "存在（返回错误）"
            };
            sb.AppendLine($"{name,-35} 0x{hr:X8}   {verdict}");
        }

        Flush(outPath, sb);
        return 0;
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
