using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ThermalPilot.Core.Msr;

namespace ThermalPilot.App.Services.Msr;

public enum MsrWriteOutcome
{
    Success,
    NotWritable,
    FirmwareLocked,
    Rejected,
    WriteFailed,
    VerifyFailed,
    Restored,
    RestoreUnavailable,
    RestoreFailed
}

public sealed record MsrWriteResult(
    MsrWriteOutcome Outcome,
    string Message,
    RaplLimits? Before = null,
    RaplLimits? After = null)
{
    public bool Ok => Outcome is MsrWriteOutcome.Success or MsrWriteOutcome.Restored;
}

/// <summary>
/// MSR 写入护栏（PRD G-01 ~ G-08）：
///   G-01 备份：首次读取即落盘，之后永不覆盖原始备份
///   G-02 限幅：写入前由 HardwareLimits/ParamConstraints 钳制，非法直接拒绝
///   G-03 回读校验：写后立即回读，不一致自动回滚
///   G-04 脏标记：写入前落 pending 文件，异常退出后下次启动自动恢复
///   G-06 审计：每次写入/回滚追加日志
///   G-08 冷却保护：由调用方在超温/PROCHOT 时禁止提升（见 SessionState）
/// </summary>
public sealed class MsrGuard : IDisposable
{
    private const uint Msr = RaplPowerLimit.MsrPkgPowerLimit;
    private const uint MsrHwpRequest = HwpRequest.Msr;          // MSR_HWP_REQUEST 0x774
    private const uint MsrTurbo = TurboRatioLimit.MsrTurboRatioLimit;   // MSR_TURBO_RATIO_LIMIT = 0x1AD
    private const uint MsrVoltage = FivrVoltage.Msr;            // MSR 0x150 OC Mailbox（个性化可选微调）

    private readonly MsrChannel _channel;
    private readonly string _backupDir;
    private readonly string _backupPath;
    private readonly string _eppBackupPath;
    private readonly string _voltageBackupPath;
    private readonly string _pendingPath;
    private readonly string _turboBackupPath;
    private readonly string _turboPendingPath;
    private readonly string _auditPath;
    private readonly object _gate = new();

    private ulong? _startupRaw;
    private ulong? _startupEppRaw;
    private ulong? _startupTurboRaw;
    private ulong? _startupVoltageRaw;
    private bool _eppSupported;

    public MsrGuard(MsrChannel channel, string dataDir)
    {
        _channel = channel;
        _backupDir = Path.Combine(dataDir, "backup");
        _backupPath = Path.Combine(_backupDir, "msr-startup.json");
        _eppBackupPath = Path.Combine(_backupDir, "msr-epp-startup.json");
        _voltageBackupPath = Path.Combine(_backupDir, "msr-voltage-startup.json");
        _pendingPath = Path.Combine(_backupDir, "pending-restore.json");
        _turboBackupPath = Path.Combine(_backupDir, "msr-turbo-startup.json");
        _turboPendingPath = Path.Combine(_backupDir, "pending-turbo-restore.json");
        _auditPath = Path.Combine(dataDir, "logs", "msr-audit.log");

        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);

