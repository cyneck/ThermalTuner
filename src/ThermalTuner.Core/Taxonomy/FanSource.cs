namespace ThermalTuner.Core.Taxonomy;

/// <summary>
/// 风扇数据的**传输通道**类型。通用工具不能假定所有笔记本都走同一条路：
/// 台式机与部分 OEM 笔记本走 SuperIO/LHM 传感器，绝大多数笔记本走 ACPI EC，
/// 还有厂商私有 WMI / ACPI 方法。这里只描述通道，不绑定任何驱动实现。
/// </summary>
public enum FanTransportKind
{
    /// <summary>LHM SensorType.Fan(RPM) / Control(%)，依赖主板暴露传感器。</summary>
    LhmSensor,
    /// <summary>ACPI EC 端口 0x62(EC_DATA) / 0x66(EC_SC) 直读，NBFC 同款机制。</summary>
    EcPortIo,
    /// <summary>NBFC 服务 / ec-probe 输出（独立进程，按机型 XML 换算）。</summary>
    NbfcService,
    /// <summary>厂商 WMI / ACPI 方法（Lenovo / Dell / HP OMEN Hub 等）。</summary>
    OemAcpi,
    /// <summary>无可用通道。</summary>
    Unavailable
}

/// <summary>
/// 读数的**物理量纲**。区分量纲是本项目的一条硬口径：
/// 档位百分比不等于 RPM，EC 原始字节在语义未确认时也不等于档位百分比。
/// </summary>
public enum FanMeasureKind
{
    /// <summary>读不到任何值。</summary>
    None,
    /// <summary>真实转速（tachometer，单位 RPM）。</summary>
    Rpm,
    /// <summary>档位 / 占空比百分比（由原始字节按机型刻度换算）。</summary>
    DutyPercent,
    /// <summary>只有 EC 原始字节，语义未确认，禁止换算成百分比展示。</summary>
    RawByte
}

/// <summary>访问能力。写操作必须与机型配置绑定，不能由通道单独决定。</summary>
public enum FanAccessMode
{
    /// <summary>只读。当前所有已实现通道都处于此模式。</summary>
    ReadOnly,
    /// <summary>仅在机型配置、复位值、备份、确认全部齐备时才允许写入。</summary>
    WritableWithProfile
}

/// <summary>
/// 一个风扇数据源声明的能力。这是「通用工具」与「本机事实」之间的边界：
/// 能力描述可移植，具体能不能读到由 <see cref="IFanSource.Read"/> 在运行时回答。
/// </summary>
public sealed record FanCapability(
    FanTransportKind Transport,
    FanMeasureKind Measure,
    FanAccessMode Access,
    string Name,
    string Detail = "")
{
    public bool CanRead => Measure != FanMeasureKind.None;
    public bool IsRpm => Measure == FanMeasureKind.Rpm;
    public bool CanWrite => Access == FanAccessMode.WritableWithProfile;

    /// <summary>构造只读能力。写能力必须显式声明机型配置，不能默认打开。</summary>
    public static FanCapability Of(
        FanTransportKind transport, FanMeasureKind measure, string name, string detail = "") =>
        new(transport, measure, FanAccessMode.ReadOnly, name, detail);

    public static FanCapability Unavailable(string detail) => new(
        FanTransportKind.Unavailable, FanMeasureKind.None,
        FanAccessMode.ReadOnly, "无可用风扇数据源", detail);
}

/// <summary>
/// 一次风扇读数。<see cref="Value"/> 为 NaN 表示本机读不到，UI 必须显示 N/A，
/// 绝不能拿决策树的 TargetFanPercent 演算值填充。
/// </summary>
public sealed record FanSample(
    FanCapability Capability,
    double Value,
    double RawValue,
    string Reason,
    int StagnantReads = 0,
    bool Stagnant = false)
{
    public bool HasValue => !double.IsNaN(Value);

    public static FanSample Missing(FanCapability capability, string reason) =>
        new(capability, double.NaN, double.NaN, reason);

    public static FanSample Unavailable(string reason) =>
        Missing(FanCapability.Unavailable(reason), reason);
}

