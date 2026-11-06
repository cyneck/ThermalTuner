using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;
using ThermalTuner.App.Services;

namespace ThermalTuner.App;

/// <summary>
/// 只读传感器清单探针：枚举 LHM 能看到的全部硬件与传感器，
/// 用于在接入 GPU / 风扇指标前确认本机到底暴露了哪些读数（不写任何寄存器）。
/// 用法：ThermalTuner.App.exe --sensor-probe --out sensors.txt（建议管理员）。
/// </summary>
internal static class SensorProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== LHM 全硬件传感器清单（只读）==");

        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = true,
                IsControllerEnabled = true,
                IsBatteryEnabled = true
            };
            computer.Open();
            try
            {
                int hardware = 0, sensors = 0;
                foreach (var hw in computer.Hardware)
                {
                    hardware++;
                    hw.Update();
                    sb.AppendLine();
                    sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                    foreach (var s in hw.Sensors)
                    {
                        sensors++;
                        sb.AppendLine($"  {s.SensorType,-14} | {s.Name,-34} | " +
                                      $"{Format(s.Value)} | {s.Identifier}");
                    }
                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                        sb.AppendLine($"  └─ 子硬件 [{sub.HardwareType}] {sub.Name}");
                        foreach (var s in sub.Sensors)
                        {
                            sensors++;
                            sb.AppendLine($"     {s.SensorType,-14} | {s.Name,-34} | " +
                                          $"{Format(s.Value)} | {s.Identifier}");
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"硬件 {hardware} 个 · 传感器 {sensors} 个");
                sb.AppendLine("RESULT: 传感器枚举完成");
            }
            finally
            {
                try { computer.Close(); } catch { /* 忽略 */ }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"枚举失败：{ex.GetType().Name}: {ex.Message}");
            sb.AppendLine("RESULT: 失败");
            Write(outPath, sb);
            return 1;
        }

        // 聚合指标：走真实遥测链路，确认 UI 拿到的不是空值
        try
        {
            using var telemetry = new HardwareTelemetry();
            var s = telemetry.Read();
            sb.AppendLine();
            sb.AppendLine("== 聚合指标（HardwareTelemetry.Read 一拍）==");
            sb.AppendLine($"CPU 使用率     : {Pct(s.LoadPercent)}");
            sb.AppendLine($"GPU            : {(string.IsNullOrEmpty(s.GpuName) ? "未检测到" : s.GpuName)} / {s.GpuSource}");
            sb.AppendLine($"GPU 使用率     : {Pct(s.GpuLoadPercent)}");
            sb.AppendLine($"GPU 显存占用率 : {Pct(s.GpuMemoryPercent)}");
            sb.AppendLine($"GPU 显存用量   : " +
                          $"{(double.IsNaN(s.GpuMemoryUsedMb) ? "N/A" : $"{s.GpuMemoryUsedMb:F0} / {s.GpuMemoryTotalMb:F0} MB")}");
            sb.AppendLine($"风扇 RPM       : {(double.IsNaN(s.FanRpm) ? "N/A" : $"{s.FanRpm:F0}")}");
            sb.AppendLine($"风扇运行 %     : {Pct(s.FanControlPercent)}");
            sb.AppendLine($"风扇来源       : {(string.IsNullOrEmpty(s.FanSource) ? "不可读" : s.FanSource)}" +
                          (double.IsNaN(s.FanRawValue) ? "" : $" · EC 原始值 {s.FanRawValue:F0}"));
            sb.AppendLine($"功耗来源       : {s.Source} · {s.PowerW:F1} W");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"聚合采样失败：{ex.GetType().Name}: {ex.Message}");
        }

        Write(outPath, sb);
        return 0;
    }

    private static string Pct(double v) => double.IsNaN(v) ? "N/A" : $"{v:F1}%";

    private static string Format(float? v) => v is { } x ? x.ToString("F1") : "null";

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 写盘失败靠退出码体现 */ }
    }
}
