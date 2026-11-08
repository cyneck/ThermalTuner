using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// 风扇数据源抽象（Core 层纯逻辑）测试：优先级、逐级降级、异常隔离、量纲口径、写闸门。
/// 全程不访问硬件，可在任意机型上重复运行。
/// </summary>
public sealed class FanSourceTests
{
    private sealed class Stub : IFanSource
    {
        private readonly Func<FanSample> _read;

        public Stub(FanCapability capability, Func<FanSample> read)
        {
            Capability = capability;
            _read = read;
        }

        public FanCapability Capability { get; }
        public FanSample Read() => _read();
    }

    private static FanCapability Rpm(string name) =>
        FanCapability.Of(FanTransportKind.LhmSensor, FanMeasureKind.Rpm, name);

    private static FanCapability Duty(string name) =>
        FanCapability.Of(FanTransportKind.EcPortIo, FanMeasureKind.DutyPercent, name);

    private static FanSample Hit(FanCapability cap, double value, double raw = double.NaN) =>
        new(cap, value, raw, "ok");

    public void RunAllTests()
    {
        Check.Suite("FanSource");
        PriorityOrder();
        FallbackWhenMissing();
        ExceptionIsolation();
        AllMissingIsUnavailable();
        EmptySources();
        MeasureKindSemantics();
        CapabilityWriteFlags();
        WriteGateReadonlyIsHardDisabled();
        WriteGatePrerequisiteOrder();
        WriteGateThermalCeilingIsHard();
    }

    private static void PriorityOrder()
    {
        var first = Rpm("LHM");
        var second = Duty("EC");
        var r = FanSourceResolver.Resolve(new IFanSource[]
        {
            new Stub(first, () => Hit(first, 3200)),
            new Stub(second, () => Hit(second, 88.83, 80))
        });

        Check.True(r.HasValue, "优先级：应有读数");
        Check.Near(3200, r.Value, 0.001, "优先级：取第一个有效源");
        Check.True(r.Capability.IsRpm, "优先级：RPM 量纲不得丢失");
        Check.True(r.Capability.Transport == FanTransportKind.LhmSensor, "优先级：通道保持 LHM");
        Check.IsNaN(r.RawValue, "优先级：LHM 路径无 EC 原始字节");
    }

    private static void FallbackWhenMissing()
    {
        var first = Rpm("LHM");
        var second = Duty("EC");
        var r = FanSourceResolver.Resolve(new IFanSource[]
        {
            new Stub(first, () => FanSample.Missing(first, "本机主板未暴露 Fan / Control 传感器")),
            new Stub(second, () => Hit(second, 88.83, 80))
        });

        Check.True(r.HasValue, "降级：第一源缺失时仍应有读数");
        Check.Near(88.83, r.Value, 0.001, "降级：取第二源档位百分比");
        Check.Near(80, r.RawValue, 0.001, "降级：EC 原始字节保留");
        Check.True(!r.Capability.IsRpm, "降级：档位百分比不得标成 RPM");
        Check.True(r.Capability.Transport == FanTransportKind.EcPortIo, "降级：通道为 EC 端口");
    }

    private static void ExceptionIsolation()
    {
        var broken = Rpm("异常源");
        var good = Duty("EC");
        var r = FanSourceResolver.Resolve(new IFanSource[]
        {
            new Stub(broken, () => throw new InvalidOperationException("driver gone")),
            new Stub(good, () => Hit(good, 42))
        });

        Check.True(r.HasValue, "异常隔离：单源抛异常不得中断后续源");
        Check.Near(42, r.Value, 0.001, "异常隔离：取到后续源读数");
    }

    private static void AllMissingIsUnavailable()
    {
        var a = Rpm("LHM");
        var b = Duty("EC");
        var r = FanSourceResolver.Resolve(new IFanSource[]
        {
            new Stub(a, () => FanSample.Missing(a, "无传感器")),
            new Stub(b, () => FanSample.Missing(b, "EC 驱动未加载"))
        });

        Check.True(!r.HasValue, "全失败：不得伪造读数");
        Check.IsNaN(r.Value, "全失败：Value 必须为 NaN（UI 显示 N/A）");
        Check.True(r.Capability.Transport == FanTransportKind.Unavailable, "全失败：通道 Unavailable");
        Check.Contains(r.Reason, "无传感器", "全失败：保留第一源原因");
        Check.Contains(r.Reason, "EC 驱动未加载", "全失败：保留第二源原因");
    }

