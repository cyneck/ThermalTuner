namespace ThermalTuner.Core;

/// <summary>
/// 自动 CPU 控制决策的兼容性 DTO（保留历史字段，供旧调用方无缝迁移）。
/// </summary>
public class AutoCpuDecision
{
    public bool Valid { get; set; }
    public double Pl1W { get; set; }
    public double Pl2W { get; set; }
    public double TauS { get; set; }
    public int Epp { get; set; }
    public double Reff { get; set; }
    /// <summary>标定热阻反解出的 PL1 安全上限（NaN = 未标定或未设基线）。决策输出会被钳到此值。</summary>
    public double ThermalCapW { get; set; } = double.NaN;
    public bool RConfident { get; set; }
    public string RQuality { get; set; } = "Low";
    public string Reason { get; set; } = string.Empty;
    /// <summary>轨迹整形层的本拍说明（降档直通 / 同带拦截 / 跨带放行 / 安全直通）。</summary>
    public string ShapingReason { get; set; } = string.Empty;
    /// <summary>本拍升档是否被温度带门控暂缓（界面可显示「等温度跨带」）。</summary>
    public bool GateHeld { get; set; }
    /// <summary>整形后的滤波温度（°C）；NaN = 无有效读数。</summary>
    public double FilteredTempC { get; set; } = double.NaN;
    public BindingLimit Limit { get; set; }
    public int Direction { get; set; } // -1 = 降能, 0 = 保持, +1 = 提速
}

/// <summary>
/// 每拍的性能/硬件状态采样（时钟、PROCHOT 等）。
/// </summary>
public class PerfSample
{
    public double CoreClocksGhz { get; set; }
    public double MaxTurboGhz { get; set; }
    public bool Prochot { get; set; }
}
