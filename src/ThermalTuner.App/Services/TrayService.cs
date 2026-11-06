using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ThermalTuner.App.Services;

/// <summary>
/// 托盘常驻（D-03：1s 采样 + 托盘）。
/// 图标统一取 Assets/ThermalTuner.ico（与 exe / 窗口一致），资源不可用时回退到运行时绘制。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _silent;
    private readonly WinForms.ToolStripMenuItem _balanced;
    private readonly WinForms.ToolStripMenuItem _performance;
    private bool _disposed;

    public TrayService(SessionState session)
    {
        _silent = new WinForms.ToolStripMenuItem("静音优先", null, (_, _) => ProfileRequested?.Invoke("silent"));
        _balanced = new WinForms.ToolStripMenuItem("均衡", null, (_, _) => ProfileRequested?.Invoke("balanced"));
        _performance = new WinForms.ToolStripMenuItem("性能优先", null, (_, _) => ProfileRequested?.Invoke("performance"));

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 ThermalTuner", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("档位") { Enabled = false });
        menu.Items.Add(_silent);
        menu.Items.Add(_balanced);
        menu.Items.Add(_performance);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出（恢复原值）", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ThermalTuner · CPU 热约束调优",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        UpdateProfile(session.Profile);
        UpdateTooltip(session);
    }

    public event Action? OpenRequested;
    public event Action<string>? ProfileRequested;
    public event Action? ExitRequested;

    public void UpdateProfile(string profile)
    {
        _silent.Checked = profile == "silent";
        _balanced.Checked = profile == "balanced";
        _performance.Checked = profile == "performance";
    }

    public void UpdateTooltip(SessionState session)
    {
        string temp = double.IsNaN(session.TempC) ? "N/A" : $"{session.TempC:F0}°C";
        string text = $"ThermalTuner · {temp} · PL1 {session.Pl1Text}W · {session.ProfileLabel}";
        // NotifyIcon.Text 上限 63 字符
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);
        }
        catch { /* 系统通知关闭时忽略 */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 优先用产品 ico（与 exe / 窗口一致）；命令行或资源未打包时回退到运行时绘制。
    /// </summary>
    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/ThermalTuner.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                using (stream) return new Drawing.Icon(stream);
            }
        }
        catch
        {
            // 资源不可用时走回退，不阻断托盘
        }
        return CreateIcon();
    }

    /// <summary>回退图标：更名后缩写为 TT，配色与产品 ico 一致。</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0F, 0x27, 0x40));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var font = new Drawing.Font("Segoe UI", 13, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x4F, 0xD1, 0xC5));
            var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("TT", font, fg, new Drawing.RectangleF(0, 0, 32, 32), format);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)temp.Clone();   // 复制一份，原句柄由 DestroyIcon 释放
        }
        finally
        {
            DestroyIcon(hIcon);                  // Icon.FromHandle 不接管所有权，必须显式销毁
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { /* 忽略 */ }
    }
}
