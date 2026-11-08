using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThermalTuner.App.Services;

namespace ThermalTuner.App;

/// <summary>
/// 离屏 UI 截图：构造真实主窗口 → 采几拍遥测 → 切到指定页 → 渲染成 PNG。
/// 用途：向用户展示实际界面、留存 UI 证据、回归比对；不需要人工点窗口。
/// 用法：ThermalTuner.App.exe --capture-ui --page overview --out ui.png [--height 1600]
/// 概览页内容较长，默认 840 只截首屏；传 --height 可一次性渲染整页用于走查。
///
/// 安全边界：MainWindow.CaptureMode 下不建托盘、不弹首次向导、不恢复 MSR、
/// 不写 settings.json；本命令只做只读采样与渲染，不写任何寄存器。
/// </summary>
internal static class UiCapture
{
    public static int Run(string outPath, string pageKey, int height = 840)
    {
        if (string.Equals(pageKey, "startup", StringComparison.OrdinalIgnoreCase))
            return CaptureStartup(outPath);

        SessionState? session = null;
        MainWindow? window = null;

        try
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            session = new SessionState();
            // 先攒几拍真实读数，保证指标卡 / 曲线不是空的
            for (int i = 0; i < 4; i++)
            {
                session.SampleOnce();
                Thread.Sleep(260);
            }

            window = new MainWindow(session)
            {
                CaptureMode = true,
                ShowActivated = false,
                Width = 1320,
                Height = Math.Clamp(height, 600, 4000)
            };
            window.NavigateTo(pageKey);
            window.Show();
            Pump();                 // 让 Loaded 触发
            Pump();                 // 让绑定与首次绘制完成

            if (window.Content is not FrameworkElement root)
                throw new InvalidOperationException("主窗口根元素不是 FrameworkElement");

            if (root.RenderSize.Width < 100 || root.RenderSize.Height < 100)
            {
                root.Measure(new Size(1320, 800));
                root.Arrange(new Rect(0, 0, 1320, 800));
            }
            window.UpdateLayout();
            Pump();
            Pump();

            int w = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Width));
            int h = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Height));

            // Window.Background 挂在 Window 上而非根 Grid，直接渲染会产生透明区，
            // 部分看图器把 alpha=0 合成成黑块。这里把画面合成到一层不透明底色上。
            var raw = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            raw.Render(root);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                dc.DrawImage(raw, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(full))
                encoder.Save(fs);

            File.WriteAllText(Path.ChangeExtension(full, ".txt"),
                $"页面      : {pageKey}\n" +
                $"尺寸      : {w}x{h}\n" +
                $"CPU       : {session.CpuName}\n" +
                $"CPU 使用率: {session.CpuLoadText}\n" +
                $"GPU 使用率: {session.GpuLoadText}\n" +
                $"GPU 显存  : {session.GpuMemoryText}\n" +
                $"风扇      : {session.FanMeasuredText}（{session.FanMeasuredHintText}）\n" +
                $"温度/功耗 : {session.TempText}°C / {session.PowerText}W（{session.PowerSource}）\n" +
                "RESULT: PASS", new System.Text.UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.ChangeExtension(Path.GetFullPath(outPath), ".txt"),
                    "RESULT: FAIL\n" + ex, new System.Text.UTF8Encoding(true));
            }
            catch { /* 落盘失败靠退出码 */ }
            return 5;
        }
        finally
        {
            window?.Hide();
            session?.Dispose();
        }
    }

    /// <summary>单独截启动初始化页：不跑检测、不写盘，只画「已就绪」状态。</summary>
    private static int CaptureStartup(string outPath)
    {
        Windows.StartupWindow? window = null;
        try
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            window = new Windows.StartupWindow
            {
                PreviewMode = true,
                ShowActivated = false
            };
            window.Show();
            Pump();
            Pump();
            window.UpdateLayout();
            Pump();

            if (window.Content is not FrameworkElement root)
                throw new InvalidOperationException("启动页根元素不是 FrameworkElement");

            int w = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Width));
            int h = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Height));

            var raw = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            raw.Render(root);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
                dc.DrawImage(raw, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(full))
                encoder.Save(fs);

            File.WriteAllText(Path.ChangeExtension(full, ".txt"),
                $"页面      : startup\n尺寸      : {w}x{h}\nRESULT: PASS",
                new System.Text.UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.ChangeExtension(Path.GetFullPath(outPath), ".txt"),
                    "RESULT: FAIL\n" + ex, new System.Text.UTF8Encoding(true));
            }
            catch { /* 落盘失败靠退出码 */ }
            return 5;
        }
        finally
        {
            window?.Hide();
            window?.Close();
        }
    }

    /// <summary>排空一次 Dispatcher 队列到 ContextIdle，等价于「让 UI 画完一帧」。</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }), null);
        Dispatcher.PushFrame(frame);
    }
}
