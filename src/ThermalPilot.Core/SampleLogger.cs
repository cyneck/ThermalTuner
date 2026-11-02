using System.Globalization;
using System.Text;

namespace ThermalPilot.Core;

/// <summary>
/// 采样数据落盘（CSV），供离线分析与"为什么曲线这样生成"追溯。
/// 路径：%ProgramData%\ThermalPilot\logs\samples-yyyyMMdd-HHmmss.csv
/// </summary>
public sealed class SampleLogger : IDisposable
{
    private StreamWriter? _writer;
    private bool _headerWritten;

    public string FilePath { get; private set; } = "";
    public bool IsOpen => _writer != null;
    public int Rows { get; private set; }

    public void Open(string? file = null)
    {
        Close();
        Directory.CreateDirectory(Log.LogDirectory);
        FilePath = file ?? Path.Combine(Log.LogDirectory, $"samples-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(true)) { AutoFlush = false };
        _writer.WriteLine("Timestamp,ElapsedSec,Stage,CpuTempC,CpuLoadPct,CpuMHz,CpuPowerW,GpuTempC," +
                          "GpuLoadPct,GpuPowerW,FanRpmMax,FanOutPct,ProcMaxPct,FanCmdPct,FanChannels," +
                          "GpuHotSpotC,RamPct,CpuTempRawC");
        _headerWritten = true;
        Rows = 0;
        Log.Info("采样日志开始：" + FilePath);
    }

    public void Write(CalibrationSample s, DateTime start, string channels)
    {
        if (_writer == null || !_headerWritten) return;
        double elapsed = (s.Time - start).TotalSeconds;
        _writer.WriteLine(string.Join(',', new string[]
        {
            s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsed.ToString("F1", CultureInfo.InvariantCulture),
            Quote(s.Stage),
            F(s.CpuTemp), F(s.CpuLoad), F(s.CpuMhz), F(s.CpuPower),
            F(s.GpuTemp), F(s.GpuLoad), F(s.GpuPower),
            s.FanRpm.ToString(CultureInfo.InvariantCulture),
            F(s.FanOut),
            s.Cap.ToString(CultureInfo.InvariantCulture),
            F(s.FanCmd),
            Quote(channels),
            F(s.GpuHotSpot), F(s.RamPct),
            // 原始读数单独一列：CpuTempC 已是抗毛刺后的值，追溯"刚才那个尖是不是毛刺"要看这列。
            // 实验室标定路径（Calibration 直接构造 CalibrationSample）没有填 CpuTempRaw，回退到 CpuTemp。
            F(s.CpuTempRaw > 0 ? s.CpuTempRaw : s.CpuTemp)
        }));
        Rows++;
        if (Rows % 30 == 0) _writer.Flush();
    }

    /// <summary>引擎连续运行时的轻量记录（不需要 Stage）。</summary>
    public void WriteRuntime(SensorSnapshot snap, double fanOut, int procMax, DateTime start)
    {
        Write(new CalibrationSample
        {
            Time = snap.Timestamp,
            Stage = "runtime",
            CpuTemp = snap.CpuTempShown,     // 抗毛刺后
            CpuTempRaw = snap.CpuTemp,       // 原始读数
            GpuHotSpot = snap.GpuHotSpotTemp,
            RamPct = snap.RamPct,
            CpuLoad = snap.CpuLoad,
            CpuMhz = snap.CpuFrequencyMhz,
            CpuPower = snap.CpuPower,
            GpuTemp = snap.GpuTemp,
            GpuLoad = snap.GpuLoad,
            GpuPower = snap.GpuPower,
            FanRpm = snap.FanRpm,
            FanOut = fanOut,
            Cap = procMax,
            // FanCmd 用引擎下发的值，不再取 snap.FanSoftwarePct ——
            // 后者只在 LHM 有 PWM 传感器时才有，笔记本（NBFC 后端）恒为 -1。
            FanCmd = fanOut
        }, start, string.Join("|", snap.Fans.Select(f => $"{f.Name}:{f.Rpm}")));
    }

    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";

    public void Close()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { }
        _writer = null;
        if (Rows > 0) Log.Info($"采样日志结束：{Rows} 行 -> {FilePath}");
    }

    public void Dispose() => Close();
}
