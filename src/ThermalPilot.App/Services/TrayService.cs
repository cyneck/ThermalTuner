using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ThermalPilot.App.Services;

/// <summary>
/// 托盘常驻（D-03：1s 采样 + 托盘）。
/// 图标在运行时绘制，免去 ico 资源管线；右键菜单提供打开 / 切档 / 退出。
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
        menu.Items.Add("打开 ThermalPilot", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("档位") { Enabled = false });
        menu.Items.Add(_silent);
        menu.Items.Add(_balanced);
        menu.Items.Add(_performance);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出（恢复原值）", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "ThermalPilot · CPU 热约束调优",
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
        string text = $"ThermalPilot · {temp} · PL1 {session.Pl1Text}W · {session.ProfileLabel}";
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

    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x2E, 0x6F, 0x5F));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var font = new Drawing.Font("Segoe UI", 13, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fg = new Drawing.SolidBrush(Drawing.Color.White);
            var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("TP", font, fg, new Drawing.RectangleF(0, 0, 32, 32), format);
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
