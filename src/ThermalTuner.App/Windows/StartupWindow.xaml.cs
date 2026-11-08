using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ThermalTuner.App.Services;

namespace ThermalTuner.App.Windows;

/// <summary>
/// 启动初始化页：先给一帧界面，再去做真正耗时的检测。
///
/// 三种结局：
/// · 环境已就绪 → 直接进主界面（缓存命中时通常 &lt; 1 秒）；
/// · 仅观测     → 进主界面，但调优停用，顶部横幅说明原因；
/// · 未就绪     → 停在本文档，给出「重新检测 / 以管理员身份重启」，避免进入一个没有数据的调优界面。
/// </summary>
public partial class StartupWindow : Window
{
    private bool _running;
    private bool _entered;

    /// <summary>离屏渲染用：只画「就绪」状态，不真的去跑检测。</summary>
    internal bool PreviewMode { get; set; }

    public StartupWindow()
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? string.Empty : $"v{v.Major}.{v.Minor}.{v.Build}";

        Loaded += async (_, _) =>
        {
            if (PreviewMode) { ApplyPreview(); return; }
            StartupCoordinator.MarkSplashShown();
            await RunAsync();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_running) Application.Current?.Shutdown();
        };
    }

    /// <summary>截图用：三步全部完成、结论「环境已就绪」，不触发任何检测或写盘。</summary>
    private void ApplyPreview()
    {
        SetBusy(false);
        Headline.Text = "环境已就绪";
        HeadlineDetail.Text = "检测结果已记住，之后启动会直接进入界面。";
        var stepDone = (string)"✓";
        foreach (var d in new[] { Step0Dot, Step1Dot, Step2Dot })
        {
            d.Text = stepDone;
            d.Foreground = (Brush)FindResource("Accent");
        }
        Step0Detail.Text = string.Empty;
        Step1Detail.Text = "沿用 2026-11-08 21:20 的检测结果";
        Step2Detail.Text = "传感器在后台读取";
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        SetBusy(true);
        ResetSteps();

        StartupPlan plan;
        try
        {
            plan = await StartupCoordinator.RunAsync(new Progress<StartupProgress>(OnProgress));
        }
        catch (Exception ex)
        {
            plan = new StartupPlan(StartupMode.Blocked, null, null, false, TimeSpan.Zero,
                "环境未就绪", "检测过程出错：" + ex.Message, false, false, false);
        }

        _running = false;
        SetBusy(false);

        if (plan.CanEnter) EnterMain(plan);
        else ShowBlocked(plan);
    }

    private void OnProgress(StartupProgress p)
    {
        if (p.Index is < 0 or > 2) return;
        var dot = p.Index switch { 0 => Step0Dot, 1 => Step1Dot, _ => Step2Dot };
        var detail = p.Index switch { 0 => Step0Detail, 1 => Step1Detail, _ => Step2Detail };
        var title = p.Index switch { 0 => Step0Text, 1 => Step1Text, _ => Step2Text };

        (string glyph, Brush brush) = p.State switch
        {
            StartupStepState.Done => ("✓", (Brush)FindResource("Accent")),
            StartupStepState.Active => ("›", (Brush)FindResource("Accent")),
            StartupStepState.Failed => ("✕", (Brush)FindResource("Danger")),
            StartupStepState.Skipped => ("–", (Brush)FindResource("TextTertiary")),
            _ => ("·", (Brush)FindResource("TextTertiary"))
        };
        dot.Text = glyph;
        dot.Foreground = brush;
        title.Foreground = p.State == StartupStepState.Pending
            ? (Brush)FindResource("TextSecondary")
            : (Brush)FindResource("TextPrimary");
        if (!string.IsNullOrEmpty(p.Detail)) detail.Text = p.Detail;
    }

    private void ResetSteps()
    {
        Headline.Text = "正在准备运行环境";
        HeadlineDetail.Text = "正在确认本机硬件与可调参数，通常只需 1–2 秒。";
        HeadlineDetail.Foreground = (Brush)FindResource("TextSecondary");
        ActionPanel.Visibility = Visibility.Collapsed;
        FootHint.Text = "检测结果会被记住：只有首次运行、更换设备、升级程序或更换运行权限时才重新检测。";
        foreach (var t in new[] { Step0Detail, Step1Detail, Step2Detail }) t.Text = string.Empty;
        foreach (var d in new[] { Step0Dot, Step1Dot, Step2Dot })
        {
            d.Text = "·";
            d.Foreground = (Brush)FindResource("TextTertiary");
        }
    }

    private void SetBusy(bool busy) =>
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    private void EnterMain(StartupPlan plan)
    {
        _entered = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var session = new SessionState(plan.Telemetry, plan.Capability);
        long afterSession = sw.ElapsedMilliseconds;
        var main = new MainWindow(session);
        long afterWindow = sw.ElapsedMilliseconds;
        Application.Current.MainWindow = main;
        main.Show();
        StartupCoordinator.RecordMainWindow(
            $"会话 {afterSession} ms · 窗口 {afterWindow - afterSession} ms · 显示 {sw.ElapsedMilliseconds - afterWindow} ms");
        Close();
    }

    private void ShowBlocked(StartupPlan plan)
    {
        Headline.Text = plan.Headline;
        HeadlineDetail.Text = plan.Detail;
        HeadlineDetail.Foreground = (Brush)FindResource("Danger");
        ActionPanel.Visibility = Visibility.Visible;
        // 温度都读不到时，观测也没有意义，不给「仅查看监控」这个出口
        MonitorButton.Visibility = plan.TempReadable ? Visibility.Visible : Visibility.Collapsed;
        FootHint.Text = plan.TempReadable
            ? "也可以先只看监控，等方便时再以管理员身份重启。"
            : "修复后点「重新检测」；若刚装好程序，建议先以管理员身份运行一次完成组件安装。";
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        await RunAsync();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            // 单实例互斥：新进程起来后退出当前实例
            Application.Current?.Shutdown();
        }
        catch
        {
            // 用户取消 UAC：保持在初始化页
        }
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        // 观测模式：跳过闸门直接进主界面；能力快照由会话自己按缓存/实测补，
        // 调优入口在缺参数时由页面自身的闸门拦住，不会静默变成「能调但没数据」。
        if (_running || _entered) return;
        _entered = true;
        var session = new SessionState(null, null);
        var main = new MainWindow(session);
        Application.Current.MainWindow = main;
        main.Show();
        StartupCoordinator.RecordMainWindow();
        Close();
    }
}