/// <summary>
/// 可插拔风扇数据源。实现方负责具体硬件访问；本层只约定能力与读数语义。
/// 写操作刻意不在接口里：EC 写入属于控制层，必须先过 <see cref="FanWriteGate"/>。
/// </summary>
public interface IFanSource
{
    FanCapability Capability { get; }
    FanSample Read();
}

/// <summary>
/// 风扇数据源优先级解析（纯逻辑，不访问硬件）。
/// 按传入顺序取第一个有效读数；单个源抛异常不得中断后续源；
/// 全部失败时返回带完整原因的 Unavailable，而不是伪造一个 0 或 100。
/// </summary>
public static class FanSourceResolver
{
    public static FanSample Resolve(IReadOnlyList<IFanSource> sources)
    {
        if (sources is null || sources.Count == 0)
            return FanSample.Unavailable("未配置任何风扇数据源");

        var reasons = new List<string>(sources.Count);
        foreach (IFanSource? candidate in sources)
        {
            if (candidate is null) continue;
            IFanSource source = candidate;
            try
            {
                string name = source.Capability?.Name ?? "未知源";
                FanSample sample = source.Read();
                if (sample.HasValue) return sample;
                reasons.Add(string.IsNullOrWhiteSpace(sample.Reason)
                    ? $"{name}：无读数"
                    : $"{name}：{sample.Reason}");
            }
            catch (Exception ex)
            {
                reasons.Add($"{source.Capability?.Name ?? "未知源"}：读取异常 {ex.GetType().Name}");
            }
        }

        return FanSample.Unavailable(reasons.Count == 0
            ? "所有风扇数据源均不可用"
            : string.Join("；", reasons));
    }
}

/// <summary>写闸门判定结果。只有 Allowed 才允许下发 EC 写。</summary>
public enum FanWriteGate
{
    /// <summary>通道声明只读，或触顶硬闸生效——不可由二次确认解除。</summary>
    Disabled,
    NeedProfileVerification,
    NeedResetValue,
    NeedBackup,
    NeedConfirmation,
    Allowed
}

/// <summary>写操作前置条件。全部由调用方显式提供，不在本层隐式推断。</summary>
public readonly record struct FanWriteContext(
    bool ProfileVerified,
    bool ResetValueKnown,
    bool BackupTaken,
    bool UserConfirmed,
    bool UnderThermalCeiling);

/// <summary>
/// EC 风扇写的安全策略（纯判定，不执行任何写）。
///
/// 与 MSR 写入采用同一套纪律：备份 → 校验 → 回读 → 失败回滚 → 退出恢复；
/// 额外增加两条笔记本特有约束：机型配置必须验证（寄存器号 / 刻度 / 复位值因机型而异），
/// 以及 PROCHOT / 超过安全天花板时「只降不升」的硬闸**不可**由二次确认解除。
///
/// 当前产品所有通道均声明 <see cref="FanAccessMode.ReadOnly"/>，因此恒返回 Disabled。
/// 该闸门的存在是为了让「通用工具」在将来接入 LpcACPIEC 等可写模块时，
/// 有一个不能绕过的结构位置，而不是散落在 UI 按钮里的 if 判断。
/// </summary>
public static class FanWritePolicy
{
    public static FanWriteGate Evaluate(FanCapability capability, FanWriteContext context)
    {
        if (capability is null || !capability.CanWrite) return FanWriteGate.Disabled;
        // 硬闸优先于一切确认：高温/触顶时禁止任何可能降低散热能力的写入。
        if (!context.UnderThermalCeiling) return FanWriteGate.Disabled;
        if (!context.ProfileVerified) return FanWriteGate.NeedProfileVerification;
        if (!context.ResetValueKnown) return FanWriteGate.NeedResetValue;
        if (!context.BackupTaken) return FanWriteGate.NeedBackup;
        if (!context.UserConfirmed) return FanWriteGate.NeedConfirmation;
        return FanWriteGate.Allowed;
    }
}
