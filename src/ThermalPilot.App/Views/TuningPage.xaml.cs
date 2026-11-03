using System.Windows;
using System.Windows.Controls;
using ThermalPilot.App.Services;
using ThermalPilot.App.Windows;

namespace ThermalPilot.App.Views;

public partial class TuningPage : UserControl
{
    private SessionState? _subscribed;

    public TuningPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private SessionState? Session => DataContext as SessionState;

    private void Attach()
    {
        Detach();
        if (DataContext is SessionState s)
        {
            s.CalibrationFinished += OnCalibrationFinished;
            _subscribed = s;
        }
    }

    private void Detach()
    {
        if (_subscribed is not null) _subscribed.CalibrationFinished -= OnCalibrationFinished;
        _subscribed = null;
    }

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

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (!s.RestartElevated())
            MessageBox.Show("已取消提权重启。读取真实功耗与 MSR 都需要管理员权限，权限不足时只能看估算值。",
                "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>标定成功后询问是否立即应用推荐档位，避免「算完只弹一条消息」。</summary>
    private void OnCalibrationFinished(CalibrationOutcome o)
    {
        if (!o.Ok || Session is not { } s) return;

        string label = o.Recommended switch
        {
            "silent" => "静音优先",
            "performance" => "性能优先",
            _ => "均衡"
        };

        bool apply = ConfirmDialog.Ask(
            Window.GetWindow(this),
            "标定完成",
            $"稳态热阻 {o.Rth:F2} K/W（{o.IdleC:F1}°C/{o.IdleW:F0}W → {o.HotC:F1}°C/{o.HotW:F0}W）。" +
            $"按本机 PL1 {o.BasisW:F0}W 预测稳态 {o.PredictedAtMaxC:F1}°C，建议「{label}」。",
            new[]
            {
                $"应用后：档位切换到「{label}」，目标温度随档位更新",
                "不会写入 MSR；若已开启自动接管，后续仍按实时温度决策",
                "热阻已记录为本机稳态值，在线拟合只做微调"
            },
            "应用推荐档位");

        if (apply) s.SetProfile(o.Recommended);
    }
}
