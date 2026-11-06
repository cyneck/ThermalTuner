namespace ThermalTuner.Monitor;

/// <summary>
/// 合成热模型（无温度传感器时的闭环实测）：
///   一阶热容模型  C·dT/dt = P - (T - T_amb) / R_th
/// 功耗由【真实 CPU 负载】×【控制器当前 PL1】折算，因此降功耗决策会真实反映到温度上，形成闭环。
/// 仅用于验证控制逻辑，不代表真实芯片温度。
/// </summary>
public sealed class SyntheticPlant
{
    private const double AmbientC = 30.0;
    private const double RthKW = 0.90;   // 笔记本整机热阻量级
    private const double CthJK = 45.0;   // 散热系统热容量级（约 40s 时间常数，便于现场观察）

    private double _tempC;

    public SyntheticPlant(double startTempC = 45.0) => _tempC = startTempC;

    /// <summary>推进一步：输入真实负载与控制器当前 PL1，返回（温度, 功耗）。</summary>
    public (double TempC, double PowerW) Step(double loadPercent, double appliedPl1W, double dtSeconds)
    {
        double load = double.IsNaN(loadPercent) ? 30.0 : Math.Clamp(loadPercent, 0, 100);
        double pl1 = Math.Clamp(appliedPl1W, 5, 250);
        double power = 5.0 + load / 100.0 * (pl1 * 0.95 - 5.0);  // 负载决定的实际功耗

        double heatOut = (_tempC - AmbientC) / RthKW;
        _tempC += (power - heatOut) * dtSeconds / CthJK;
        _tempC = Math.Clamp(_tempC, AmbientC, 110.0);
        return (_tempC, power);
    }
}
