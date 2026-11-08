using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ThermalTuner.App.Services;
using ThermalTuner.Core.Stress;

namespace ThermalTuner.App.Views;

/// <summary>
/// 概览页（唯一监控主视图）。原「实时监控」页的工具条与采样明细已并入本页，
/// 事件处理随之迁移；页面缓存复用，Ticked 订阅在 Unloaded 时解除，避免泄漏。
/// </summary>
public partial class OverviewPage : UserControl
{
    private SessionState? _session;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private SessionState? Session => DataContext as SessionState;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        _session = s;
        s.Ticked += OnTicked;
        UpdateCount(s);
        SyncRunControls();
    }

    /// <summary>负载场景只在真正施加压测时可操作，避免「选了场景却没生效」的误解。</summary>
    private void SyncRunControls() => KernelBox.IsEnabled = StressBox.IsChecked == true;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_session is { } s) s.Ticked -= OnTicked;
        _session = null;
    }

    private void OnTicked()
    {
        if (_session is { } s) UpdateCount(s);
    }

    private void UpdateCount(SessionState s) => CountText.Text = $"已采样 {s.Samples.Count} 条";

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
        SyncRunControls();
        if (Session is { } s) s.SetStress(true, s.Environment.LogicalCores, CurrentKernel);
    }

    private void Stress_Unchecked(object sender, RoutedEventArgs e)
    {
        if (Session is { } s) s.SetStress(false);
        SyncRunControls();
    }

    private void Kernel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StressBox.IsChecked == true && Session is { } s)
            s.SetStress(true, s.Environment.LogicalCores, CurrentKernel);
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Session is not { } s || IntervalBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string tag && int.TryParse(tag, out int ms))
            s.SetInterval(ms);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        var dlg = new SaveFileDialog
        {
            Title = "导出采样数据",
            Filter = "CSV 文件|*.csv",
            FileName = $"thermaltuner-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
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
