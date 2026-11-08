using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.App.Services;

/// <summary>
/// 风扇数据源装配层：把 App 层现有的三条读取路径包装成 <see cref="IFanSource"/>，
/// 交给 Core 的 <see cref="FanSourceResolver"/> 按统一优先级编排。
///
/// 优先级（与迁移前行为一致）：
///   ① LHM Fan/Control 传感器 → ② 内置 EC 端口直读 → ③ NBFC 服务 → ④ Unavailable。
/// 其中 ②③ 由 <c>EcFanReader.ReadRuntime()</c> 内部完成二级回退与 2.2s 缓存；
/// 这里保持该聚合不变，避免改动缓存与恒定检测的既有语义。
///
/// 全体通道均为只读：不写 EC 寄存器内容，不写风扇控制寄存器 148。
/// </summary>
internal static class FanSources
{
    /// <summary>
    /// 按当前进程能力组装并读取一次风扇数据。
    /// <paramref name="lhmReader"/> 为 null 时表示 LHM 未初始化（例如纯命令行模式）。
    /// </summary>
    public static FanSample Read(Func<(double Rpm, double Pct)>? lhmReader)
    {
        var sources = new List<IFanSource>(2);
        if (lhmReader is not null) sources.Add(new LhmFanSource(lhmReader));
        sources.Add(new EcChainFanSource());
        // OemAcpi 尚未实现：厂商 WMI / ACPI 方法必须先做机型白名单与只读实测，
        // 不得仅凭「通用工具应该有」就接入未经验证的写入路径。
        return FanSourceResolver.Resolve(sources);
    }
}

/// <summary>
/// ① LHM 传感器通道。Fan 传感器是 RPM，Control 传感器是百分比，两者量纲不同，
/// 命中时动态改写 <see cref="FanMeasureKind"/>，避免把百分比当转速展示。
/// </summary>
internal sealed class LhmFanSource : IFanSource
{
    private readonly Func<(double Rpm, double Pct)> _read;

    public LhmFanSource(Func<(double Rpm, double Pct)> read) => _read = read;

    public FanCapability Capability => FanCapability.Of(
        FanTransportKind.LhmSensor, FanMeasureKind.Rpm, "LHM 实测 · 只读",
        "SensorType.Fan(RPM) / Control(%)；需主板暴露传感器，多数笔记本 EC 不暴露");

    public FanSample Read()
    {
        var (rpm, pct) = _read();
        if (!double.IsNaN(rpm))
            return new FanSample(Capability, rpm, double.NaN, "Fan 传感器实测转速");
        if (!double.IsNaN(pct))
            return new FanSample(
                Capability with { Measure = FanMeasureKind.DutyPercent },
                pct, double.NaN, "Control 传感器实测占空比（非 RPM）");
        return FanSample.Missing(Capability, "本机主板未暴露 Fan / Control 传感器");
    }
}

/// <summary>
/// ②③ EC 聚合通道：包装既有 <c>EcFanReader.ReadRuntime()</c>。
/// 保留其内部「内置 EC 直读 → NBFC ec-probe → NBFC status」的二级回退与恒定检测，
/// 只把结果升格为带量纲与通道类型的 <see cref="FanSample"/>。
/// </summary>
internal sealed class EcChainFanSource : IFanSource
{
    public FanCapability Capability => FanCapability.Of(
        FanTransportKind.EcPortIo, FanMeasureKind.DutyPercent, "EC 只读链",
        "ACPI EC 0x62/0x66 读握手；档位刻度非 RPM。NBFC 卸载后仍可用");

    public FanSample Read()
    {
        FanReading r = EcFanReader.ReadRuntime();
        if (double.IsNaN(r.Percent))
            return FanSample.Missing(Capability, r.Detail);

        FanTransportKind transport = r.Source.Contains("NBFC", StringComparison.OrdinalIgnoreCase)
            ? FanTransportKind.NbfcService
            : FanTransportKind.EcPortIo;

        return new FanSample(
            Capability with { Transport = transport, Name = r.Source },
            r.Percent, r.RawValue, r.Detail, r.StagnantReads, r.Stagnant);
    }
}
