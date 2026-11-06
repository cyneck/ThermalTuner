using System.IO;
using System.Text;
using System.Windows;
using ThermalTuner.App.Services;
using ThermalTuner.App.Views;

namespace ThermalTuner.App;

/// <summary>
/// 无界面自检：构造会话、逐页加载并执行一次布局，把结果写入文件。
/// 用途：研发改 UI 后快速确认「XAML 能解析、绑定不炸、页面能渲染」，不需要人工点。
/// 用法：ThermalTuner.App.exe --smoke --out smoke.txt
/// </summary>
internal static class SmokeTest
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        int failures = 0;

        SessionState? session = null;
        try
        {
            session = new SessionState();
            sb.AppendLine("== 环境 ==");
            sb.AppendLine($"CPU          : {session.CpuName}");
            sb.AppendLine($"平台         : {session.PlatformText} / {session.FormText}");
            sb.AppendLine($"核心         : {session.CoreText}");
            sb.AppendLine($"温度/功耗    : {session.TjMaxText} · {session.PlRangeText}");
            sb.AppendLine($"驱动/权限    : {session.DriverText} / {session.ElevationText}");
            sb.AppendLine($"传感器       : {session.SensorCount}");
            sb.AppendLine($"功耗计量     : {session.RaplText}");
            sb.AppendLine($"标定持久化   : {session.CalibrationSummaryText}");
            sb.AppendLine($"微调持久化   : {session.SavedTuningText}");
            sb.AppendLine($"开机重放     : {session.Settings.ReplayOnStartup}（允许实写 {session.Settings.AllowRealWrite}）");
            sb.AppendLine();

            sb.AppendLine("== 主循环（3 拍）==");
            for (int i = 1; i <= 3; i++)
            {
                session.SampleOnce();
                sb.AppendLine($"  拍{i}: T={session.TempText}°C load={session.LoadText}% P={session.PowerText}W " +
                              $"来源={session.PowerSource} Rth={session.RthText}({session.RthSourceText}) " +
                              $"PL1={session.Pl1Text}W 规则={session.LastRule}");
                System.Threading.Thread.Sleep(350);
            }

            // 采样后再判定：CanCalibrate 依赖首个有效温度样本
            sb.AppendLine($"可否标定     : {session.CanCalibrate}（{session.CalibrationBlockedReason}）");
            sb.AppendLine();

            sb.AppendLine("== 热模型 / 固件锚点 ==");
            sb.AppendLine($"最高核频     : {session.ClockText} GHz");
            sb.AppendLine($"固件默认     : {session.FwDefaultText}");
            sb.AppendLine($"当前生效     : PL1 {session.FwPl1Text}W · PL2 {session.FwPl2Text}W · Tau {session.FwTauText} · EPP {session.FwEppText}");
            sb.AppendLine($"EPP 可编辑   : {session.EppEditable}");
            sb.AppendLine($"预测稳态     : {session.PredictedText}°C（{session.PredictedBasisText}）");
            sb.AppendLine($"预测基线     : {session.PredictedBaselineText}");
            sb.AppendLine($"标定前馈     : {session.ThermalCapText}");
            sb.AppendLine($"分歧说明     : {session.FwDivergeText}");
            sb.AppendLine();

            sb.AppendLine("== 页面 ==");
            var pages = new (string Name, Func<FrameworkElement> Factory)[]
            {
                ("概览", () => new OverviewPage()),
                ("智能调优", () => new TuningPage()),
                ("实时监控", () => new MonitorPage()),
                ("高级参数", () => new ParamsPage()),
                ("诊断与日志", () => new DiagnosticsPage()),
                ("设置", () => new SettingsPage())
            };

            foreach (var (name, factory) in pages)
            {
                try
                {
                    var page = factory();
                    page.DataContext = session;
                    page.Measure(new Size(1200, 820));
                    page.Arrange(new Rect(0, 0, 1200, 820));
                    page.UpdateLayout();
                    sb.AppendLine($"OK    {name}  (desired {page.DesiredSize.Width:F0}x{page.DesiredSize.Height:F0})");
                }
                catch (Exception ex)
                {
                    failures++;
                    sb.AppendLine($"FAIL  {name}  {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 主窗口同样做一次构造 + 布局（不 Show，避免触发首次向导）
            try
            {
                var win = new MainWindow();
                win.Measure(new Size(1320, 840));
                win.Arrange(new Rect(0, 0, 1320, 840));
                win.UpdateLayout();
                sb.AppendLine("OK    MainWindow");
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine($"FAIL  MainWindow  {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine("FAIL  会话初始化  " + ex);
        }
        finally
        {
            session?.Dispose();
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 落盘失败就只能靠退出码
        }

        return failures;
    }
}
