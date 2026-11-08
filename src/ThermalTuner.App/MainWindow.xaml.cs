using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThermalTuner.App.Services;
using ThermalTuner.App.Views;
using ThermalTuner.App.Windows;

namespace ThermalTuner.App;

public partial class MainWindow : Window
{
    private readonly SessionState _session;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private TrayService? _tray;
    private bool _reallyExit;
    private bool _hideHintShown;

    /// <summary>离屏截图模式：跳过托盘、首次向导与关闭时的寄存器恢复/配置落盘。</summary>
    public bool CaptureMode { get; set; }
    private readonly Dictionary<string, (string Title, string Subtitle)> _meta = new()
    {
        ["overview"] = ("概览", "全部监控指标、统一时序图、决策凭证与采样明细"),
        ["tuning"] = ("智能微调", "选中档位即下发；阶梯调度器按温度低频动态微调"),
        ["params"] = ("手工微调", "解锁编辑全部参数，保存并下发后进入手工模式"),
        ["diagnostics"] = ("诊断与日志", "环境体检、固件基线对照、自检报告与恢复机制"),
        ["settings"] = ("设置", "调优模式、升档冷却、自启、采样与写入授权")
    };

    public MainWindow() : this(new SessionState())
    {
    }

    /// <summary>注入会话：便于离屏渲染与集成测试复用同一份数据。</summary>
    public MainWindow(SessionState session)
    {
        _session = session;
        InitializeComponent();
        DataContext = _session;
        Navigate("overview");   // 构造即落地首屏，避免离屏渲染/自检时内容区为空

        Loaded += OnLoaded;
        Closing += OnClosing;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionState.StatusText)
                or nameof(SessionState.StatusLevel)
                or nameof(SessionState.DriverAvailable)
                or nameof(SessionState.AlertCount))
                UpdateChrome();

            if (e.PropertyName is nameof(SessionState.TempText) or nameof(SessionState.Pl1Text))
                _tray?.UpdateTooltip(_session);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _session.Start();
        UpdateChrome();
        if (CaptureMode) return;   // 截图：不建托盘、不弹向导
        SetupTray();

        if (!_session.Settings.WizardDone)
        {
            var wizard = new FirstRunWizard(_session) { Owner = this };
            bool? ok = wizard.ShowDialog();
            bool adopted = wizard.Adopted;
            _session.Settings.WizardDone = true;
            if (ok == true && adopted)
            {
                _session.SetProfile(wizard.RecommendedProfile);
                _session.Settings.AutoTakeover = true;
                SettingsStore.Save(_session.Settings);
            }
            SettingsStore.Save(_session.Settings);
            UpdateChrome();
        }
    }

    private void SetupTray()
    {
        try
        {
            _tray = new TrayService(_session);
            _tray.OpenRequested += () =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
            _tray.ProfileRequested += profile =>
            {
                // v0.5.0：托盘切档与页面点选走同一条「选中即下发」链路，含二次确认。
                if (_session.Profile == profile) return;
                var plan = _session.PrepareProfileApply(profile);
                if (plan.HardBlocked)
                {
                    _session.ClearPendingPlan();
                    _session.AddAlert("danger", "托盘切档被拒绝",
                        plan.ConfirmReason + "；高温只降不升不可由二次确认解除。");
                    return;
                }

                bool proceed = !plan.NeedConfirmation || ConfirmDialog.Ask(
                    null, "确认切换档位并下发",
                    $"托盘切换到「{plan.ProfileLabel}」（天花板 {plan.CeilingC:F0}°C）。" +
                    (double.IsNaN(plan.TargetPl1W)
                        ? "目标 PL1 将在首拍采样后重算。"
                        : $"目标 PL1 {plan.TargetPl1W:F1}W，Δ {plan.DeltaW:+0;-0;0}W。"),
                    new[] { plan.ConfirmReason, "只写 0x610 的 PL1/PL2；回读校验，失败回滚并留审计" },
                    "确认并下发");

                if (!proceed)
                {
                    _session.ClearPendingPlan();
                    return;
                }

                var outcome = _session.ApplyProfile(profile, force: plan.NeedConfirmation);
                _tray?.UpdateProfile(profile);
                _session.AddAlert(outcome.Ok ? "info" : "warn", "托盘切换档位",
                    outcome.Ok
                        ? $"已下发 {outcome.StepPl1W}W 到 0x610 并回读校验（{plan.ProfileLabel}）。"
                        : $"档位意图已更新为「{plan.ProfileLabel}」；硬件下发：{outcome.Message}");
            };
            _tray.ExitRequested += ExitApplication;

            if (_session.Settings.RunAtStartup)
                StartupRegistration.Set(true);
        }
        catch
        {
            // 托盘不可用（如无桌面会话）不影响主窗口
        }
    }

    /// <summary>托盘/菜单退出：恢复原值（若开启）后真正结束进程。</summary>
    public void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (CaptureMode) return;   // 截图：绝不恢复 MSR、绝不写 settings.json
        if (!_reallyExit)
        {
            if (_session.Settings.CloseToTray && _tray is not null)
            {
                e.Cancel = true;
                Hide();
                if (!_hideHintShown)
                {
                    _hideHintShown = true;
                    _tray.ShowBalloon("ThermalTuner 仍在后台运行",
                        "采样继续，双击托盘图标可重新打开；右键菜单可退出。");
                }
                return;
            }
        }

        // 退出时恢复 MSR 原值（PRD G-05）
        // 注意：恢复失败只记告警，绝不弹模态框——否则托盘退出会被对话框挂住，进程无法结束
        if (_session.Settings.RestoreOnExit && _session.HasMsrBackup && _session.MsrWritable)
        {
            var r = _session.RestoreOriginal();
            if (!r.Ok)
                _session.AddAlert("warn", "退出前恢复原值失败",
                    r.Message + "；请重启后在「手工微调」页点「恢复原值」，或重启机器回到固件默认值。");
        }

        _tray?.Dispose();
        SettingsStore.Save(_session.Settings);
        _session.Dispose();
    }

    // ---------------- 导航 ----------------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) Navigate(key);
    }

    /// <summary>供离屏渲染 / 集成测试切换到指定页，并同步左侧导航选中态。</summary>
    public void NavigateTo(string key)
    {
        Navigate(key);
        RadioButton? nav = key switch
        {
            "overview" => NavOverview,
            "tuning" => NavTuning,
            "params" => NavParams,
            "diagnostics" => NavDiagnostics,
            "settings" => NavSettings,
            _ => null
        };
        if (nav is not null) nav.IsChecked = true;
    }

    private void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "tuning" => new TuningPage(),
                "params" => new ParamsPage(),
                "diagnostics" => new DiagnosticsPage(),
                "settings" => new SettingsPage(),
                _ => new OverviewPage()
            };
            page.DataContext = _session;
            _pages[key] = page;
        }

        Host.Content = page;
        if (_meta.TryGetValue(key, out var meta))
        {
            PageTitle.Text = meta.Title;
            PageSub.Text = meta.Subtitle;
        }
    }

    // ---------------- 状态条 ----------------

    private void UpdateChrome()
    {
        var level = _session.StatusLevel;
        Banner.Visibility = level == StatusLevel.Ok ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = _session.StatusText;
        BannerAction.Content = _session.StatusAction;

        (Brush bg, Brush border, Brush fg) = level switch
        {
            StatusLevel.Warn => (Brush("WarnSoft"), Brush("Warn"), Brush("Warn")),
            StatusLevel.Danger => (Brush("DangerSoft"), Brush("Danger"), Brush("Danger")),
            StatusLevel.Info => (Brush("AccentSoft"), Brush("Accent"), Brush("AccentDeep")),
            _ => (Brush("BgSubtle"), Brush("Border"), Brush("TextSecondary"))
        };
        Banner.Background = bg;
        Banner.BorderBrush = border;
        BannerText.Foreground = fg;

        AlertPanel.Background = _session.AlertCount > 0 ? Brush("WarnSoft") : Brush("BgSubtle");

        DriverChip.Background = _session.DriverAvailable ? Brush("AccentSoft") : Brush("WarnSoft");
        DriverChip.BorderBrush = _session.DriverAvailable ? Brush("Border") : Brush("Warn");
        ElevChip.Background = _session.IsElevated ? Brush("BgSubtle") : Brush("WarnSoft");
        ElevChip.BorderBrush = _session.IsElevated ? Brush("Border") : Brush("Warn");
    }

    private void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        if (_session.DriverAvailable) Navigate("diagnostics");
        else Navigate("diagnostics");
        NavDiagnostics.IsChecked = true;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
