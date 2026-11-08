namespace ThermalTuner.App.Services;

/// <summary>
/// 产品语言层：界面只说「这是什么、我该做什么」，工程语言（寄存器地址、驱动名、接口名）
/// 只允许出现在「诊断与日志 → 技术详情」与代码注释里。
///
/// 为什么单独一层：同一件事在两种语境下有不同叫法，混写会让普通用户看不懂，
/// 也会让文案随实现一起漂移。新增界面文案时先查本表，不要就地写死术语。
/// </summary>
public static class ProductTerms
{
    // ---------------- 参数（用户视角） ----------------
    public const string LongPowerLimit = "持续功耗上限";
    public const string ShortPowerLimit = "短时功耗上限";
    public const string BurstDuration = "短时功耗时长";
    public const string EnergyPreference = "能效偏好";
    public const string MinPerf = "最低性能档";
    public const string MaxPerf = "最高性能档";
    public const string ResponseWindow = "调频响应窗口";
    public const string TurboLimit = "睿频上限";
    public const string VoltageOffset = "电压偏移";

    // ---------------- 通道与组件 ----------------
    public const string TuningChannel = "调优通道";
    public const string TuningDriver = "调优组件";
    public const string SensorEngine = "传感器组件";
    public const string PowerMeter = "功耗计量";
    public const string WriteAuthorization = "写入授权";

    /// <summary>
    /// 技术详情前缀。工具提示里允许出现寄存器地址，但必须挂在用户能看懂的说明之后，
    /// 并有明确前缀，避免和产品文案混成一句。
    /// </summary>
    public const string TechPrefix = "技术详情：";

    public static string Tech(string detail) => TechPrefix + detail;

    /// <summary>参数名 → 层（智能可推荐 / 仅手动），供界面分组标题使用。</summary>
    public static string LayerName(bool userOnly) =>
        userOnly ? "个性化微调 · 仅手动设置，重新检测不会覆盖" : "通用参数 · 自动检测可给建议，也可手动设置";
}
