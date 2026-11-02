using ThermalPilot.Core.Taxonomy;

namespace ThermalPilot.Core.DecisionTree;

/// <summary>
/// 采样拍的不可变快照（Immutable Telemetry Snapshot）
/// 避免在长链路决策过程中重复读取或并发访问易失硬件寄存器。
/// </summary>
public record ThermalSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double CurrentTempC { get; init; }
    public double AmbientTempC { get; init; }
    public double PackagePowerW { get; init; }
    public double CurrentPL1W { get; init; }
    public double CurrentFanPercent { get; init; }
    public double LoadPercent { get; init; }
    public double DtDt { get; init; } // 温度变化率 (°C/s)
    public bool IsProchotActive { get; init; }
    public double CoreClockGhz { get; init; }
    public double MaxTurboGhz { get; init; }
    public IdentifiedThermalState ThermalModel { get; init; } = new();

    public BindingLimit DetectActiveLimit(double targetCeiling)
    {
        if (IsProchotActive || CurrentTempC >= targetCeiling)
            return BindingLimit.ThermalLimit;
        if (LoadPercent < 20.0 || PackagePowerW < 12.0)
            return BindingLimit.LightLoad;
        if (MaxTurboGhz > 0 && CoreClockGhz >= MaxTurboGhz * 0.98)
            return BindingLimit.FrequencyCap;
        return BindingLimit.PowerWall;
    }
}
