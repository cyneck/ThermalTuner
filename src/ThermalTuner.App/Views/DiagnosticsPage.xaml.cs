using System.IO;
using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using ThermalTuner.App.Services;

namespace ThermalTuner.App.Views;

public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private SessionState? Session => DataContext as SessionState;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>管理员重新自检（无害写探测）。原「高级参数」页的按钮迁到诊断页。</summary>
    private void EnvCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        string result = s.RunFullEnvironmentCheck();
        EnvCheckOutcome.Text = "环境自检：" + result;
        EnvCheckOutcome.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        if (Session is not { } s) return;

        VmText.Text = s.Environment.IsVirtualized ? "是（功耗寄存器可能不可写）" : "否";

        try
        {
            SensorText.Text = string.Join(Environment.NewLine, s.DumpSensors());
        }
        catch (Exception ex)
        {
            SensorText.Text = "读取传感器失败：" + ex.Message;
        }

        HvciText.Text = ReadHvci();
    }

    private static string ReadHvci()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, CodeIntegrityPolicyEnforcementStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    object? vbs = mo["VirtualizationBasedSecurityStatus"];
                    object? ci = mo["CodeIntegrityPolicyEnforcementStatus"];
                    string vbsText = Convert.ToInt32(vbs) switch
                    {
                        2 => "VBS 已启用",
                        1 => "VBS 已启用（未运行）",
                        _ => "VBS 关闭"
                    };
                    string ciText = Convert.ToInt32(ci) switch
                    {
                        2 => "驱动强制签名",
                        1 => "审计模式",
                        _ => "签名策略关闭"
                    };
                    return $"{vbsText} · {ciText}";
                }
            }
        }
        catch
        {
            // 非管理员或平台不支持时静默降级
        }
        return "无法读取（需管理员或平台不支持）";
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsStore.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开目录：" + ex.Message, "ThermalTuner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