    private static void EmptySources()
    {
        var empty = FanSourceResolver.Resolve(Array.Empty<IFanSource>());
        Check.True(!empty.HasValue, "空列表：返回不可用");
        Check.True(empty.Capability.Transport == FanTransportKind.Unavailable, "空列表：Unavailable");

        var allNull = FanSourceResolver.Resolve(new IFanSource[] { null!, null! });
        Check.True(!allNull.HasValue, "空引用源：跳过且返回不可用");

        var nullArg = FanSourceResolver.Resolve(null!);
        Check.True(!nullArg.HasValue, "null 入参：不得抛异常");
    }

    private static void MeasureKindSemantics()
    {
        var none = FanCapability.Unavailable("无");
        Check.True(!none.CanRead, "量纲：None 不可读");
        Check.True(!none.IsRpm, "量纲：None 非 RPM");
        Check.True(!none.CanWrite, "量纲：不可用通道不可写");

        var raw = FanCapability.Of(FanTransportKind.EcPortIo, FanMeasureKind.RawByte, "原始字节");
        Check.True(raw.CanRead, "量纲：RawByte 可读");
        Check.True(!raw.IsRpm, "量纲：RawByte 非 RPM");

        var duty = Duty("档位");
        Check.True(duty.CanRead, "量纲：DutyPercent 可读");
        Check.True(!duty.IsRpm, "量纲：DutyPercent 非 RPM");
    }

    private static void CapabilityWriteFlags()
    {
        var readOnly = Duty("只读");
        Check.True(!readOnly.CanWrite, "能力：Of() 默认只读");
        Check.True(readOnly.Access == FanAccessMode.ReadOnly, "能力：默认访问模式 ReadOnly");

        var writable = readOnly with { Access = FanAccessMode.WritableWithProfile };
        Check.True(writable.CanWrite, "能力：显式声明后才有写能力");
    }

    private static void WriteGateReadonlyIsHardDisabled()
    {
        var allReady = new FanWriteContext(true, true, true, true, true);
        var r = FanWritePolicy.Evaluate(Duty("只读"), allReady);
        Check.True(r == FanWriteGate.Disabled, "写闸门：只读通道即使全部条件齐备也禁止写入");

        var unavailable = FanWritePolicy.Evaluate(FanCapability.Unavailable("无"), allReady);
        Check.True(unavailable == FanWriteGate.Disabled, "写闸门：无通道禁止写入");

        var nullCap = FanWritePolicy.Evaluate(null!, allReady);
        Check.True(nullCap == FanWriteGate.Disabled, "写闸门：null 能力不得抛异常");
    }

    private static void WriteGatePrerequisiteOrder()
    {
        var cap = Duty("EC") with { Access = FanAccessMode.WritableWithProfile };

        var steps = new (FanWriteContext Context, FanWriteGate Expected)[]
        {
            (new FanWriteContext(false, false, false, false, true), FanWriteGate.NeedProfileVerification),
            (new FanWriteContext(true,  false, false, false, true), FanWriteGate.NeedResetValue),
            (new FanWriteContext(true,  true,  false, false, true), FanWriteGate.NeedBackup),
            (new FanWriteContext(true,  true,  true,  false, true), FanWriteGate.NeedConfirmation),
            (new FanWriteContext(true,  true,  true,  true,  true), FanWriteGate.Allowed)
        };

        int i = 0;
        foreach (var (ctx, expected) in steps)
        {
            var actual = FanWritePolicy.Evaluate(cap, ctx);
            Check.True(actual == expected, $"写闸门：前置条件第 {i + 1} 步应为 {expected}");
            i++;
        }
    }

    private static void WriteGateThermalCeilingIsHard()
    {
        var cap = Duty("EC") with { Access = FanAccessMode.WritableWithProfile };
        // 其余条件全部齐备，仅触顶：硬闸不可由二次确认解除。
        var overCeiling = new FanWriteContext(true, true, true, true, false);
        var r = FanWritePolicy.Evaluate(cap, overCeiling);
        Check.True(r == FanWriteGate.Disabled, "写闸门：触顶硬闸优先于二次确认，必须 Disabled");

        var underCeiling = overCeiling with { UnderThermalCeiling = true };
        var ok = FanWritePolicy.Evaluate(cap, underCeiling);
        Check.True(ok == FanWriteGate.Allowed, "写闸门：解除触顶后恢复 Allowed");
    }
}
