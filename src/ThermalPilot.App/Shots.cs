using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThermalPilot.App.Services;
using ThermalPilot.App.Views;
using ThermalPilot.App.Windows;

namespace ThermalPilot.App;

/// <summary>
/// 离屏渲染：把窗口与各页面渲染成 PNG，用于设计走查与研发对照。
/// 用法：ThermalPilot.App.exe --shots artifacts/ui-shots
/// </summary>
internal static class Shots
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var session = new SessionState();
        int ok = 0, fail = 0;

        PrimeData(session);

        try
        {
            // 主窗口内容
            try
            {
                var win = new MainWindow(session);
                if (win.Content is FrameworkElement content)
                {
                    object? dc = content.DataContext;
                    win.Content = null; // 先从 Window 逻辑树摘下，才能放进离屏容器
                    var wrapWin = Wrap(content, 1320, 840);
                    wrapWin.DataContext = dc; // 脱离窗口后需手动续上 DataContext 继承链
                    Save(wrapWin, 1320, 840, Path.Combine(dir, "00-overview-window.png"));
                }
                ok++;
            }
            catch (Exception ex) { fail++; Console.Error.WriteLine("MainWindow: " + ex.Message); }

            var pages = new (string File, Func<FrameworkElement> Factory)[]
            {
                ("01-overview.png", () => new OverviewPage()),
                ("02-tuning.png", () => new TuningPage()),
                ("03-monitor.png", () => new MonitorPage()),
                ("04-params.png", () => new ParamsPage()),
                ("05-diagnostics.png", () => new DiagnosticsPage()),
                ("06-settings.png", () => new SettingsPage())
            };

            foreach (var (file, factory) in pages)
            {
                try
                {
                    var page = factory();
                    page.DataContext = session;
                    page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Save(Wrap(page, 1096, 800), 1096, 800, Path.Combine(dir, file));
                    ok++;
                }
                catch (Exception ex) { fail++; Console.Error.WriteLine($"{file}: {ex.Message}"); }
            }

            // 首次向导
            try
            {
                var wiz = new FirstRunWizard(session);
                if (wiz.Content is FrameworkElement c)
                {
                    object? dcWiz = c.DataContext;
                    wiz.Content = null;
                    var wrapWiz = Wrap(c, 760, 580);
                    wrapWiz.DataContext = dcWiz;
                    Save(wrapWiz, 760, 580, Path.Combine(dir, "07-wizard.png"));
                }
                ok++;
            }
            catch (Exception ex) { fail++; Console.Error.WriteLine("Wizard: " + ex.Message); }

            // 危险操作确认弹窗
            try
            {
                var dlg = new ConfirmDialog("确认应用参数",
                    "以下改动将写入 CPU 功耗寄存器。当前版本为演练模式：只走完整确认流程，不实际写入。",
                    new[] { "PL1 44.0W → 38.0W", "PL2 55.0W → 46.0W" },
                    "确认应用");
                if (dlg.Content is FrameworkElement c)
                {
                    dlg.Content = null;
                    Save(Wrap(c, 480, 430), 480, 430, Path.Combine(dir, "08-confirm.png"));
                }
                ok++;
            }
            catch (Exception ex) { fail++; Console.Error.WriteLine("ConfirmDialog: " + ex); }
        }
        finally
        {
            session.Dispose();
        }

        Console.Error.WriteLine($"shots ok={ok} fail={fail} dir={dir}");
        return fail;
    }

    /// <summary>先采几拍真实数据；拿不到温度时注入一条标注为演示的曲线，仅用于设计走查。</summary>
    private static void PrimeData(SessionState s)
    {
        for (int i = 0; i < 4; i++)
        {
            s.SampleOnce();
            System.Threading.Thread.Sleep(250);
        }

        if (s.TempHistory.Count >= 2) return;

        var rnd = new Random(11);
        double temp = 46, pl1 = 38;
        for (int i = 0; i < 80; i++)
        {
            temp = Math.Clamp(temp + (rnd.NextDouble() - 0.42) * 2.2, 42, 90);
            pl1 = Math.Clamp(pl1 + (rnd.NextDouble() - 0.45) * 5, 30, 88);
            s.TempHistory.Add(Math.Round(temp, 1));
            s.Pl1History.Add(Math.Round(pl1, 1));
            s.PowerHistory.Add(Math.Round(pl1 * 0.6, 1));
        }

        s.Decisions.Add(new DecisionRow("10:21:08", "bottleneck_arbiter", "[功耗墙突破] 尚有 34.0°C 安全余量，升功耗至 41.0W", "41.0W ↑", "↑", "PowerWall", "safety → light-load → predictive → ceiling → arbiter"));
        s.Decisions.Add(new DecisionRow("10:21:09", "thermal_predictive", "[热阻稳态预警] 预测升至 90.2°C 将超标，提前锁定当前功耗", "41.0W →", "→", "ThermalLimit", "safety → light-load → predictive"));
        for (int i = 0; i < 8; i++)
            s.Samples.Add(new SampleRow($"10:21:{i + 1:00}",
                $"{52 + i * 2.4:F1}", $"{18 + (i * 7) % 60}%", $"{22 + i * 3}W", $"{38 + i * 2:F1}W",
                i % 3 == 0 ? "bottleneck_arbiter" : "thermal_predictive"));

        s.AddAlert("info", "开始监控", "采样间隔 1000ms，档位 均衡（演示数据）");
    }

    private static FrameworkElement Wrap(FrameworkElement child, double w, double h)
    {
        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["BgCanvas"],
            Child = child
        };
        border.Width = w;
        border.Height = h;
        return border;
    }

    private static void Save(FrameworkElement element, double w, double h, string path)
    {
        element.Measure(new Size(w, h));
        element.Arrange(new Rect(0, 0, w, h));
        element.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
