using System.IO;
using System.Text;
using ThermalPilot.App.Services;

namespace ThermalPilot.App;

/// <summary>
/// 标定流程自检（会真实拉满 CPU，1–2 分钟）：
/// 手动驱动 SampleOnce 推进两段式状态机，把每段进度与最终结果落盘。
/// 用法：ThermalPilot.App.exe --calibrate-test --out calib.txt
/// 说明：不带消息泵运行时 DispatcherTimer 不会自走，因此这里显式逐拍驱动。
/// </summary>
internal static class CalibrationProbe
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        using var session = new SessionState();

        session.SampleOnce();
        sb.AppendLine("== 前置 ==");
        sb.AppendLine($"温度        : {session.TempText}°C");
        sb.AppendLine($"功耗        : {session.PowerText}W（{session.PowerCaption}）");
        sb.AppendLine($"可否标定    : {session.CanCalibrate}（{session.CalibrationBlockedReason}）");

        if (!session.CanCalibrate)
        {
            Write(outPath, sb);
            return 4;
        }

        CalibrationOutcome? outcome = null;
        session.CalibrationFinished += o => outcome = o;

        int interval = Math.Max(250, session.Settings.IntervalMs);
        sb.AppendLine();
        sb.AppendLine("== 标定 ==");
        sb.AppendLine($"间隔        : {interval}ms");

        session.BeginCalibration();

        int step = 0;
        int maxTicks = 240_000 / interval;   // 4 分钟硬上限，防止挂死
        while (session.IsCalibrating && step < maxTicks)
        {
            Thread.Sleep(interval);
            session.SampleOnce();
            step++;
            if (step % 10 == 0)
                sb.AppendLine($"  {step * interval / 1000,4}s  {session.CalibrationProgress,5:F0}%  " +
                              $"T={session.TempText}°C P={session.PowerText}W  {session.CalibrationText}");
        }

        sb.AppendLine();
        sb.AppendLine("== 结果 ==");
        if (outcome is null)
        {
            sb.AppendLine("未完成（超时或被中断）");
            Write(outPath, sb);
            return 5;
        }

        sb.AppendLine($"成功        : {outcome.Ok}");
        sb.AppendLine($"说明        : {outcome.Message}");
        if (outcome.Ok)
        {
            sb.AppendLine($"热阻        : {outcome.Rth:F3} K/W");
            sb.AppendLine($"空载        : {outcome.IdleC:F1}°C @ {outcome.IdleW:F1}W");
            sb.AppendLine($"满载        : {outcome.HotC:F1}°C @ {outcome.HotW:F1}W");
            sb.AppendLine($"预测稳态    : {outcome.PredictedAtMaxC:F1}°C（按本机 PL1 {outcome.BasisW:F0}W）");
            sb.AppendLine($"推荐档位    : {outcome.Recommended}");
            sb.AppendLine($"界面显示    : Rth={session.RthText}（{session.RthSourceText}）");
        }

        Write(outPath, sb);
        return outcome.Ok ? 0 : 6;
    }

    private static void Write(string path, StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 落盘失败靠退出码 */ }
    }
}
