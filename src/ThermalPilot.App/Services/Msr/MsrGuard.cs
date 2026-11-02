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

    private readonly MsrChannel _channel;
    private readonly string _backupDir;
    private readonly string _backupPath;
    private readonly string _pendingPath;
    private readonly string _auditPath;
    private readonly object _gate = new();

    private ulong? _startupRaw;

    public MsrGuard(MsrChannel channel, string dataDir)
    {
        _channel = channel;
        _backupDir = Path.Combine(dataDir, "backup");
        _backupPath = Path.Combine(_backupDir, "msr-startup.json");
        _pendingPath = Path.Combine(_backupDir, "pending-restore.json");
        _auditPath = Path.Combine(dataDir, "logs", "msr-audit.log");

        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);

        EnsureStartupBackup();
        PruneOldBackups();
    }

    public MsrChannel Channel => _channel;
    public bool CanWrite => _channel.CanWrite;
    public bool HasPendingRestore => File.Exists(_pendingPath);

    public RaplLimits? StartupBackup
    {
        get
        {
            if (_startupRaw is ulong raw) return RaplPowerLimit.Decode(raw);
            return null;
        }
    }

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

    /// <summary>保留最近 20 份备份（PRD §10）。</summary>
    private void PruneOldBackups()
    {
        try
        {
            var files = Directory.GetFiles(_backupDir, "msr-*.json")
                .Where(f => !f.EndsWith("msr-startup.json", StringComparison.OrdinalIgnoreCase))
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
                // 0x80070002：签名模块不提供该 MSR 的写能力 → 永久降级为只读
                if (hrWrite == unchecked((int)0x80070002))
                    _channel.MarkNotWritable(
                        "签名模块不支持写入 0x610（读可用、写不可用）。本机降级为只读模式。");
                Audit("write", original, target, $"failed:hr=0x{hrWrite:X8}");
                return new MsrWriteResult(MsrWriteOutcome.WriteFailed,
                    $"驱动拒绝写入（HRESULT 0x{hrWrite:X8}，返回条目 {_channel.LastWriteReturned}）：" +
                    "常见原因是被 BIOS/HVCI 拦截，或该 MSR 在模块白名单之外", before);
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

    private static void WriteRawFile(string path, ulong raw, string kind, ulong? target = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["timestamp"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            ["msr"] = $"0x{RaplPowerLimit.MsrPkgPowerLimit:X}",
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