        EnsureStartupBackup();
        EnsureEppBackup();
        EnsureVoltageBackup();
        EnsureTurboBackup();
        PruneOldBackups();
    }

    public MsrChannel Channel => _channel;
    public bool CanWrite => _channel.CanWrite;
    public bool HasPendingRestore => File.Exists(_pendingPath);
    public bool HasTurboPendingRestore => File.Exists(_turboPendingPath);
    /// <summary>0x774 是否可读（部分机型不暴露 HWP 请求寄存器）。</summary>
    public bool EppSupported => _eppSupported;
    /// <summary>0x774 出厂整字备份（首次启动时记录，永不覆盖）。</summary>
    public ulong? StartupHwpRaw => _startupEppRaw;
    /// <summary>0x1AD 睿频倍率表是否可读（出厂默认值的锚点）。</summary>
    public bool TurboSupported => _startupTurboRaw.HasValue;
    /// <summary>0x150 是否可读。可读 ≠ 可写，写入能力必须靠实测。</summary>
    public bool VoltageReadable => _startupVoltageRaw.HasValue;
    public ulong? StartupVoltageRaw => _startupVoltageRaw;

    /// <summary>当前 0x774 实读状态（读不到返回 null）。</summary>
    public HwpRequestState? ReadHwp() =>
        _channel.TryRead(MsrHwpRequest, out ulong raw) ? HwpRequest.Decode(raw) : null;

    public RaplLimits? StartupBackup
    {
        get
        {
            if (_startupRaw is ulong raw) return RaplPowerLimit.Decode(raw);
            return null;
        }
    }

    /// <summary>固件出厂睿频倍率表（启动时备份，永不覆盖）。</summary>
    public TurboRatioState? StartupTurboBackup => _startupTurboRaw is ulong raw
        ? TurboRatioLimit.Decode(raw) : null;

    // ---------------- 备份 ----------------

    private void EnsureStartupBackup()
    {
        if (File.Exists(_backupPath))
        {
            var saved = ReadRaw(_backupPath);
            if (saved is not null) { _startupRaw = saved; return; }
        }

        if (!_channel.TryRead(Msr, out ulong raw)) return;

        _startupRaw = raw;
        WriteRawFile(_backupPath, raw, "startup");
    }

    /// <summary>EPP 启动备份（0x774）。与 0x610 一样，退出时恢复固件默认值。</summary>
    private void EnsureEppBackup()
    {
        if (File.Exists(_eppBackupPath))
        {
            var saved = ReadRaw(_eppBackupPath);
            if (saved is not null) { _startupEppRaw = saved; _eppSupported = true; return; }
        }

        if (!_channel.TryRead(MsrHwpRequest, out ulong raw)) { _eppSupported = false; return; }

        _startupEppRaw = raw;
        _eppSupported = true;
        var payload = new Dictionary<string, string>
        {
            ["kind"] = "epp-startup",
            ["timestamp"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            ["msr"] = $"0x{MsrHwpRequest:X}",
            ["original"] = raw.ToString(CultureInfo.InvariantCulture)
        };
        try
        {
            File.WriteAllText(_eppBackupPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 目录不可写时仅内存备份 */ }
    }

    /// <summary>睿频倍率表启动备份（0x1AD）。退出 / 恢复原值时用它精确还原出厂档位。</summary>
    private void EnsureTurboBackup()
    {
        if (File.Exists(_turboBackupPath))
        {
            var saved = ReadRaw(_turboBackupPath);
            if (saved is not null) { _startupTurboRaw = saved; return; }
        }

        if (!_channel.TryRead(MsrTurbo, out ulong raw) || raw == 0) return;

        _startupTurboRaw = raw;
        try { WriteRawFile(_turboBackupPath, raw, "turbo-startup", msr: MsrTurbo); }
        catch { /* 目录不可写时仅内存备份 */ }
    }

    /// <summary>0x150 OC Mailbox 启动快照。仅作「出厂状态参考」，恢复一律用回零指令。</summary>
    private void EnsureVoltageBackup()
    {
        if (File.Exists(_voltageBackupPath))
        {
            var saved = ReadRaw(_voltageBackupPath);
            if (saved is not null) { _startupVoltageRaw = saved; return; }
        }

        // 0x150 是命令/响应接口：读到的只是上次命令响应，不是实时电压。
        // 这里只记录「可读取」这一事实，不把响应值当成电压真值。
        if (!_channel.TryRead(MsrVoltage, out ulong raw)) return;
        _startupVoltageRaw = raw;
        try { WriteRawFile(_voltageBackupPath, raw, "voltage-startup", msr: MsrVoltage); }
        catch { /* 目录不可写时仅内存备份 */ }
    }

    /// <summary>保留最近 20 份备份（PRD §10）；启动/待恢复文件不参与清理。</summary>
    private void PruneOldBackups()
    {
        try
        {
            var files = Directory.GetFiles(_backupDir, "msr-*.json")
                .Where(f => !f.EndsWith("msr-startup.json", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith("msr-epp-startup.json", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith("msr-turbo-startup.json", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith("msr-voltage-startup.json", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith("pending-restore.json", StringComparison.OrdinalIgnoreCase)
                         && !f.EndsWith("pending-turbo-restore.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(20)
                .ToList();
            foreach (var f in files) File.Delete(f);
        }
        catch { /* 清理失败不影响功能 */ }
    }

    // ---------------- 写入 ----------------

    /// <param name="writeTimeWindow">
    /// 是否同时改写时间窗口。默认 false：部分厂商把该字段设成非常规值（如 0x6F），
    /// 在不确认机型布局前只动 PL1/PL2，避免写到未知位。
    /// </param>
    public MsrWriteResult Apply(double pl1W, double pl2W, double tauS, bool writeTimeWindow = false)
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable,
                    $"当前通道不可写：{_channel.Detail}");

            if (double.IsNaN(pl1W) || double.IsNaN(pl2W) || double.IsNaN(tauS))
                return new MsrWriteResult(MsrWriteOutcome.Rejected, "参数缺失，拒绝写入");

            if (pl2W < pl1W)
                return new MsrWriteResult(MsrWriteOutcome.Rejected,
                    $"PL2 ({pl2W:F1}W) 小于 PL1 ({pl1W:F1}W)，违反物理约束");

            if (writeTimeWindow && RaplPowerLimit.EncodeTau(tauS) < 0)
                return new MsrWriteResult(MsrWriteOutcome.Rejected,
                    $"Tau ({tauS:F0}s) 不是合法的 MSR 时间窗口编码");

            if (!_channel.TryRead(Msr, out ulong original))
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed, "写入前读取原值失败，已中止");

            var before = RaplPowerLimit.Decode(original);
            if (before.Locked)
                return new MsrWriteResult(MsrWriteOutcome.FirmwareLocked,
                    "0x610 的 lock 位已置位：固件锁定，写入不会生效", before);

            // 保留原时间窗口（除非显式要求改写）
            double effectiveTau = writeTimeWindow ? tauS : before.TauS;

            ulong target;
            try { target = RaplPowerLimit.Encode(original, pl1W, pl2W, effectiveTau); }
            catch (ArgumentOutOfRangeException ex)
            {
                return new MsrWriteResult(MsrWriteOutcome.Rejected, ex.Message, before);
            }

            // G-04：写前落 pending，异常退出后下次启动可恢复
            WritePending(original, target);

            if (!_channel.TryWrite(Msr, target, out int hrWrite))
            {
                ClearPending();
                // 驱动明确拒绝写入：降级为只读，避免每拍重复尝试
                if (hrWrite == unchecked((int)0x80070002))
                    _channel.MarkNotWritable("驱动不支持写入 0x610，本机降级为只读模式。");
                Audit("write", original, target, $"failed:hr=0x{hrWrite:X8}");
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    $"驱动拒绝写入（HRESULT 0x{hrWrite:X8}）：常见原因是 BIOS 锁定、HVCI 内存完整性或安全软件拦截内核驱动", before);
            }

            bool readBackOk = _channel.TryRead(Msr, out ulong readback);
            if (!readBackOk || !SameLimits(readback, target))
            {
                // G-03：回读不一致立即回滚
                bool rolled = _channel.TryWrite(Msr, original);
                ClearPending();
                Audit("write", original, target, rolled ? "verify-failed:rolled-back" : "verify-failed:rollback-failed");
                return new MsrWriteResult(MsrWriteOutcome.VerifyFailed,
                    rolled
                        ? "回读与目标不一致，已自动回滚到原值（可能被固件周期性改写）"
                        : "回读不一致且回滚失败，请重启机器恢复默认值",
                    before, readBackOk ? RaplPowerLimit.Decode(readback) : null);
            }

            ClearPending();
            Audit("write", original, target, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Success,
                $"已写入并回读校验通过（{RaplPowerLimit.Describe(readback)}）",
                before, RaplPowerLimit.Decode(readback));
        }
    }

    /// <summary>写 EPP（0x774 [31:24]），保留其它位，写后回读校验。</summary>
    public MsrWriteResult ApplyEpp(int epp)
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"当前通道不可写：{_channel.Detail}");
            if (!_eppSupported)
                return new MsrWriteResult(MsrWriteOutcome.Rejected,
                    "当前通道读不到 0x774，EPP 微调不可用（PL1/PL2/Tau 的写入不受影响）");
            if (epp < 0 || epp > 255)
                return new MsrWriteResult(MsrWriteOutcome.Rejected, "EPP 必须在 [0, 255]");

            if (!_channel.TryRead(MsrHwpRequest, out ulong original))
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed, "写入前读取 0x774 失败，已中止");

            ulong target = (original & ~((ulong)0xFF << 24)) | ((ulong)epp << 24);
            if (!_channel.TryWrite(MsrHwpRequest, target, out int hrWrite))
            {
                Audit("write-epp", original, target, $"failed:hr=0x{hrWrite:X8}");
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    $"驱动拒绝写入 0x774（HRESULT 0x{hrWrite:X8}），常见原因是 HWP/Speed Shift 被 BIOS 锁定");
            }

            if (!_channel.TryRead(MsrHwpRequest, out ulong readback) || readback != target)
            {
                _channel.TryWrite(MsrHwpRequest, original);
                Audit("write-epp", original, target, "verify-failed:rolled-back");
                return new MsrWriteResult(MsrWriteOutcome.VerifyFailed, "EPP 回读不一致，已自动回滚");
            }

            Audit("write-epp", original, target, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Success,
                $"EPP 已写入并回读校验通过（{epp}）");
        }
    }

    /// <summary>恢复 EPP 到启动时的固件值。</summary>
    public MsrWriteResult RestoreEpp()
    {
        lock (_gate)
        {
            if (!_eppSupported || _startupEppRaw is not ulong raw)
                return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "本机没有可用的 EPP 启动备份");
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"通道不可写：{_channel.Detail}");
            if (!_channel.TryRead(MsrHwpRequest, out ulong current))
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "读取当前 EPP 失败");

            if (current == raw) return new MsrWriteResult(MsrWriteOutcome.Restored, "EPP 已是固件默认值");

            if (!_channel.TryWrite(MsrHwpRequest, raw))
            {
                Audit("restore-epp", current, raw, "failed:driver-rejected");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "驱动拒绝恢复 EPP");
            }
            if (!_channel.TryRead(MsrHwpRequest, out ulong verify) || verify != raw)
            {
                Audit("restore-epp", current, raw, "failed:verify");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "EPP 恢复后回读不一致");
            }
            Audit("restore-epp", current, raw, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Restored, "EPP 已恢复为固件默认值");
        }
    }

    // ---------------- HWP 全字段（通用）/ 电压偏移（个性化可选） ----------------

    /// <summary>
    /// 通用 HWP 参数写入：Min Perf / Max Perf / EPP / Activity Window。
    /// -1 = 保持原值；Desired Perf 与 Package Control 永不改写（保护 HWP 自主选频）。
    /// 整字读-改-写 + 回读校验 + 失败回滚，与 0x610 同一套护栏。
    /// </summary>
    public MsrWriteResult ApplyHwp(int minPerf = -1, int maxPerf = -1,
        int epp = -1, int activityWindow = -1, int firmwareMaxPerf = 0)
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"当前通道不可写：{_channel.Detail}");
            if (!_eppSupported)
                return new MsrWriteResult(MsrWriteOutcome.Rejected, "0x774 不可读，HWP 参数微调不可用");

            var cur = ReadHwp();
            if (cur is null)
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed, "写入前读取 0x774 失败，已中止");

            int min = minPerf < 0 ? cur.Value.MinPerf : minPerf;
            int max = maxPerf < 0 ? cur.Value.MaxPerf : maxPerf;
            int ep = epp < 0 ? cur.Value.Epp : epp;
            int aw = activityWindow < 0 ? cur.Value.ActivityWindow : activityWindow;
            string? invalid = HwpRequest.Validate(min, max, ep, aw, firmwareMaxPerf);
            if (invalid is not null)
                return new MsrWriteResult(MsrWriteOutcome.Rejected, invalid);

            if (!_channel.TryRead(MsrHwpRequest, out ulong original))
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed, "写入前读取 0x774 失败，已中止");

            ulong target = HwpRequest.Encode(original, minPerf, maxPerf, epp, activityWindow);
            if (target == original)
                return new MsrWriteResult(MsrWriteOutcome.Success, "HWP 参数与当前实读一致，无需写入");

            if (!_channel.TryWrite(MsrHwpRequest, target, out int hrWrite))
            {
                Audit("write-hwp", original, target, $"failed:hr=0x{hrWrite:X8}");
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    $"驱动拒绝写入 0x774（HRESULT 0x{hrWrite:X8}），常见于 HWP 被 BIOS 锁定");
            }

            if (!_channel.TryRead(MsrHwpRequest, out ulong readback) || readback != target)
            {
                _channel.TryWrite(MsrHwpRequest, original);
                Audit("write-hwp", original, target, "verify-failed:rolled-back");
                return new MsrWriteResult(MsrWriteOutcome.VerifyFailed,
                    "HWP 回读不一致，已自动回滚（可能被 Windows 电源计划 / Intel DTT 覆盖）");
            }

            Audit("write-hwp", original, target, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Success,
                $"HWP 已写入并回读校验通过（{HwpRequest.Decode(readback).Describe}）");
        }
    }

    /// <summary>
    /// 个性化可选微调：Core/Cache 电压偏移（0x150 OC Mailbox）。
    /// 只允许降压 [-150, 0] mV；两种写使能编码都试；任一失败都强制回零。
    /// 注意：0x150 是命令/响应接口，回读判据是高位匹配，不是「电压值相等」。
    /// </summary>
    public MsrWriteResult ApplyVoltage(int offsetMv)
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"当前通道不可写：{_channel.Detail}");

            string? invalid = FivrVoltage.Validate(offsetMv);
            if (invalid is not null)
                return new MsrWriteResult(MsrWriteOutcome.Rejected, invalid);

            _channel.TryRead(MsrVoltage, out ulong before);   // 仅作审计基线，不是电压真值

            for (int variant = 0; variant < FivrVoltage.EnableVariants; variant++)
            {
                ulong cmd = FivrVoltage.Encode(offsetMv, variant);
                if (!_channel.TryWrite(MsrVoltage, cmd, out _)) continue;
                if (_channel.TryRead(MsrVoltage, out ulong readback)
                    && FivrVoltage.ResponseMatches(cmd, readback))
                {
                    Audit("write-voltage", before, readback, $"ok:variant={variant}:{offsetMv}mV");
                    return new MsrWriteResult(MsrWriteOutcome.Success,
                        $"电压偏移 {offsetMv} mV 已下发并读回校验通过（0x{readback:X16}）；重启即失效");
                }
            }

            // 任何失败都不留半写入状态：强制回零
            _channel.TryWrite(MsrVoltage, FivrVoltage.Zero);
            Audit("write-voltage", before, 0, "failed:zeroed");
            return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                "电压偏移写入被 BIOS / Plundervolt 修复微码拒绝，或读回不匹配；已强制回零。" +
                "该 CPU 不支持此个性化项，通用参数不受影响。");
        }
    }

    /// <summary>退出 / 恢复原值时把电压偏移回零（写入易失寄存器，不改固件）。</summary>
    public MsrWriteResult RestoreVoltage()
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, "通道不可写，电压偏移将在重启后自动失效");

            _channel.TryRead(MsrVoltage, out ulong before);
            bool ok = true;
            for (int i = 0; i < 2; i++)
                ok &= _channel.TryWrite(MsrVoltage, FivrVoltage.Zero, out _);

            Audit("restore-voltage", before, 0, ok ? "ok" : "failed");
            return ok
                ? new MsrWriteResult(MsrWriteOutcome.Restored, "电压偏移已回零")
                : new MsrWriteResult(MsrWriteOutcome.RestoreFailed,
                    "回零写入失败；重启机器即可由固件恢复默认（MSR 易失）");
        }
    }

    // ---------------- 睿频倍率（MSR 0x1AD） ----------------

    /// <summary>读取并解码当前睿频倍率表；读不到返回 null。</summary>
    public TurboRatioState? ReadTurbo() =>
        _channel.TryRead(MsrTurbo, out ulong raw) && raw != 0
            ? TurboRatioLimit.Decode(raw)
            : null;

    /// <summary>
    /// 把各核睿频倍率上限设为 ratio（×100MHz）。
    /// 只允许落在 [MinRatio, 出厂单核上限] 内 —— 这是 Intel 允许的睿频表配置范围，
    /// 不做超出出厂规格的超频。写后严格逐档回读校验，不一致立即回滚。
    /// </summary>
    public MsrWriteResult ApplyTurbo(int ratio)
    {
        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"当前通道不可写：{_channel.Detail}");
            if (_startupTurboRaw is not ulong startupRaw)
                return new MsrWriteResult(MsrWriteOutcome.Rejected,
                    "本机读不到 0x1AD 睿频倍率表，无法调节（PL1/PL2/Tau 不受影响）");

            var startup = TurboRatioLimit.Decode(startupRaw);
            if (ratio < TurboRatioLimit.MinRatio || ratio > startup.MaxTier)
                return new MsrWriteResult(MsrWriteOutcome.Rejected,
                    $"睿频倍率必须在 [{TurboRatioLimit.MinRatio}, {startup.MaxTier}] 之间" +
                    $"（出厂单核上限 {startup.MaxGhz:F1}GHz）；超出属于超频，产品不执行");

            if (!_channel.TryRead(MsrTurbo, out ulong current) || current == 0)
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    "写入前读取 0x1AD 失败，已中止");

            ulong target = TurboRatioLimit.SetAll(current, ratio);
            if (target == current)
                return new MsrWriteResult(MsrWriteOutcome.Success,
                    $"所有档位已是 {ratio}x（{ratio / 10.0:F1}GHz），无需写入");

            // G-04：写前落 pending，异常退出后下次启动自动恢复出厂倍率表
            try { WriteRawFile(_turboPendingPath, startupRaw, "pending-turbo-restore", msr: MsrTurbo); }
            catch { /* 目录不可写时仍继续，靠内存备份与退出恢复兜底 */ }

            if (!_channel.TryWrite(MsrTurbo, target, out int hrWrite))
            {
                ClearTurboPending();
                Audit("write-turbo", current, target, $"failed:hr=0x{hrWrite:X8}");
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    $"驱动拒绝写入 0x1AD（HRESULT 0x{hrWrite:X8}）：常见原因是 BIOS 锁定倍频表 / HVCI 拦截内核驱动");
            }

            if (!_channel.TryRead(MsrTurbo, out ulong readback) || readback != target)
            {
                bool rolled = _channel.TryWrite(MsrTurbo, current);
                ClearTurboPending();
                Audit("write-turbo", current, target, rolled ? "verify-failed:rolled-back" : "verify-failed:rollback-failed");
                return new MsrWriteResult(MsrWriteOutcome.VerifyFailed,
                    rolled
                        ? "睿频倍率回读与目标不一致，已自动回滚（可能被固件周期性重置）"
                        : "回读不一致且回滚失败，请重启机器恢复出厂倍率表");
            }

            ClearTurboPending();
            Audit("write-turbo", current, target, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Success,
                $"睿频倍率已设为 {ratio}x（{ratio / 10.0:F1}GHz）：{TurboRatioLimit.Describe(readback)}");
        }
    }

    /// <summary>恢复睿频倍率表到启动时的固件默认值。</summary>
    public MsrWriteResult RestoreTurbo()
    {
        lock (_gate)
        {
            if (_startupTurboRaw is not ulong raw)
                return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "没有可用的睿频倍率启动备份");
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, $"通道不可写：{_channel.Detail}");
            if (!_channel.TryRead(MsrTurbo, out ulong current))
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "读取当前 0x1AD 失败");

            if (current == raw)
                return new MsrWriteResult(MsrWriteOutcome.Restored, "睿频倍率表已是固件默认值");

            if (!_channel.TryWrite(MsrTurbo, raw))
            {
                Audit("restore-turbo", current, raw, "failed:driver-rejected");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "驱动拒绝恢复睿频倍率表");
            }
            if (!_channel.TryRead(MsrTurbo, out ulong verify) || verify != raw)
            {
                Audit("restore-turbo", current, raw, "failed:verify");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "睿频倍率表恢复后回读不一致");
            }

            ClearTurboPending();
            Audit("restore-turbo", current, raw, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Restored,
                $"睿频倍率表已恢复固件默认（{TurboRatioLimit.Describe(raw)}）");
        }
    }

    /// <summary>上次异常退出残留的 turbo pending：启动时自动恢复出厂倍率表。</summary>
    public MsrWriteResult RestorePendingTurboIfAny()
    {
        if (!File.Exists(_turboPendingPath))
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "无待恢复记录");

        var raw = ReadRaw(_turboPendingPath);
        if (raw is null)
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "睿频待恢复文件损坏");

        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, "通道不可写，暂不能自动恢复睿频倍率表");
            if (!_channel.TryWrite(MsrTurbo, raw.Value))
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "自动恢复睿频倍率表写入失败");
            ClearTurboPending();
            Audit("auto-restore-turbo", 0, raw.Value, "ok:previous-crash");
            return new MsrWriteResult(MsrWriteOutcome.Restored,
                "检测到上次异常退出，睿频倍率表已自动恢复固件默认");
        }
    }

    public MsrWriteResult Restore()
    {
        lock (_gate)
        {
            if (_startupRaw is not ulong raw)
                return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "没有可用的启动备份");

            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable,
                    $"当前通道不可写，无法恢复：{_channel.Detail}");

            if (!_channel.TryRead(Msr, out ulong current))
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "读取当前值失败");

            if (!_channel.TryWrite(Msr, raw))
            {
                Audit("restore", current, raw, "failed:driver-rejected");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "驱动拒绝恢复写入");
            }

            if (!_channel.TryRead(Msr, out ulong verify) || !SameLimits(verify, raw))
            {
                Audit("restore", current, raw, "failed:verify");
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed,
                    "恢复后回读不一致（固件可能接管功耗）", RaplPowerLimit.Decode(current));
            }

            ClearPending();
            Audit("restore", current, raw, "ok");
            return new MsrWriteResult(MsrWriteOutcome.Restored,
                "已恢复为启动时的原值", RaplPowerLimit.Decode(current), RaplPowerLimit.Decode(verify));
        }
    }

    /// <summary>上次异常退出残留的 pending：启动时自动恢复。</summary>
    public MsrWriteResult RestorePendingIfAny()
    {
        if (!File.Exists(_pendingPath))
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "无待恢复记录");

        var raw = ReadRaw(_pendingPath);
        if (raw is null)
            return new MsrWriteResult(MsrWriteOutcome.RestoreUnavailable, "待恢复文件损坏");

        lock (_gate)
        {
            if (!_channel.CanWrite)
                return new MsrWriteResult(MsrWriteOutcome.NotWritable, "通道不可写，暂不能自动恢复");

            if (!_channel.TryWrite(Msr, raw.Value))
                return new MsrWriteResult(MsrWriteOutcome.RestoreFailed, "自动恢复写入失败");

            ClearPending();
            Audit("auto-restore", 0, raw.Value, "ok:previous-crash");
            return new MsrWriteResult(MsrWriteOutcome.Restored, "检测到上次异常退出，已自动恢复功耗参数");
        }
    }

    // ---------------- 文件与日志 ----------------

    private void WritePending(ulong original, ulong target) =>
        WriteRawFile(_pendingPath, original, "pending-restore", target);

    private void ClearPending()
    {
        try { if (File.Exists(_pendingPath)) File.Delete(_pendingPath); } catch { /* 忽略 */ }
    }

    private void ClearTurboPending()
    {
        try { if (File.Exists(_turboPendingPath)) File.Delete(_turboPendingPath); } catch { /* 忽略 */ }
    }

    private static void WriteRawFile(string path, ulong raw, string kind,
        ulong? target = null, uint msr = RaplPowerLimit.MsrPkgPowerLimit)
    {
        var payload = new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["timestamp"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            ["msr"] = $"0x{msr:X}",
            ["original"] = raw.ToString(CultureInfo.InvariantCulture),
            ["target"] = (target ?? 0).ToString(CultureInfo.InvariantCulture)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ulong? ReadRaw(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("original", out var el) &&
                ulong.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong raw))
                return raw;
        }
        catch { /* 损坏视为不可用 */ }
        return null;
    }

    private void Audit(string action, ulong before, ulong after, string result)
    {
        try
        {
            var line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss} | {1,-11} | before=0x{2:X16} | after=0x{3:X16} | {4}{5}",
                DateTime.Now, action, before, after, result, System.Environment.NewLine);
            File.AppendAllText(_auditPath, line, Encoding.UTF8);
        }
        catch { /* 审计失败不影响主流程 */ }
    }

    /// <summary>只比较 PL1/PL2/Tau 三个字段，忽略固件可能回写的其它位。</summary>
    private static bool SameLimits(ulong a, ulong b)
    {
        var da = RaplPowerLimit.Decode(a);
        var db = RaplPowerLimit.Decode(b);
        return Math.Abs(da.Pl1W - db.Pl1W) < 0.2
               && Math.Abs(da.Pl2W - db.Pl2W) < 0.2
               && Math.Abs(da.TauS - db.TauS) < 0.5;
    }

    public void Dispose() => _channel.Dispose();
}
