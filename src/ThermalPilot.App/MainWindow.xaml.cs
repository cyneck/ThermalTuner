using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThermalPilot.App.Services;
using ThermalPilot.App.Views;
using ThermalPilot.App.Windows;

namespace ThermalPilot.App;

public partial class MainWindow : Window
{
    private readonly SessionState _session;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private TrayService? _tray;
    private bool _reallyExit;
    private bool _hideHintShown;
    private readonly Dictionary<string, (string Title, string Subtitle)> _meta = new()
    {
        ["overview"] = ("概览", "一屏看清温度、功耗与最新决策"),
        ["tuning"] = ("智能调优", "用档位与天花板表达意图，其余交给决策树"),
        ["monitor"] = ("实时监控", "连续采样与明细，可压测、可导出"),
        ["params"] = ("高级参数", "默认只读；显式解锁后可编辑并应用"),
        ["diagnostics"] = ("诊断与日志", "环境体检、传感器清单与恢复机制"),
        ["settings"] = ("设置", "自启、采样、恢复策略与许可")
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
                _session.SetProfile(profile);
                _tray?.UpdateProfile(profile);
                _session.AddAlert("info", "已切换档位", $"托盘切换到「{_session.ProfileLabel}」。");
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
        if (!_reallyExit)
        {
            if (_session.Settings.CloseToTray && _tray is not null)
            {
                e.Cancel = true;
                Hide();
                if (!_hideHintShown)
                {
                    _hideHintShown = true;
                    _tray.ShowBalloon("ThermalPilot 仍在后台运行",
                        "采样继续，双击托盘图标可重新打开；右键菜单可退出。");
                }
                return;
            }
        }

        // 退出时恢复 MSR 原值（PRD G-05）
        if (_session.Settings.RestoreOnExit && _session.HasMsrBackup && _session.MsrWritable)
        {
            var r = _session.RestoreOriginal();
            if (!r.Ok)
                MessageBox.Show("退出前恢复原值失败：" + r.Message + "\n请手动在「高级参数」页重试。",
                    "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "tuning" => new TuningPage(),
                "monitor" => new MonitorPage(),
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
