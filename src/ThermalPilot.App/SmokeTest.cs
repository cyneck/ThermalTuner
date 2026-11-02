using System.IO;
using System.Text;
using System.Windows;
using ThermalPilot.App.Services;
using ThermalPilot.App.Views;

namespace ThermalPilot.App;

/// <summary>
/// 无界面自检：构造会话、逐页加载并执行一次布局，把结果写入文件。
/// 用途：研发改 UI 后快速确认「XAML 能解析、绑定不炸、页面能渲染」，不需要人工点。
/// 用法：ThermalPilot.App.exe --smoke --out smoke.txt
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
            sb.AppendLine();

            sb.AppendLine("== 主循环（3 拍）==");
            for (int i = 1; i <= 3; i++)
            {
                session.SampleOnce();
                sb.AppendLine($"  拍{i}: T={session.TempText}°C load={session.LoadText}% P={session.PowerText}W " +
                              $"PL1={session.Pl1Text}W 规则={session.LastRule}");
                System.Threading.Thread.Sleep(350);
            }
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
