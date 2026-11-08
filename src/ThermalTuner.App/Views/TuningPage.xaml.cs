using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThermalTuner.App.Services;
using ThermalTuner.App.Windows;

namespace ThermalTuner.App.Views;

public partial class TuningPage : UserControl
{
    private SessionState? _subscribed;

    public TuningPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Attach(); SyncTakeover(); };
        // 截图与冒烟自检走离屏渲染，不触发 Loaded；DataContext 落地时补一次同步
        DataContextChanged += (_, _) => SyncTakeover();
        Unloaded += (_, _) => Detach();
    }

    private SessionState? Session => DataContext as SessionState;

    /// <summary>开关不是绑定控件：进入页面、点击之后都以 SessionState 为准回读一次。</summary>
    private void SyncTakeover()
    {
        if (Session is { } s) TakeoverBox.IsChecked = s.AutoTakeoverOn;
    }

    /// <summary>
    /// 档位卡内的「之后持续动态微调」：只管决策树每拍是否继续修正，
    /// 与「选中即下发一次」互不影响，所以放在档位旁边而不是设置页。
    /// </summary>
    private void Takeover_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        s.SetAutoTakeover(TakeoverBox.IsChecked == true);
        SyncTakeover();
    }

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

    /// <summary>
    /// v0.5.0「选中即生效」：点选档位立即触发一次「决策重算 + 0x610 下发」。
    /// 有闸门缺口 / 跨档升功率 / 升档冷却 / 离开手工模式 / 高温时先二次确认；
    /// 温度达到安全上限且要求升功率是唯一不可解除的硬红线。
    /// </summary>
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string profile } || Session is not { } s) return;
        if (s.Profile == profile)
        {
            ShowOutcome($"当前已是「{s.ProfileLabel}」档，无需重复下发。", false);
            return;
        }

        var plan = s.PrepareProfileApply(profile);

        if (plan.HardBlocked)
        {
            s.ClearPendingPlan();
            MessageBox.Show(
                plan.ConfirmReason +
                "\n\n温度达到安全上限时只允许降功率，这条硬件安全红线不能通过二次确认解除。" +
                "请降低负载或等温度回落后再切换。",
                "ThermalTuner · 已拒绝升功率", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowOutcome("已拒绝：高温只降不升。档位未改变。", false);
            return;
        }

        bool proceed = true;
        if (plan.NeedConfirmation)
        {
            string tempText = double.IsNaN(plan.CurrentTempC)
                ? "温度 N/A" : $"{plan.CurrentTempC:F1}°C";
            string targetText = double.IsNaN(plan.TargetPl1W)
                ? "目标 PL1 将在首拍采样后由决策树重算。"
                : $"决策树重算目标 PL1 {plan.TargetPl1W:F1}W，相对已下发值 Δ {plan.DeltaW:+0;-0;0}W。";
            proceed = ConfirmDialog.Ask(
                Window.GetWindow(this),
                "确认切换档位并下发",
                $"当前 {tempText} → 目标档位「{plan.ProfileLabel}」（天花板 {plan.CeilingC:F0}°C）。{targetText}",
                BuildConfirmItems(plan),
                "确认并下发");
        }

        if (!proceed)
        {
            s.ClearPendingPlan();
            ShowOutcome("已取消：档位与寄存器都未改变。", false);
            return;
        }

        var outcome = s.ApplyProfile(profile, force: plan.NeedConfirmation);
        ShowOutcome(outcome.Ok
                ? $"已下发 {outcome.StepPl1W}W 并回读一致 · 「{plan.ProfileLabel}」天花板 " +
                  $"{plan.CeilingC:F0}°C。功耗收敛需要 Tau 量级时间，性能不会立刻变化。"
                : $"档位意图已更新为「{plan.ProfileLabel}」（天花板 {plan.CeilingC:F0}°C）；" +
                  $"硬件下发结果：{outcome.Message}",
            outcome.Ok);
    }

    private static IEnumerable<string> BuildConfirmItems(ProfileApplyPlan plan)
    {
        yield return $"风险说明：{plan.ConfirmReason}";
        if (!plan.GatesOpen)
            yield return "确认后若写入闸门仍不可用，只更新档位意图并记录原因，不会静默假装已写入";
        if (plan.LeaveManualMode)
            yield return "将停止开机重放并清除已保存的手工微调值；此操作不可自动撤销";
        if (plan.CooldownRemainingS > 0 && plan.DeltaW > 0)
            yield return $"本次确认会绕过剩余 {plan.CooldownRemainingS:F0}s 升档冷却，仅这一次";
        yield return "写入前备份当前功耗参数，写入后立即回读校验，失败自动回滚并留审计";
        yield return "只改写持续 / 短时功耗上限；短时时长保持原值，能效偏好与风扇在智能链路不写入";
        yield return "高温 ≥ 安全上限时的「只降不升」仍是硬红线，本确认无法解除";
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
            MessageBox.Show("已取消提权重启。读取真实功耗与硬件参数都需要管理员权限，权限不足时只能看估算值。",
                "ThermalTuner", MessageBoxButton.OK, MessageBoxImage.Information);
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
                "只更新档位意图，不触发写入；要立即下发请在档位区点选该档",
                "热阻已记录为本机稳态值，在线拟合只做微调"
            },
            "应用推荐档位");

        if (apply) s.SetProfile(o.Recommended);
    }

    private void ShowOutcome(string text, bool ok)
    {
        OutcomeText.Text = text;
        OutcomeBanner.Background = (Brush)FindResource(ok ? "AccentSoft" : "WarnSoft");
        OutcomeBanner.BorderBrush = (Brush)FindResource(ok ? "Accent" : "Warn");
        OutcomeText.Foreground = (Brush)FindResource(ok ? "AccentDeep" : "Warn");
        OutcomeBanner.Visibility = Visibility.Visible;
    }
}
