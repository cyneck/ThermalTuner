using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ThermalTuner.App.Services;
using ThermalTuner.Core.Stress;

namespace ThermalTuner.App.Views;

public partial class MonitorPage : UserControl
{
    public MonitorPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SessionState s)
                CountText.Text = $"已采样 {s.Samples.Count} 条";
        };
    }

    private SessionState? Session => DataContext as SessionState;

    private void ToggleRun_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (s.IsRunning) s.Stop();
        else s.Start();
    }

    /// <summary>当前选中的负载场景内核；场景切换会即时重建负载。</summary>
    private StressKernel CurrentKernel =>
        KernelBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
        StressScenarioCatalog.TryParseKernel(tag, out var k)
            ? k : StressKernel.SimdFma;

    private void Stress_Checked(object sender, RoutedEventArgs e)
    {
        if (Session is { } s) s.SetStress(true, s.Environment.LogicalCores, CurrentKernel);
    }

    private void Stress_Unchecked(object sender, RoutedEventArgs e) => Session?.SetStress(false);

    private void Kernel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StressBox.IsChecked == true && Session is { } s)
            s.SetStress(true, s.Environment.LogicalCores, CurrentKernel);
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Session is null || IntervalBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string tag && int.TryParse(tag, out int ms))
            Session.SetInterval(ms);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        var dlg = new SaveFileDialog
        {
            Title = "导出采样数据",
            Filter = "CSV 文件|*.csv",
            FileName = $"thermalpilot-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            int n = s.ExportCsv(dlg.FileName);
            MessageBox.Show($"已导出 {n} 条采样记录：\n{dlg.FileName}", "ThermalTuner",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "ThermalTuner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
