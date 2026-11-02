using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ThermalPilot.App.Services;
using ThermalPilot.App.Windows;

namespace ThermalPilot.App.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromSettings();
    }

    private SessionState? Session => DataContext as SessionState;

    private void LoadFromSettings()
    {
        if (Session is not { } s) return;

        _loading = true;
        TakeoverBox.IsChecked = s.Settings.AutoTakeover;
        RestoreBox.IsChecked = s.Settings.RestoreOnExit;
        TrayBox.IsChecked = s.Settings.CloseToTray;
        RealWriteBox.IsChecked = s.Settings.AllowRealWrite;
        StartupBox.IsChecked = StartupRegistration.IsEnabled();
        MsrStatusLine.Text = $"MSR 通道：{s.MsrStatusText} —— {s.MsrDetail}";

        foreach (ComboBoxItem item in IntervalBox.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out int ms) && ms == s.Settings.IntervalMs)
            {
                IntervalBox.SelectedItem = item;
                break;
            }
        }
        _loading = false;

        DataDirText.Text = SettingsStore.DataDirectory;
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Session is not { } s) return;
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int ms))
            s.SetInterval(ms);
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || Session is not { } s) return;

        s.Settings.AutoTakeover = TakeoverBox.IsChecked == true;
        s.Settings.RestoreOnExit = RestoreBox.IsChecked == true;
        s.Settings.CloseToTray = TrayBox.IsChecked == true;
        SettingsStore.Save(s.Settings);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = StartupBox.IsChecked == true;
        bool ok = StartupRegistration.Set(wanted);
        if (!ok)
        {
            MessageBox.Show("写入开机启动项失败（可能被安全软件拦截）。", "ThermalPilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StartupBox.IsChecked = StartupRegistration.IsEnabled();
        }
    }

    private void RealWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || Session is not { } s) return;

        bool wanted = RealWriteBox.IsChecked == true;
        if (!wanted)
        {
            s.SetAllowRealWrite(false);
            MsrStatusLine.Text = $"MSR 通道：{s.MsrStatusText} —— 已关闭实写";
            return;
        }

        if (!s.MsrWritable)
        {
            MessageBox.Show($"当前通道不可写：{s.MsrDetail}\n\n请以管理员身份运行，并确认 PawnIO 已安装。",
                "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            RealWriteBox.IsChecked = false;
            return;
        }

        bool confirmed = ConfirmDialog.Ask(
            Window.GetWindow(this),
            "开启真实写入",
            "开启后，应用参数会立即改变 CPU 功耗上限（PL1/PL2），可能影响性能、温度与稳定性。",
            new[]
            {
                "写入前会备份 0x610 原值到本地",
                "写入后立即回读校验，不一致自动回滚",
                "退出程序时可按设置恢复原值",
                "建议先在低风险档位试用，确认机器稳定后再长期使用"
            },
            "我已了解，开启");

        if (!confirmed)
        {
            RealWriteBox.IsChecked = false;
            return;
        }

        s.SetAllowRealWrite(true);
        MsrStatusLine.Text = $"MSR 通道：{s.MsrStatusText} —— 实写已授权";
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            Process.Start(new ProcessStartInfo { FileName = SettingsStore.DataDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开目录：" + ex.Message, "ThermalPilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RerunWizard_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        var wizard = new FirstRunWizard(s) { Owner = Window.GetWindow(this) };
        if (wizard.ShowDialog() == true && wizard.Adopted)
        {
            s.SetProfile(wizard.RecommendedProfile);
            s.AddAlert("info", "已按向导重新设置", $"档位切换为「{wizard.RecommendedProfile}」。");
        }
    }
}
