using System.Windows;
using System.Windows.Controls;
using ThermalPilot.App.Services;

namespace ThermalPilot.App.Views;

public partial class TuningPage : UserControl
{
    public TuningPage() => InitializeComponent();

    private SessionState? Session => DataContext as SessionState;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string profile })
            Session?.SetProfile(profile);
    }

    private void Ceiling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 只有用户拖动导致的差异才回写，避免与 OneWay 绑定互相触发
        if (Session is { } s && Math.Abs(s.CeilingC - e.NewValue) > 0.4)
            s.SetCeiling(e.NewValue);
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        Session?.BeginCalibration();
    }

    private void CancelCalibration_Click(object sender, RoutedEventArgs e)
    {
        Session?.CancelCalibration();
    }
}
