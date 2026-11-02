using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThermalPilot.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
// WinForms 与 WPF 同名类型：本文件只用 WPF 的
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using WinForms = System.Windows.Forms;

namespace ThermalPilot.App;

/// <summary>诊断结果行（界面绑定用）。</summary>
public sealed class DiagRow
{
    public string Name { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";
    public Brush StatusBrush { get; init; } = System.Windows.Media.Brushes.Gray;
}

/// <summary>行对象要能原地更新：每 2 秒重建集合会让列表闪、也会丢滚动位置。</summary>
public abstract class NotifyRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>风扇通道行（界面绑定用）。</summary>
public sealed class FanRow : NotifyRow
{
    private string _name = "";
    private string _rpmText = "--";
    private string _outText = "--";
    private string _tip = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string RpmText { get => _rpmText; set => Set(ref _rpmText, value); }
    public string OutText { get => _outText; set => Set(ref _outText, value); }
    public string Tip { get => _tip; set => Set(ref _tip, value); }
}

/// <summary>
/// 进程负载行（界面绑定用）。数值到文本的换算全在这里，
/// 核心 ProcessMonitor 只给物理量，不掺显示格式。
/// </summary>
public sealed class ProcRow : NotifyRow
{
    private string _name = "";
    private string _instancesText = "";
    private string _powerText = "--";
    private string _cpuText = "--";
    private string _memText = "--";
    private string _ioText = "--";
    private Brush _powerBrush = Brushes.White;
    private Brush _ioBrush = Brushes.White;
    private Brush _rowBg = Brushes.Transparent;
    private string _detail = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string InstancesText { get => _instancesText; set => Set(ref _instancesText, value); }
    public string PowerText { get => _powerText; set => Set(ref _powerText, value); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public string MemText { get => _memText; set => Set(ref _memText, value); }
    public string IoText { get => _ioText; set => Set(ref _ioText, value); }
    public Brush PowerBrush { get => _powerBrush; set => Set(ref _powerBrush, value); }
    public Brush IoBrush { get => _ioBrush; set => Set(ref _ioBrush, value); }
    public Brush RowBg { get => _rowBg; set => Set(ref _rowBg, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x55, 0x62, 0x72));
    private static readonly Brush Strong = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xF8));
    private static readonly Brush Hot = new SolidColorBrush(Color.FromRgb(0xF0, 0xA3, 0x3C));
    private static readonly Brush Zebra = new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x20));

    public void Fill(ProcLoad r, int index, bool powerOk)
    {
        Name = r.Name;
        InstancesText = r.Instances > 1 ? r.Instances.ToString() : "";
        // 0.00 满屏会让人以为列没工作；小于显示精度时写 "<0.01" 明确表示"非零但极小"
        PowerText = !powerOk ? "--"
                  : r.PowerW < 0.005 ? "0"
                  : r.PowerW < 0.01 ? "<0.01"
                  : r.PowerW.ToString("0.00");
        CpuText = r.CpuPct < 0.05 ? "0" : r.CpuPct.ToString("0.0");
        // 内存读不到（系统进程没权限）和"真的 0"要分开，否则会写成占用 0 MB
        MemText = r.MemMb < 0.05 ? "--" : r.MemMb.ToString("0");
        IoText = r.DiskMBps < 0.005 ? "0" : r.DiskMBps.ToString("0.00");

        // 功耗为主序，所以只要它够大就给足视觉重量
        PowerBrush = !powerOk ? Dim : r.PowerW >= 2 ? Hot : r.PowerW >= 0.3 ? Strong : Dim;
        // IO 高但 CPU 低的行按确定的优先级会被排到后面，用颜色提醒别漏
        IoBrush = r.DiskMBps >= 5 ? Hot : (r.DiskMBps >= 0.05 ? Strong : Dim);
        RowBg = index % 2 == 1 ? Zebra : Brushes.Transparent;

        string inst = r.Instances > 1 ? $"（{r.Instances} 个同名进程）" : "";
        Detail = $"{r.Name}{inst}\nPID：{r.PidText}\n"
                 + $"功耗 {PowerText} W（按 CPU 份额分摊）　CPU {CpuText}%　内存 {MemText} MB　IO {IoText} MB/s";
    }

    public void Clear()
    {
        Name = ""; InstancesText = ""; PowerText = ""; CpuText = ""; MemText = ""; IoText = ""; Detail = "";
        RowBg = Brushes.Transparent;
    }
}

public partial class MainWindow : Window
{
    private const int HistoryLength = 120;

    private readonly SettingsStore.AppSettings _settings;
    private readonly ThermalEngine _engine;
    /// <summary>
    /// 参数中心：全局配置的唯一真源。所有面板改的都是它的草稿（Draft），
    /// 只有点「应用」才会进 Committed 并写进硬件。见 ParameterCenter 的类注释。
    /// </summary>
    private readonly ParameterCenter _pc;
    private readonly DispatcherTimer _uiTimer;
    /// <summary>
    /// 编辑防抖：输入框每敲一个字符都写草稿会让「待应用」表和配置档表反复重建，
    /// 所以文本类编辑等 350 ms 静默后再落草稿；勾选框/下拉立即落。
    /// </summary>
    private readonly DispatcherTimer _draftDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    /// <summary>不参与草稿捕获的输入框（纯展示/路径类）。</summary>
    private static readonly string[] NonParamBoxes = { "ReportBox", "LogBox", "LogPathText", "CpuStatusText", "DiagList", "CpuCapDetail" };

    /// <summary>
    /// 用户手改过 PL1/PL2/τ/EPP 输入框（预设套用、「读取当前值」不算）。
    /// 提交时凭它决定智能自动是否让位 —— 手调必须赢得自动。
    /// </summary>
    private bool _cpuHandEdited;
    private readonly ObservableCollection<CurvePoint> _fanPoints = new();
    private readonly ObservableCollection<CurvePoint> _freqPoints = new();
    private readonly ObservableCollection<FanRow> _fanRows = new();
    private readonly ObservableCollection<DiagRow> _diagRows = new();

    /// <summary>进程负载采样器：2 秒一次，在后台线程跑（枚举 200 个进程约 5–10 ms，够快但别放 UI 线程）。</summary>
    private readonly ProcessMonitor _procMon = new();
    private readonly ObservableCollection<ProcRow> _procRows = new();
    private DateTime _lastProcSample = DateTime.MinValue;
    private int _procBusy;
    /// <summary>配置档表格行（基线 / 当前值 / 约束 / 来源）。</summary>
    private readonly ObservableCollection<ProfileParamRow> _profileRows = new();
    private bool _turboProbed;

    private readonly double[] _cpuHist = EmptyHistory();
    private readonly double[] _gpuHist = EmptyHistory();
    private readonly double[] _fanHist = EmptyHistory();
    private readonly double[] _procHist = EmptyHistory();

    private CalibrationEngine? _calib;
    private CancellationTokenSource? _cts;
    private CalibrationResult? _lastResult;
    private string _calibPreset = "均衡";
    private string _calibLevel = "标准";
    private double _stageBase;
    private int _stageCount = 1;

    private bool _updating;
    private bool _ready;
    private bool _exiting;
    private bool _cleaned;
    private WinForms.NotifyIcon? _tray;

    private static double[] EmptyHistory() => Enumerable.Repeat(double.NaN, HistoryLength).ToArray();

    public MainWindow()
    {
        InitializeComponent();

        // 界面自检专用：把平台能力替换成伪造值，用于验证「非 Intel 平台禁用高级参数」
        if (App.SmokeTest && !string.IsNullOrEmpty(App.SmokeCaps))
            CpuCapabilities.Override(CpuCapabilities.Fake(App.SmokeCaps));

        _settings = SettingsStore.Load();
        _pc = new ParameterCenter(_settings.Config);
        _pc.DraftChanged += RefreshPending;
        _engine = new ThermalEngine(_pc.Committed);

        ProfileCombo.ItemsSource = Presets.Names;
        LevelCombo.ItemsSource = CalibrationPlans.Levels;
        LevelCombo.SelectedItem = _settings.CalibrationLevel ?? "标准";
        _calibLevel = LevelCombo.SelectedItem as string ?? "标准";
        _calibPreset = _settings.CalibrationPreset ?? "均衡";

        FanGrid.ItemsSource = _fanPoints;
        FreqGrid.ItemsSource = _freqPoints;
        FanList.ItemsSource = _fanRows;
        ProcList.ItemsSource = _procRows;
        DiagList.ItemsSource = _diagRows;
        ProfileList.ItemsSource = _profileRows;

        // 平台功率必须在显示之前探一次：界面上的「PL1 ≤ 45」这类提示、配置档表格里的约束文案，
        // 都直接读 PlatformPower 的生效值。不先探就会显示成内置默认（45W），看起来像"没识别出来"。
        PlatformPower.Apply(_pc.Committed, null, force: true);

        // 用 Committed 而不是磁盘里那份：能力清洗（本机没有的旋钮清值）已经作用在 Committed 上，
        // 直接显示 _settings.Config 会出现"界面写着 EPP=128、控件却是灰的、实际不会下发"的错位。
        LoadConfigToUi(_pc.Committed);

        FanGrid.CellEditEnding += Grid_CellEditEnding;
        FreqGrid.CellEditEnding += Grid_CellEditEnding;

        Log.LineWritten += OnLogLine;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        // 编辑即入草稿：所有输入控件统一挂事件（真正挂载在 Loaded，那时视觉树才建好）。
        // 之前只有「① 策略」页的 4 个框挂了事件，高级页的 PL1/EPP/τ/降压 等改了草稿根本不更新，
        // 底部「应用（写入硬件）」因此永远是灰的 —— 用户只能去点那些名不副实的子按钮。
        _draftDebounce.Tick += (_, _) => { _draftDebounce.Stop(); CaptureDraftFromUi(); };

        _ready = true;
        UpdateLevelHint();
        RefreshPending();
        RefreshStrategyHints();
        ShowValidation(_pc.Validate());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WireDraftCapture();   // 视觉树此时才建好

        if (App.SmokeTest)
        {
            AdminText.Text = "界面自检模式 · 未接管硬件";
            StatusText.Text = "自检通过即退出";

            switch (App.SmokeNav)
            {
                case "strategy": NavStrategy.IsChecked = true; break;
                case "calib": NavCalib.IsChecked = true; break;
                case "advanced": NavAdvanced.IsChecked = true; ExpandAllAdvanced(); break;
                case "diag": NavDiag.IsChecked = true; UpdateDiagnostics(silent: true); break;
            }

            // 仪表盘要多等一会儿：进程榜第一次采样只建基线，第二个采样点（约 2.4s）才有数
            bool dashboard = string.IsNullOrEmpty(App.SmokeNav) || App.SmokeNav == "dash";
            await Task.Delay(dashboard ? 3400 : 700);
            if (App.SmokeScroll > 0)
            {
                PanelAdvanced.ScrollToVerticalOffset(App.SmokeScroll);
                await Task.Delay(500);
            }
            await Task.Delay(500);
            try
            {
                if (!string.IsNullOrEmpty(App.SmokeShotPath)) CapturePng(App.SmokeShotPath!);
                Log.Info("界面自检完成。");
            }
            catch (Exception ex)
            {
                Log.Error("界面自检截图失败", ex);
            }
            System.Windows.Application.Current.Shutdown();
            return;
        }

        AdminText.Text = UndervoltService.IsElevated
            ? "管理员模式"
            : "非管理员运行 · 风扇/降压不可用";

        SetupTray();
        await StartEngineAsync();

        if (_settings.StartMinimized && _settings.MinimizeToTray)
            Hide();
    }

    private void ExpandAllAdvanced()
    {
        // 用逻辑子元素而不是可视化树：自检时窗口还没渲染，VisualTree 里拿不到 Expander
        try
        {
            foreach (var child in AdvancedStack.Children)
                if (child is Expander ex) ex.IsExpanded = true;
        }
        catch { }
        try
        {
            foreach (var ex in FindVisualChildren<Expander>(PanelAdvanced)) ex.IsExpanded = true;
        }
        catch { }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    private async Task StartEngineAsync()
    {
        // 启动前先把草稿落地：否则引擎会按上次应用的旧值跑，用户会以为改动没生效
        if (_pc.HasPending && !ApplyDraft(announce: false))
        {
            StatusText.Text = "启动被拦住：参数未通过机理校验";
            ActionText.Text = "有阻断级参数，请到「① 策略」页看校验原因后再启动";
            NavStrategy.IsChecked = true;
            return;
        }

        BtnRun.IsEnabled = false;
        StatusText.Text = "正在初始化硬件监控…";

        var notes = await Task.Run(() => _engine.Start());

        // 引擎起来后 Ring0 才可用 —— 这时候的能力判定才是最终的（能读到 0x610/0x773/0x1AD 了）
        CpuCapabilities.Refresh(_engine.Cpu.Msr);
        RefreshCpuCapsUi();

        // 平台功率也在这时候才拿得到 MSR 0x614 —— 引擎启动前只能靠型号表。
        PlatformPower.Apply(_pc.Committed, _engine.Cpu.Msr, force: true);
        RefreshPowerUi();
        RefreshProfileTable();
        RefreshPending();

        BtnRun.IsEnabled = true;
        BtnRun.Content = "■ 停止";
        BtnPause.IsEnabled = true;
        BtnPause.Content = "暂停";

        foreach (var n in notes)
        {
            Log.Info("启动提示：" + n);
            ActionText.Text = n;
        }

        UpdateDiagnostics(silent: true);
    }

    // ---------------- UI 刷新 ----------------

    private void RefreshUi()
    {
        var st = _engine.State;
        var s = st.Snapshot;

        // 温度一律显示抗毛刺后的值：原始读数约 6% 的样本是单点离群值（真机 43/704），
        // 峰值能假到 85~96℃。被平滑掉的这一拍在副标题里标「已平滑」，并可在悬浮里看原始值。
        CpuTempText.Text = s.CpuTempValid ? s.CpuTempShown.ToString("F1") : "--";
        var cpuBits = new List<string> { $"负载 {s.CpuLoad:F0}%" };
        if (s.CpuPower > 0) cpuBits.Add($"{s.CpuPower:F0}W");
        if (s.CpuVoltage > 0.2) cpuBits.Add($"{s.CpuVoltage:F2}V");
        // 不在这里加"已平滑"标记：副标题只有一行、卡片宽度有限，多三个字会被裁掉。
        // 本拍原始值放在悬浮提示里（见下面的 ToolTip），需要追溯时悬停即可。
        CpuSubText.Text = string.Join(" · ", cpuBits);
        GpuTempText.Text = s.GpuTempValid ? s.GpuTempShown.ToString("F1") : "--";
        // 副标题带上热点：主数字是核心温度（与 OEM 工具同口径），热点只作参考。
        // 两者差 8~12℃ 是正常的（热点是 die 上最热的一点），不是传感器坏了。
        GpuSubText.Text = s.GpuTempValid
            ? $"负载 {s.GpuLoad:F0}%" + (s.GpuHotSpotTemp > 0 ? $"　·　热点 {s.GpuHotSpotTemp:F0}℃" : "")
            : "未检测到独显";
        GpuSubText.ToolTip = s.GpuTempValid
            ? $"核心 {s.GpuTempShown:F0}℃ / 热点 {(s.GpuHotSpotTemp > 0 ? s.GpuHotSpotTemp.ToString("F0") + "℃" : "—")}"
              + Environment.NewLine
              + "大数字取核心温度（与任务管理器、OMEN Hub 同口径）；热点通常高 8~12℃，只在负载高时才有参考意义。"
            : null;

        // CPU 温度同理：大数字是 Package（抗毛刺后），原始值/最热单核放进悬浮
        CpuSubText.ToolTip = s.CpuTempValid
            ? $"Package {s.CpuTempShown:F0}℃"
              + (Math.Abs(s.CpuTemp - s.CpuTempShown) >= 1 ? $"（原始 {s.CpuTemp:F0}℃）" : "")
              + (s.CpuCoreMaxTemp > 0 ? $" / 最热核心 {s.CpuCoreMaxTemp:F0}℃" : "")
              + (s.CpuTempSuppressed ? "　·　本拍原始读数按毛刺剔除" : "")
              + Environment.NewLine
              + "大数字取 CPU Package，与任务管理器/OMEN Hub 同口径；最热单核通常高 1~3℃。"
              + Environment.NewLine
              + "已做 5 点中值抗毛刺：传感器偶发单点跳高 20~40℃（功率却不变，物理上不成立），"
              + "直接显示会误触发超温保护。稳态不受影响，真实升温/降温约滞后 1~2 秒。"
            : null;

        RamText.Text = s.RamPct > 0 ? s.RamPct.ToString("F0") : "--";
        RamSubText.Text = s.RamTotalMb > 0
            ? $"{s.RamUsedMb / 1024:F1} / {s.RamTotalMb / 1024:F1} GB"
            : "读不到内存状态";
        RamSubText.ToolTip = s.RamTotalMb > 0
            ? $"物理内存：已用 {s.RamUsedMb:F0}MB / 共 {s.RamTotalMb:F0}MB（{s.RamPct:F0}%）"
            : null;
        // 主数字 = 「当前频率」，与任务管理器「性能 → CPU → 速度」同口径。
        //   任务管理器的 Speed 来自 Processor Frequency 计数器 = %Processor Performance × 最高频率，
        //   而 %Processor Performance 是 APERF/MPERF 的比率 —— MPERF 只在 C0 推进，
        //   所以它就是「醒着时的平均频率」，即这里的 C0Mhz（跨核汇总，与 _Total 一致）。
        //   旧版主数字用的是「有效频率」（ΔAPERF / 墙钟时间，把休眠也摊进去）：
        //   空闲 3% 负载时给 1164MHz，任务管理器同一时刻 3.59GHz，差 3 倍 → 看起来就是算错了。
        //   两个数都是对的，只是回答不同问题：当前频率 = "跑多快"，有效频率 = "实际交付了多少"。
        var pf = _engine.Perf.Last;
        bool effOk = pf is { Valid: true, EffectiveMhz: > 200 };
        bool curOk = pf is { Valid: true, C0Mhz: > 200 };
        double curShowMhz = curOk ? pf!.C0Mhz : s.CpuFrequencyMhz;
        FreqText.Text = curShowMhz > 0 ? curShowMhz.ToString("F0") : "--";
        if (FreqTitleText != null)
            FreqTitleText.Text = "CPU 频率";

        // 副标题：把「有效频率」和限制原因并排放，回答"为什么是这个频率"
        string limitTxt = !string.IsNullOrEmpty(st.BindLimitText) ? st.BindLimitText : "限制源：—";
        string limitShort = limitTxt.Split('（')[0].Trim();   // "轻载（需求低于任何限制）" → "轻载"
        FreqSubText.Text = effOk
            ? $"有效 {pf!.EffectiveMhz:F0}MHz · {limitShort}"
            : (s.CpuFrequencyMhz > 0 ? $"瞬时倍频 {s.CpuFrequencyMhz:F0}MHz · {limitShort}" : limitShort);

        // 三个频率口径必须一次讲清，否则"1164 还是 3587"永远会再问一遍
        FreqSubText.ToolTip = (curShowMhz > 0
                                  ? $"当前 {curShowMhz:F0}MHz —— 与任务管理器「速度」同口径（只有核心醒着时才统计），"
                                    + "空闲时也会接近睿频，这是正常的"
                                  : "当前频率不可用")
                             + Environment.NewLine
                             + (effOk
                                 ? $"　·　有效 {pf!.EffectiveMhz:F0}MHz —— ΔAPERF / 墙钟时间，休眠时间也算在分母里。"
                                   + "低负载时它远低于当前频率，说明大部分时间没有在跑，不是被降频。"
                                 : $"　·　有效频率本轮不可用：{pf?.EffNote}")
                             + Environment.NewLine
                             + $"　·　瞬时倍频 {s.CpuFrequencyMhz:F0}MHz（内核请求的 P-state）"
                             + $" / 芯片档位 单核 {(pf?.TurboLimit1CoreMhz ?? 0):F0} · 全核 {(pf?.TurboLimitMhz ?? 0):F0}MHz"
                             + Environment.NewLine
                             + $"限制：{limitTxt}"
                             + (string.IsNullOrEmpty(pf?.Evidence) ? "" : Environment.NewLine + "依据：" + pf!.Evidence);

        // 风扇只显示一个数，绝不把"回读的占空比"和"下发指令"并列：
        //   ① 有真转速传感器（台式机 SuperIO / 部分笔记本 EC）→ 显示 RPM，副标题带指令值
        //   ② 只有 NBFC 后端 → 显示我们下发的占空比（NBFC 的"回读"其实就是它上次写入值，
        //      与本程序的指令同义，不会随负载自己动；旧版并列两格被用户当成"实测不动"的 bug）
        //   ③ 都没有 → --，并说明本机风扇由 EC 固件管理
        int rpm = s.FanRpm;
        var hws = _engine.Hardware;
        bool fanHw = hws.FanControlSupported;
        double cmdPct = st.AppliedFanPct;

        if (rpm > 0)
        {
            FanTitleText.Text = "风扇转速";
            FanText.Text = rpm.ToString();
            FanUnitText.Text = " RPM";
            FanSubText.Text = cmdPct >= 0 ? $"实测 · 指令 {cmdPct:0}%" : $"实测 · {s.Fans.Count} 个通道";
        }
        else if (cmdPct >= 0)
        {
            FanTitleText.Text = "风扇占空比";
            FanText.Text = cmdPct.ToString("F0");
            FanUnitText.Text = " %";
            // 只有 NBFC 回读确认 Auto=off 且 Target≈请求值时，才会走到这里 ——
            // 也就是说这个数**已经确认落到风扇后端**，不是"我们打算下发多少"。
            FanSubText.Text = _engine.IsRunning && !st.Paused
                ? $"已确认生效 · 本机无转速传感器"
                : "引擎未接管 · 此值未生效（BIOS 自管）";
            FanSubText.Foreground = (Brush)FindResource(_engine.IsRunning && !st.Paused ? "Ok" : "Muted");
            FanSubText.ToolTip = "这是**下发值**（占空比），不是转速：本机 EC 不提供 FG 测速信号。"
                               + Environment.NewLine
                               + "它已经过回读校验：NBFC 的 Auto control 必须为关、Target 与请求一致，否则会显示「未生效」。";
        }
        else
        {
            FanTitleText.Text = "风扇";
            FanText.Text = "--";
            FanUnitText.Text = " %";
            // 有明确失败原因就照实说 —— 旧版这里只写"尚无下发值"，用户只能靠听声音猜
            bool hasWhy = !string.IsNullOrEmpty(st.FanNote) && _engine.IsRunning;
            FanSubText.Text = hasWhy ? st.FanNote
                : fanHw ? "尚无下发值（引擎未接管）" : "EC 固件管理，不可读也不可控";
            FanSubText.Foreground = (Brush)FindResource(hasWhy ? "Warn" : "Muted");
            FanSubText.ToolTip = hasWhy
                ? "指令发出去了但风扇仍由 NBFC 自动曲线控制，所以你听到的声音来自 BIOS/NBFC，"
                  + "与界面上的数字无关。" + Environment.NewLine + Environment.NewLine + st.FanNote
                : null;
        }

        StatusText.Text = st.Status;
        if (!string.IsNullOrEmpty(st.LastAction)) ActionText.Text = st.LastAction;

        // CPU 精细调控 / 智能自动 的实时状态
        if (ChkAutoCpu is { IsChecked: true })
            AutoStatusText.Text = string.IsNullOrEmpty(st.CpuHint) ? "自动已启用，等待首个采样…" : st.CpuHint;
        if (!string.IsNullOrEmpty(st.PerfText)) PerfStatusText.Text = st.PerfText;
        if (!string.IsNullOrEmpty(st.CpuHint))
            CpuStatusText.Text = st.CpuHint;

        // 在线估出的热阻喂给参数校验：这样"PL1 设多大"能立刻给出预测稳态温度
        double rEst = _engine.AutoController.REstimate;
        if (!double.IsNaN(rEst) && rEst > 0.05)
        {
            _pc.REstimate = rEst;
            _pc.RConfident = _engine.AutoController.RConfident;
        }
        StatusDot.Fill = new SolidColorBrush(
            st.Faulted ? Color.FromRgb(0xF0, 0x5B, 0x5B)
            : !_engine.IsRunning ? Color.FromRgb(0x7D, 0x8A, 0x9A)
            : st.Paused ? Color.FromRgb(0xF0, 0xA3, 0x3C)
            : Color.FromRgb(0x5A, 0xC8, 0xFA));

        UpdateFanRows(s);
        // 进程榜只在仪表盘可见时才跑，省得在别的页白白枚举两百个进程
        if (PanelDashboard.Visibility == Visibility.Visible) TickProcesses(s);

        if (_engine.IsRunning && s.CpuTempValid)
        {
            // 曲线也画抗毛刺后的值，否则图上全是 20~40℃ 的假尖刺，看不出真实趋势。
            Push(_cpuHist, s.CpuTempShown);
            Push(_gpuHist, s.GpuTempValid ? s.GpuTempShown : double.NaN);
            Push(_fanHist, st.AppliedFanPct >= 0 ? st.AppliedFanPct : 0);
            Push(_procHist, st.AppliedProcMax > 0 ? st.AppliedProcMax : 100);
        }

        TempChart.SetData(20, 105, "°C",
            new ChartSeries { Name = "CPU", Color = Color.FromRgb(0x5A, 0xC8, 0xFA), Values = _cpuHist },
            new ChartSeries { Name = "GPU", Color = Color.FromRgb(0xF0, 0xA3, 0x3C), Values = _gpuHist });

        CtrlChart.SetData(0, 100, "%",
            new ChartSeries { Name = "风扇", Color = Color.FromRgb(0x7E, 0xD3, 0x8B), Values = _fanHist },
            new ChartSeries { Name = "频率上限", Color = Color.FromRgb(0xB1, 0x8C, 0xFA), Values = _procHist, Dashed = true });
    }

    private void UpdateFanRows(SensorSnapshot s)
    {
        if (s.Fans.Count == 0)
        {
            // 不放占位行：一行「-- --」看着像"有通道但读不到"，实际是硬件没有这个通道
            if (_fanRows.Count > 0) _fanRows.Clear();
            FanEmptyText.Text = "本机 EC 固件不提供风扇转速与占空比通道。\n"
                                + "风扇由 BIOS 自管，程序只能靠降频控制温度。\n"
                                + "装 NBFC 并选好配置后可接管（见「诊断」页）。";
            FanEmptyText.Visibility = Visibility.Visible;
            return;
        }

        for (int i = 0; i < s.Fans.Count; i++)
        {
            var f = s.Fans[i];
            var row = i < _fanRows.Count ? _fanRows[i] : null;
            if (row == null)
            {
                row = new FanRow();
                _fanRows.Add(row);
            }
            row.Name = f.Name;
            row.RpmText = f.Rpm > 0 ? f.Rpm.ToString() : "--";
            row.OutText = f.OutputPct.HasValue ? f.OutputPct.Value.ToString("F0") + "%" : "--";
            row.Tip = $"{f.Name}\n来源：{f.Source}\n转速：{(f.Rpm > 0 ? f.Rpm + " RPM" : "不可读")}"
                      + $"\n输出：{(f.OutputPct.HasValue ? f.OutputPct.Value.ToString("F0") + "%" : "不可读")}"
                      + $"\n可调速：{(f.Controllable ? "是" : "否（EC 固件接管）")}";
        }
        while (_fanRows.Count > s.Fans.Count) _fanRows.RemoveAt(_fanRows.Count - 1);
        FanEmptyText.Visibility = _fanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 进程 Top 10 ----------------

    /// <summary>
    /// 每 2 秒在后台线程采一次进程负载，采完回 UI 线程原地更新行对象。
    /// 包功率优先取 RAPL（真实测量），拿不到再退 LHM 的 CPU 功耗，都没有就 0
    /// （此时功耗列显示 --，排序自动退化为按 CPU）。
    /// </summary>
    private void TickProcesses(SensorSnapshot s)
    {
        if ((DateTime.UtcNow - _lastProcSample).TotalSeconds < 2) return;
        if (Interlocked.CompareExchange(ref _procBusy, 1, 0) != 0) return;
        _lastProcSample = DateTime.UtcNow;

        double pkg = _engine.Perf.Last?.PackagePowerW ?? 0;
        if (pkg <= 0.5 && s.CpuPower > 0.5) pkg = s.CpuPower;
        // 界面自检跑在非管理员下，读不到 RAPL → 给个假的包功率只是为了看渲染效果，
        // 真实运行永远用真实值（这条只在 --smoke 下生效）
        if (pkg <= 0.5 && App.SmokeTest) pkg = 45;

        _ = Task.Run(() =>
        {
            ProcSummary? sum = null;
            try { sum = _procMon.Sample(pkg, 10); }
            catch (Exception ex) { Log.Warn("进程采样失败：" + ex.Message); }
            finally { Interlocked.Exchange(ref _procBusy, 0); }

            if (sum == null) return;
            try
            {
                Dispatcher.BeginInvoke(() => ApplyProcessRows(sum));
            }
            catch { }
        });
    }

    private void ApplyProcessRows(ProcSummary sum)
    {
        if (ProcList == null) return;

        // 第一次只建基线：这时候还没有增量，说清楚比空表好
        if (sum.ProcessCount == 0)
        {
            ProcEmptyText.Text = "正在建立基线，2 秒后出结果…";
            ProcEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var rows = sum.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = i < _procRows.Count ? _procRows[i] : null;
            if (row == null) { row = new ProcRow(); _procRows.Add(row); }
            row.Fill(rows[i], i, sum.PowerAvailable);
        }
        // 行数变少时不要 RemoveAt 到空（会造成重建），改成把多余行清空并收缩
        while (_procRows.Count > rows.Count) _procRows.RemoveAt(_procRows.Count - 1);

        ProcEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcEmptyText.Text = rows.Count == 0 ? "当前没有可统计的进程（全部空闲）" : "";

        ProcSumText.Text = sum.PowerAvailable
            ? $"分摊合计 {sum.TotalPowerW:F1} W · CPU {sum.TotalCpuPct:F0}% · {sum.ProcessCount} 个进程"
            : $"合计 CPU {sum.TotalCpuPct:F0}% · {sum.ProcessCount} 个进程 · 功耗列需管理员（MSR）";
    }

    private static void Push(double[] arr, double value)
    {
        Array.Copy(arr, 1, arr, 0, arr.Length - 1);
        arr[^1] = value;
    }

    private void OnLogLine(string line)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[^100_000..];
                LogBox.ScrollToEnd();
            });
        }
        catch { }
    }

    // ---------------- 导航 ----------------

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (PanelDashboard == null) return;
        PanelDashboard.Visibility = NavDash.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelStrategy.Visibility = NavStrategy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelCalibration.Visibility = NavCalib.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAdvanced.Visibility = NavAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDiag.Visibility = NavDiag.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavDiag.IsChecked == true && _diagRows.Count == 0) UpdateDiagnostics(silent: true);
        if (NavStrategy.IsChecked == true) { RefreshPending(); ShowValidation(_pc.Validate()); RefreshStrategyHints(); }
    }

    /// <summary>
    /// 取向 chip：显示「基础取向」以及它是否被手改过（静音 / 静音 · 自定义）。
    /// 明细（哪几项从多少改到多少）放进悬浮提示，不占版面。
    /// </summary>
    private void UpdateProfileChip()
    {
        if (ProfileChip == null) return;

        var c = _pc.Committed;
        bool custom = ProfileBinding.IsCustom(c);
        ProfileChip.Text = (custom ? "◐ " : "● ") + ProfileBinding.DisplayName(c);
        ProfileChip.Foreground = (Brush)FindResource(custom ? "Warn" : "Accent");

        // 悬浮提示回答两个问题：改了什么（基线→现在），以及"重启还在吗"
        var inst = _settings.Profile;
        string detail = inst != null && inst.IsCustom
            ? inst.Describe(c)
            : ProfileBinding.Describe(c)
              + Environment.NewLine + Environment.NewLine + "增量随「应用」一起写入 settings.json，重启后仍然保留。";
        ProfileChip.ToolTip = "当前**已应用**的全局取向。要改请到「① 策略」页，"
            + "参数明细见「③ 高级 → ⓪ 当前配置档」。"
            + Environment.NewLine + Environment.NewLine + detail;
    }

    /// <summary>策略页的派生值说明：让用户看见「选了取向之后到底变了什么」。</summary>
    private void RefreshStrategyHints()
    {
        if (StrategyDerivedText == null) return;

        var d = _pc.Draft;
        var overrides = ProfileBinding.Overrides(d);

        StrategyDerivedText.Text = $"草稿：{ProfileBinding.DisplayName(d)}　"
            + $"上限 {d.AutoTargetC:0}℃ · 风扇 {d.FanMinPct:0}–{d.FanMaxPct:0}% "
            + $"· EPP {(d.CpuEpp >= 0 ? d.CpuEpp.ToString() : "不改")} · τ {(d.CpuTauS > 0 ? d.CpuTauS + "s" : "不改")}";

        if (overrides.Count > 0)
            StrategyDerivedText.Text += Environment.NewLine
                + "自定义项：" + string.Join("、", overrides.Select(x => $"{x.Label} {x.Preset}→{x.Actual}"))
                + "（点「回到预设」清空）";

        StrategyDerivedText.Foreground = (Brush)FindResource(overrides.Count > 0 ? "Warn" : "Accent");

        // 功率预算：(上限 − 室温) / R。
        // 注意 R 已经是「结到环境」的**总**热阻（由 T = a + R·P 拟合而来），
        // 旧版再加一个 R2=0.30 属于重复计入，会把预算算小 20%~25%。
        double r = _pc.REstimate;
        bool rOk = !double.IsNaN(r) && r > 0.05;
        double budget = rOk ? (d.AutoTargetC - d.AmbientC) / r : double.NaN;
        BudgetText.Text = rOk
            ? $"功率预算 ≈ {budget:0.0} W　（(上限 {d.AutoTargetC:0}℃ − 室温 {d.AmbientC:0}℃) / R {r:0.00} K/W）"
            : "功率预算：待热阻估计（跑一段满载后自动给出）";
        BudgetText.ToolTip = "这个数才是「能放出多少性能」的上界：在给定温度上限下，散热系统能长期带走的功率。"
                           + Environment.NewLine + "R 来自 T = a + R·P 的斜率拟合（只用满载准稳态样本），已包含 die→散热器→环境全链路。"
                           + Environment.NewLine + "把温度上限压低，它会同步变小；换硅脂/相变片后 R 变小，它会变大。";
        BudgetText.Foreground = budget < 12
            ? (Brush)FindResource("Danger")
            : budget < 25 ? (Brush)FindResource("Warn") : (Brush)FindResource("Ok");

        if (AdvProfileText != null)
        {
            string name = ProfileBinding.DisplayName(d);
            AdvProfileText.Text = $"全局取向「{name}」· 上限 {d.AutoTargetC:0}℃ · 室温 {d.AmbientC:0}℃";
            AdvProfileText.ToolTip = ProfileBinding.Describe(d)
                + Environment.NewLine + Environment.NewLine
                + "取向、温度上限、室温在「① 策略」页统一设置（全局参数只允许一处修改）。";
        }

        UpdateProfileChip();
    }

    /// <summary>策略页里任一全局边界改动 → 只进草稿。</summary>
    private void GlobalParam_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        CaptureDraftFromUi(announce: false);
        RefreshStrategyHints();
        ShowValidation(_pc.Validate());
    }

    // ---------------- 运行控制 ----------------

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            await Task.Run(() => _engine.Stop());
            BtnRun.Content = "▶ 启动";
            BtnPause.IsEnabled = false;
            Log.Info("已停止并还原系统默认。");
        }
        else
        {
            await StartEngineAsync();
        }
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsPaused)
        {
            _engine.Resume();
            BtnPause.Content = "暂停";
        }
        else
        {
            _engine.Pause();
            BtnPause.Content = "继续";
        }
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        BtnRun.Content = "▶ 启动";
        ActionText.Text = "已还原 BIOS 风扇策略、电源方案与电压偏移";
        Log.Info("用户手动还原系统默认。");
    }

    // ---------------- 环境检测 ----------------

    private void BtnCheck_Click(object sender, RoutedEventArgs e)
    {
        NavDiag.IsChecked = true;
        UpdateDiagnostics(silent: false);
    }

    private void UpdateDiagnostics(bool silent)
    {
        StatusText.Text = "正在检测本机环境…";
        var hw = _engine.Hardware;
        var power = _engine.Power;
        var uv = _engine.Undervolt;

        Task.Run(() =>
        {
            var items = EnvironmentCheck.Run(hw, power, uv, _engine.Cpu);
            var report = SupportReport.Build(hw, power, uv, _engine.Config, _engine.Cpu, _settings.Profile);
            Dispatcher.Invoke(() =>
            {
                _diagRows.Clear();
                foreach (var it in items)
                {
                    _diagRows.Add(new DiagRow
                    {
                        Name = it.Name,
                        Detail = it.Detail,
                        Advice = it.Advice,
                        StatusText = it.Status switch
                        {
                            DiagStatus.Ok => "符合",
                            DiagStatus.Warn => "提示",
                            _ => "不符合"
                        },
                        StatusBrush = it.Status switch
                        {
                            DiagStatus.Ok => new SolidColorBrush(Color.FromRgb(0x6E, 0xD3, 0x8C)),
                            DiagStatus.Warn => new SolidColorBrush(Color.FromRgb(0xF0, 0xA3, 0x3C)),
                            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0x5B, 0x5B))
                        }
                    });
                }

                ReportBox.Text = report;
                int bad = items.Count(i => i.Status == DiagStatus.Fail);
                int warn = items.Count(i => i.Status == DiagStatus.Warn);
                StatusText.Text = bad > 0 ? $"检测完成：{bad} 项不符合" : "检测完成：核心能力可用";
                if (!silent)
                {
                    ActionText.Text = $"检测完成：{bad} 项不符合、{warn} 项提示，详见右侧诊断列表";
                    Log.Info($"环境检测完成：失败 {bad} 项，提示 {warn} 项。");
                }
            });
        });
    }

    // ---------------- 自动标定 ----------------

    private void LevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelCombo.SelectedItem is string s) _calibLevel = s;
        UpdateLevelHint();
    }

    private void Pref_Checked(object sender, RoutedEventArgs e) { }

    private void UpdateLevelHint()
    {
        int total = CalibrationPlans.TotalSeconds(CalibrationPlans.Build(_calibLevel));
        if (LevelHint != null)
            LevelHint.Text = $"约 {total / 60} 分 {total % 60} 秒，期间请勿高负载办公（会自动满载压测）";
    }

    private async void BtnCalibStart_Click(object sender, RoutedEventArgs e)
    {
        if (_calib is { IsRunning: true }) return;

        if (!_engine.IsRunning)
        {
            System.Windows.MessageBox.Show(this, "请先启动引擎（右上角「启动」），标定需要读取传感器与电源方案。",
                "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var plan = CalibrationPlans.Build(_calibLevel);
        var pref = CalibrationPreference.Create(_calibPreset);

        _cts = new CancellationTokenSource();
        _calib = new CalibrationEngine();
        _stageCount = plan.Count;
        _stageBase = 0;
        CalibProgress.Value = 0;
        CalibSummary.Text = "标定进行中…";
        CalibNotes.Items.Clear();
        BtnCalibStart.IsEnabled = false;
        BtnCalibStop.IsEnabled = true;
        BtnApplyCalib.IsEnabled = false;

        _calib.StageChanged += (name, i, total) => Dispatcher.Invoke(() =>
        {
            CalibStageText.Text = $"阶段 {i}/{total}：{name}";
            _stageBase = (double)(i - 1) / total;
            CalibGpuHint.Text = name.Contains("GPU") ? StressTest.ExternalGpuHint : "";
        });

        _calib.Tick += (sec, totalSec) => Dispatcher.Invoke(() =>
        {
            CalibDetailText.Text = $"已采样 {sec}/{totalSec} 秒";
            CalibProgress.Value = (_stageBase + (double)sec / totalSec / _stageCount) * 100;
        });

        _calib.Message += msg => Dispatcher.Invoke(() => Log.Info("标定：" + msg));
        _calib.Sampled += _ => { };

        try
        {
            var result = await _calib.RunAsync(_engine, plan, pref, _cts.Token);
            _lastResult = result;

            CalibSummary.Text = result.Summary;
            CalibNotes.Items.Clear();
            foreach (var n in result.Notes) CalibNotes.Items.Add("· " + n);
            CalibFanGrid.ItemsSource = result.FanCurve.Points;
            CalibFreqGrid.ItemsSource = result.FreqCurve.Points;
            CalibCsvText.Text = string.IsNullOrEmpty(result.CsvPath) ? "" : Path.GetFileName(result.CsvPath);
            BtnApplyCalib.IsEnabled = result.Success;
            CalibProgress.Value = 100;
            CalibStageText.Text = "标定完成";
            CalibGpuHint.Text = "";
            Log.Info("标定完成：" + result.Summary);

            if (_engine.IsPaused) _engine.Resume();
            UpdateDiagnostics(silent: true);
        }
        catch (OperationCanceledException)
        {
            CalibStageText.Text = "标定已取消";
            Log.Warn("标定被用户取消。");
            if (_engine.IsPaused) _engine.Resume();
        }
        catch (Exception ex)
        {
            CalibStageText.Text = "标定失败：" + ex.Message;
            Log.Error("标定失败", ex);
            System.Windows.MessageBox.Show(this, "标定失败：" + ex.Message, "ThermalPilot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            if (_engine.IsPaused) _engine.Resume();
        }
        finally
        {
            BtnCalibStart.IsEnabled = true;
            BtnCalibStop.IsEnabled = false;
        }
    }

    private void BtnCalibStop_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        BtnCalibStop.IsEnabled = false;
    }

    private void BtnApplyCalib_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult == null) return;

        // 标定结果是「预览」：先放进草稿，再走统一的应用流程（含机理校验）
        _pc.Edit(cfg =>
        {
            cfg.FanCurve = _lastResult.FanCurve.Clone();
            cfg.FreqCurve = _lastResult.FreqCurve.Clone();
            if (_lastResult.Model.CapValid || _lastResult.Model.FanValid)
                cfg.TargetTempC = CalibrationPreference.Create(_calibPreset).TargetTemp;
            cfg.LastCalibrationNote =
                $"{DateTime.Now:MM-dd HH:mm} 标定（{_calibLevel}/{_calibPreset}）：{_lastResult.Summary}";
        }, normalize: false);

        ApplyCalibrationResult();
    }

    private void ApplyCalibrationResult()
    {
        _settings.CalibrationLevel = _calibLevel;
        _settings.CalibrationPreset = _calibPreset;
        SettingsStore.Save(_settings);

        LoadConfigToUi(_pc.Draft);
        RefreshPending();

        if (ApplyDraft(announce: false))
        {
            ActionText.Text = "已应用标定生成的推荐曲线";
            Log.Info("已应用标定推荐曲线。");
        }
        else
        {
            ActionText.Text = "标定曲线已放入草稿，但被机理约束拦下，请到「策略」页看校验原因";
        }
    }

    // ---------------- 配置读写 ----------------

    private void LoadConfigToUi(ThermalConfig cfg)
    {
        _updating = true;
        try
        {
            _fanPoints.Clear();
            foreach (var p in cfg.FanCurve.Points) _fanPoints.Add(new CurvePoint(p.Temperature, p.Value));
            _freqPoints.Clear();
            foreach (var p in cfg.FreqCurve.Points) _freqPoints.Add(new CurvePoint(p.Temperature, p.Value));

            ChkFan.IsChecked = cfg.EnableFanControl;
            ChkFreq.IsChecked = cfg.EnableFrequencyControl;
            // 通道互斥：只有走电源计划通道时，这个勾选框才有意义
            ChkFreq.IsEnabled = (cfg.ControlChannel ?? ControlChannels.Msr) == ControlChannels.PowerCfg;
            ChkUv.IsChecked = cfg.EnableUndervolt;
            UvBox.Text = cfg.UndervoltMv.ToString();

            // 智能自动（取向不再单独设，跟全局取向走）
            ChkAutoCpu.IsChecked = cfg.AutoCpuMode;
            AutoTargetBox.Text = cfg.AutoTargetC.ToString("F0");
            AmbientBox.Text = cfg.AmbientC.ToString("F0");
            ChkAutoDryRun.IsChecked = cfg.AutoCpuDryRun;
            ChannelBox.SelectedIndex = (cfg.ControlChannel ?? ControlChannels.Msr).ToLowerInvariant() switch
            {
                ControlChannels.PowerCfg => 1,
                ControlChannels.Policy => 2,
                _ => 0
            };
            // 标定页的取向是只读的：显示当前全局取向 + 它派生了什么
            _calibPreset = ProfilePreset.Normalize(cfg.ProfileName);
            if (PrefFollowText != null)
            {
                PrefFollowText.Text = ProfileBinding.DisplayName(cfg);
                PrefFollowText.ToolTip = $"标定按全局取向的偏好反解曲线。当前生效：温度上限 {cfg.AutoTargetC:0}℃ "
                                       + $"· 风扇≤{cfg.FanMaxPct:0}% · EPP {(cfg.CpuEpp >= 0 ? cfg.CpuEpp.ToString() : "不改")}"
                                       + $" · τ {(cfg.CpuTauS > 0 ? cfg.CpuTauS + "s" : "不改")}。"
                                       + Environment.NewLine + ProfileBinding.Describe(cfg);
            }
            UpdateProfileChip();

            // CPU 精细微调
            ChkCpuTune.IsChecked = cfg.EnableCpuTuning;
            Pl1Box.Text = cfg.CpuPl1W.ToString();
            Pl2Box.Text = cfg.CpuPl2W.ToString();
            TauBox.Text = cfg.CpuTauS.ToString();
            EppBox.Text = cfg.CpuEpp.ToString();
            TurboBox.Text = cfg.CpuTurboRatio.ToString();

            CpuDesignBox.Text = cfg.CpuDesignW > 0 ? cfg.CpuDesignW.ToString("F0") : "0";
            GpuDesignBox.Text = cfg.GpuDesignW > 0 ? cfg.GpuDesignW.ToString("F0") : "0";
            IntervalBox.Text = cfg.SampleIntervalMs.ToString();
            EcIntervalBox.Text = cfg.FanWriteMinIntervalMs.ToString();
            HystBox.Text = cfg.FanHysteresisC.ToString("F1");
            FanMinBox.Text = cfg.FanMinPct.ToString("F0");
            FanMaxBox.Text = cfg.FanMaxPct.ToString("F0");
            RampUpBox.Text = cfg.FanRampUpPctPerSec.ToString("F0");
            RampDownBox.Text = cfg.FanRampDownPctPerSec.ToString("F0");
            CriticalBox.Text = cfg.CriticalTempC.ToString("F0");

            RefreshCpuCapsUi();
            RefreshPowerUi();
            TargetBox.Text = cfg.TargetTempC.ToString("F0");
            KpBox.Text = cfg.Kp.ToString("F2");
            KiBox.Text = cfg.Ki.ToString("F2");
            ChkTargetMode.IsChecked = cfg.TargetTempMode;

            ChkLogging.IsChecked = cfg.EnableLogging;
            LogEveryBox.Text = cfg.LogEveryN.ToString();
            LogPathText.Text = string.IsNullOrEmpty(_engine.SampleLogPath)
                ? $"采样日志目录：{Log.LogDirectory}"
                : "当前日志文件：" + _engine.SampleLogPath;

            ChkPolicy.IsChecked = cfg.UsePolicyModel;
            PolicyPathBox.Text = cfg.PolicyModelPath ?? "";
            if (PolicyFollowText != null)
                PolicyFollowText.Text = "跟随全局：" + ProfileBinding.DisplayName(cfg);
            UpdatePolicyInfo(cfg);

            ChkMinTray.IsChecked = _settings.MinimizeToTray;
            ChkAutoStart.IsChecked = _settings.AutoStart;

            ProfileCombo.SelectedItem = cfg.ProfileName;
            RefreshStrategyHints();   // 让「取向 · 自定义」标记跟着配置一起刷新
        }
        finally
        {
            _updating = false;
        }
    }

    private ThermalConfig BuildConfigFromUi()
    {
        // 只写草稿。旧版这里是 `var cfg = _engine.Config;`，等于把界面上每一个控件
        // 的改动直接改到引擎正在用的配置对象上——没有校验、没有回退点，这是"边拖边写硬件"的根因。
        var cfg = _pc.Draft;

        // 取向：全程序唯一一份，派生 AutoCpuProfile / 曲线族
        cfg.ProfileName = ProfilePreset.Normalize(ProfileCombo.SelectedItem as string ?? cfg.ProfileName);
        cfg.AutoCpuProfile = ProfilePreset.AutoProfile(cfg.ProfileName);
        // 控制通道：互斥（MSR 精细 / 电源计划限频 / 实测策略模型）
        cfg.ControlChannel = ChannelBox.SelectedIndex switch
        {
            1 => ControlChannels.PowerCfg,
            2 => ControlChannels.Policy,
            _ => ControlChannels.Msr
        };
        cfg.FanCurve = new Curve { Points = _fanPoints.Select(p => new CurvePoint(p.Temperature, Math.Clamp(p.Value, 0, 100))).ToList() };
        cfg.FanCurve.Normalize();
        cfg.FreqCurve = new Curve { Points = _freqPoints.Select(p => new CurvePoint(p.Temperature, Math.Clamp(p.Value, 5, 100))).ToList() };
        cfg.FreqCurve.Normalize();

        cfg.EnableFanControl = ChkFan.IsChecked == true;
        cfg.EnableFrequencyControl = ChkFreq.IsChecked == true;
        cfg.EnableUndervolt = ChkUv.IsChecked == true;

        cfg.UndervoltMv = int.TryParse(UvBox.Text, out int uv) ? Math.Clamp(uv, -150, 0) : -50;

        // 智能自动（取向已在上面从全局取向派生，这里不再有第二份）
        cfg.AutoCpuMode = ChkAutoCpu.IsChecked == true;
        cfg.AutoTargetC = double.TryParse(AutoTargetBox.Text, out double at)
            ? Math.Clamp(at, 50, AutoCpuController.SafetyCeilingC) : 88;
        cfg.AmbientC = double.TryParse(AmbientBox.Text, out double amb) ? Math.Clamp(amb, -10, 45) : 24;
        cfg.AutoCpuDryRun = ChkAutoDryRun.IsChecked == true;

        // CPU 精细微调
        cfg.EnableCpuTuning = ChkCpuTune.IsChecked == true;
        // 平台功率（0 = 自动探测）。上限用绝对边界，真正的"本机上限"由探测/校验决定，
        // 这里只挡住手滑输入（例如把 45 打成 450）。
        cfg.CpuDesignW = double.TryParse(CpuDesignBox.Text, out double cdw)
            ? Math.Clamp(cdw, 0, PlatformPower.AbsoluteCpuMaxW) : 0;
        cfg.GpuDesignW = double.TryParse(GpuDesignBox.Text, out double gdw)
            ? Math.Clamp(gdw, 0, PlatformPower.AbsoluteGpuMaxW) : 0;

        cfg.CpuPl1W = int.TryParse(Pl1Box.Text, out int pl1) ? Math.Clamp(pl1, 0, 200) : 0;
        cfg.CpuPl2W = int.TryParse(Pl2Box.Text, out int pl2) ? Math.Clamp(pl2, 0, 250) : 0;
        cfg.CpuTauS = int.TryParse(TauBox.Text, out int tau) ? Math.Clamp(tau, 0, 64) : 0;
        cfg.CpuEpp = int.TryParse(EppBox.Text, out int epp) ? Math.Clamp(epp, -1, 255) : -1;
        cfg.CpuTurboRatio = int.TryParse(TurboBox.Text, out int tr) ? Math.Clamp(tr, 0, 60) : 0;
        cfg.SampleIntervalMs = int.TryParse(IntervalBox.Text, out int iv) ? Math.Clamp(iv, 500, 5000) : 1000;
        cfg.FanWriteMinIntervalMs = int.TryParse(EcIntervalBox.Text, out int ecInt) ? Math.Clamp(ecInt, 1000, 10000) : 3000;
        cfg.FanHysteresisC = double.TryParse(HystBox.Text, out double hy) ? Math.Clamp(hy, 0.5, 10) : 3;
        cfg.FanMinPct = double.TryParse(FanMinBox.Text, out double fm) ? Math.Clamp(fm, 0, 100) : 20;
        cfg.FanMaxPct = double.TryParse(FanMaxBox.Text, out double fx) ? Math.Clamp(fx, 1, 100) : 100;
        cfg.FanRampUpPctPerSec = double.TryParse(RampUpBox.Text, out double ru) ? Math.Clamp(ru, 5, 200) : 60;
        cfg.FanRampDownPctPerSec = double.TryParse(RampDownBox.Text, out double rd) ? Math.Clamp(rd, 1, 100) : 10;
        // 安全线上限由模型锁定（TJmax 100 − 5℃），界面上限抄到 110 是错误的：
        // 允许用户把"强制降档线"设到 99℃ 等于取消保护。
        cfg.CriticalTempC = double.TryParse(CriticalBox.Text, out double ct)
            ? Math.Clamp(ct, 70, ParamConstraints.SafetyCeilingC) : ParamConstraints.SafetyCeilingC;

        cfg.TargetTempMode = ChkTargetMode.IsChecked == true;
        cfg.TargetTempC = double.TryParse(TargetBox.Text, out double tt) ? Math.Clamp(tt, 55, 100) : 80;
        cfg.Kp = double.TryParse(KpBox.Text, out double kp) ? Math.Clamp(kp, 0.5, 20) : 5;
        cfg.Ki = double.TryParse(KiBox.Text, out double ki) ? Math.Clamp(ki, 0, 3) : 0.35;

        cfg.EnableLogging = ChkLogging.IsChecked == true;
        cfg.LogEveryN = int.TryParse(LogEveryBox.Text, out int ln) ? Math.Clamp(ln, 1, 60) : 2;

        cfg.UsePolicyModel = ChkPolicy.IsChecked == true;
        cfg.PolicyModelPath = PolicyPathBox.Text?.Trim() ?? "";
        // 策略模型不再单独设取向：直接由全局取向派生，避免同一个语义在多处各有一份
        cfg.PolicyStrategy = ProfilePreset.AutoProfile(cfg.ProfileName) switch
        {
            "silent" => "quiet",
            "performance" => "power",
            _ => "balanced"
        };

        // 通道归一化必须在最后：它会按所选通道关掉多余的控制器
        ControlChannels.Normalize(cfg);
        return cfg;
    }

    // ---------------- 实测策略模型 ----------------

    /// <summary>在若干常见位置找最新的 model-*.json（实验台产出）。</summary>
    private static string? FindLatestModel()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p!);
        }

        Add(@"C:\ProgramData\ThermalPilot\lab-data");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ThermalPilot", "lab-data"));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            Add(Path.Combine(dir.FullName, "lab-data"));
            Add(Path.Combine(dir.FullName, "ThermalPilot", "lab-data"));
        }

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var f in Directory.GetFiles(root, "model-*.json", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t <= bestTime) continue;
                    bestTime = t;
                    best = f;
                }
            }
            catch { }
        }
        return best;
    }

    private void UpdatePolicyInfo(ThermalConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PolicyModelPath) || !File.Exists(cfg.PolicyModelPath))
        {
            PolicyInfoText.Text = "未载入策略模型。先以管理员身份跑 lab\\run-lab.cmd 产出 model-*.json，再点「用最新结果」。";
            return;
        }

        var m = PolicyModel.Load(cfg.PolicyModelPath);
        if (m == null || m.ControlLadder.Count == 0)
        {
            PolicyInfoText.Text = "模型文件无法解析或无控制阶梯：" + Path.GetFileName(cfg.PolicyModelPath);
            return;
        }

        var strategy = (cfg.PolicyStrategy ?? "balanced").ToLowerInvariant() switch
        {
            "quiet" => PolicyModel.Strategy.Quiet,
            "power" => PolicyModel.Strategy.Power,
            _ => PolicyModel.Strategy.Balanced
        };
        var opt = m.Resolve(cfg.TargetTempC, strategy);

        string head = $"{Path.GetFileName(cfg.PolicyModelPath)} · {m.Mode} · {m.ControlLadder.Count} 档 · τ={m.Dynamic.TauS:F0}s";
        PolicyInfoText.Text = opt == null
            ? head + "\n当前目标温度在阶梯表覆盖范围之外，运行时将退回曲线控制。"
            : head + $"\n按目标 {cfg.TargetTempC:F0}℃ 查表 → 频率上限 {opt.Cap}%，风扇 " +
              (opt.Fan.HasValue ? $"{opt.Fan.Value:F0}%" : "不干预（BIOS 自控）") +
              $"，预测稳态 {opt.PredT:F1}℃，噪声 {opt.SplDeltaDb:+0.0;-0.0;0} dB";
    }

    private void ChkPolicy_Changed(object sender, RoutedEventArgs e) => RefreshPolicyInfo();

    private void RefreshPolicyInfo()
    {
        if (_updating || !_ready) return;
        try { UpdatePolicyInfo(BuildConfigFromUi()); } catch { }
    }

    private void BtnPickPolicy_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择实测策略模型",
            Filter = "策略模型 (model-*.json)|*.json|所有文件|*.*"
        };
        var guess = FindLatestModel();
        if (guess != null) dlg.InitialDirectory = Path.GetDirectoryName(guess);
        if (dlg.ShowDialog(this) != true) return;

        PolicyPathBox.Text = dlg.FileName;
        ChkPolicy.IsChecked = true;
        CaptureDraftFromUi();
        ActionText.Text = "策略模型已载入：" + Path.GetFileName(dlg.FileName);
    }

    private void BtnAutoPolicy_Click(object sender, RoutedEventArgs e)
    {
        var latest = FindLatestModel();
        if (latest == null)
        {
            System.Windows.MessageBox.Show(this,
                "没找到 model-*.json。\n\n请先以管理员身份运行 lab\\run-lab.cmd（约 18 分钟），\n" +
                "跑完会在 lab-data 下生成 model-<tag>.json。",
                "还没有实验数据", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PolicyPathBox.Text = latest;
        ChkPolicy.IsChecked = true;
        CaptureDraftFromUi();
        ActionText.Text = "已使用最新策略模型：" + Path.GetFileName(latest);
    }

    /// <summary>
    /// 给所有可编辑的输入控件挂「改动即入草稿」。
    ///
    /// 为什么不在 XAML 里逐个写事件：控件有三十多个，漏一个就是一个"改了没反应"的坑
    /// （底部「应用」永远灰正是这么来的）。这里按类型统一挂，新增控件不用再记得加绑定。
    ///
    /// 为什么放在 Loaded：视觉树在窗口构造阶段还没建好，那时遍历不到子控件。
    /// </summary>
    private void WireDraftCapture()
    {
        foreach (var tb in FindVisual<TextBox>(this))
        {
            if (tb.IsReadOnly || string.IsNullOrEmpty(tb.Name)) continue;
            if (NonParamBoxes.Contains(tb.Name)) continue;
            // 这 4 个已在 XAML 里绑了 GlobalParam_Changed，避免重复触发
            if (tb.Name is "AutoTargetBox" or "AmbientBox" or "FanMaxBox" or "FanMinBox") continue;
            tb.TextChanged += OnDraftTextChanged;
        }

        foreach (var cb in FindVisual<CheckBox>(this))
        {
            cb.Checked += OnDraftToggled;
            cb.Unchecked += OnDraftToggled;
        }
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindVisual<T>(child)) yield return deeper;
        }
    }

    /// <summary>文本编辑：等 350 ms 静默再落草稿，避免每敲一个字符重建一次表格。</summary>
    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !_ready || App.SmokeTest) return;
        // 这四个框是自动控制器的执行器；用户手改它们 = 要手动接管
        if (sender is TextBox tb && tb.Name is "Pl1Box" or "Pl2Box" or "TauBox" or "EppBox")
            _cpuHandEdited = true;
        _draftDebounce.Stop();
        _draftDebounce.Start();
    }

    /// <summary>勾选框/单选：切换本身就是最终状态，立即落草稿。</summary>
    private void OnDraftToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || !_ready || App.SmokeTest) return;

        // 智能自动与手工 CPU 调控互斥：勾一个就摘另一个。
        // 不这么做的后果是界面上"两个都开着"，提交时归一化悄悄丢掉一个，
        // 用户以为生效的其实没生效。
        if (sender == ChkAutoCpu && ChkAutoCpu.IsChecked == true && ChkCpuTune.IsChecked == true)
        {
            _updating = true;
            try { ChkCpuTune.IsChecked = false; } finally { _updating = false; }
        }
        else if (sender == ChkCpuTune && ChkCpuTune.IsChecked == true && ChkAutoCpu.IsChecked == true)
        {
            _updating = true;
            try { ChkAutoCpu.IsChecked = false; } finally { _updating = false; }
        }

        _draftDebounce.Stop();
        CaptureDraftFromUi(announce: false);
    }

    /// <summary>
    /// 把界面上的值**暂存进草稿**，不写硬件。
    ///
    /// 名字必须准确：它只暂存。真正写入硬件的唯一入口是 <see cref="ApplyDraft"/>。
    /// 曾经它是"每次控件变动就 ApplyConfig + 存盘"，等于边拖边写硬件 —— 没有校验窗口、
    /// 没有回退点，用户自定义极值时可以先烧了再说。现在只留暂存这一半语义。
    /// </summary>
    private void CaptureDraftFromUi(bool announce = true)
    {
        if (!_ready || App.SmokeTest) return;

        BuildConfigFromUi();          // 只写 _pc.Draft
        PlatformPower.Apply(_pc.Draft, null);   // 设计功耗改了 → PL1/PL2 的可用区间跟着变
        RefreshPowerUi();

        // 托盘 / 自启属于"程序行为"，不是热参数，勾选即生效。
        // 只有真变了才落盘：编辑热参数会频繁走到这里，无谓写盘会把输入拖卡。
        bool behavior = _settings.MinimizeToTray != (ChkMinTray.IsChecked == true)
                        || _settings.AutoStart != (ChkAutoStart.IsChecked == true);
        if (behavior)
        {
            _settings.MinimizeToTray = ChkMinTray.IsChecked == true;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            SettingsStore.Save(_settings);
            ApplyAutoStart(_settings.AutoStart);
        }

        LogPathText.Text = string.IsNullOrEmpty(_engine.SampleLogPath)
            ? $"采样日志目录：{Log.LogDirectory}"
            : "当前日志文件：" + _engine.SampleLogPath;

        RefreshPending();
        RefreshStrategyHints();       // 高级页手改也会改变「取向 · 自定义」标记
        if (announce)
            ActionText.Text = _pc.HasPending
                ? $"已暂存 {_pc.PendingCount} 项变更 —— 点「应用」才写入硬件"
                : "配置未变更";
    }

    /// <summary>
    /// 唯一写入硬件的入口：校验（机理约束）→ 提交 → 通知引擎重读。
    /// 有阻断级违规就**不提交、不写硬件**，把原因原样显示在「参数校验」区。
    /// </summary>
    private bool ApplyDraft(bool announce = true, bool autoYield = true)
    {
        if (!_ready) return false;

        BuildConfigFromUi();

        // 校验用的 Specs（Pl1/Pl2 的 Max）读的是"当前生效的平台功率"，
        // 所以必须在 Apply() 之前把草稿里的设计功耗刷新进去，否则会用旧上限去卡新值。
        PlatformPower.Apply(_pc.Draft, null);
        RefreshPowerUi();

        // 降压确认放在提交层，而不是某个按钮里 —— 任何一条提交路径（底栏、卡片按钮）
        // 只要会改动 MSR 降压偏移，都必须过这一道。
        if (!ConfirmUndervoltIfNeeded()) return false;

        // 手调即让位：手动改过 PL1/PL2/τ/EPP 就交出智能自动的控制权，
        // 否则控制器每 3 秒重算会把刚应用的手调值顶掉（"改了又被改回去"）。
        // 「启用智能自动」这类明确要自动的提交路径传 autoYield:false，用户的显式意图优先。
        bool yielded = autoYield && _pc.YieldAutoForManualCpuEdit(_cpuHandEdited);
        if (yielded)
        {
            _updating = true;
            try { ChkAutoCpu.IsChecked = false; ChkCpuTune.IsChecked = true; }
            finally { _updating = false; }
        }

        var r = _pc.Apply();
        ShowValidation(r.Violations);
        if (!r.Ok)
        {
            ActionText.Text = r.Message;
            return false;
        }

        _engine.ApplyConfig(_pc.Committed);
        _settings.Config = _pc.Committed.Clone();
        // 配置档 = 内置基线 + 增量，与应用同一时刻落盘：这样"哪些项是我改的"永远不会丢，
        // 升级程序后没改过的项还能跟随新的模型基线。
        _settings.Profile = ProfileInstance.Capture(_pc.Committed);
        SettingsStore.Save(_settings);

        RefreshPending();
        _cpuHandEdited = false;
        if (yielded)
            AutoStatusText.Text = "已切换为手动：按你的参数执行（智能自动已关闭，想恢复点「启用智能自动」）";
        if (announce)
            ActionText.Text = r.Message + "（已写入硬件）" + (yielded ? "；已切换为手动" : "");
        return true;
    }

    /// <summary>
    /// 降压是底层 MSR 写入（等价 ThrottleStop FIVR），提交前必须二次确认。
    /// 只在"这次提交会新开启降压或改动偏移量"时问，重复点应用不会反复弹框。
    /// </summary>
    private bool ConfirmUndervoltIfNeeded()
    {
        if (App.SmokeTest) return true;

        bool wasUv = _pc.Committed.EnableUndervolt;
        int wasMv = _pc.Committed.UndervoltMv;
        if (!_pc.Draft.EnableUndervolt) return true;
        if (wasUv && _pc.Draft.UndervoltMv == wasMv) return true;

        var r = System.Windows.MessageBox.Show(this,
            "CPU 降压会直接写 Intel MSR（偏移量易失，重启即恢复）：\n\n" +
            "· 需要管理员权限；10 代以后 Intel + 新版 BIOS 通常直接拒绝写入\n" +
            "· 开启「内存完整性(HVCI)」时内核驱动被禁止加载，功能不可用\n" +
            "· 偏移过大的最坏情况是死机，重启即可恢复\n\n" +
            $"本次将写入 {_pc.Draft.UndervoltMv} mV。建议从 -30 mV 起步，每次 10 mV 递减并做压力测试。\n是否继续？",
            "开启降压确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        return r == MessageBoxResult.OK;
    }

    private void DiscardDraft()
    {
        _pc.Discard();
        _cpuHandEdited = false;
        LoadConfigToUi(_pc.Draft);
        RefreshPending();
        ShowValidation(_pc.Validate());
        ActionText.Text = "已放弃草稿，回到上次应用的值";
    }

    /// <summary>
    /// 按"本机平台能力"决定高级参数里哪些控件可编辑。
    ///
    /// 为什么必须在界面上禁用而不是让用户填完再报错：参数范围约束解决的是"值能多大"，
    /// 解决不了"这台机器上有没有这个寄存器"。AMD 平台没有 0x610/0x773/0x1AD/0x150、
    /// 老 CPU 没有 HWP、无睿频的 CPU 没有倍率表、11 代移动端关了 FIVR ——
    /// 这些情况下填进去只会得到一条"写入失败"，用户完全看不出那是硬件不存在。
    ///
    /// 只有"平台层面不存在"（CapState.Unsupported）才禁用；
    /// "需要前置条件"（非管理员 / HVCI / 驱动未加载）保持可编辑，但用文字讲清楚缺什么 ——
    /// 否则用户提权回来还得重填一遍。
    /// </summary>
    /// <summary>
    /// 刷新「平台功率」区块：把 CPU/GPU 设计功耗与 PL1/PL2 的可用区间显示出来。
    ///
    /// 这一块的重点不是数字本身，而是**来源**：同一个 45W 可能是 MSR 读出来的（可信），
    /// 也可能是查型号表猜的（仅供参考），两者必须一眼能分开 —— 否则用户会拿猜出来的数
    /// 去判断"我这台机器还能不能超"。
    /// </summary>
    private void RefreshPowerUi()
    {
        if (PlatPowerBox == null) return;

        var cpu = PlatformPower.Cpu;
        var gpu = PlatformPower.Gpu;
        bool solid = cpu.Measured && (!gpu.HasValue || gpu.Measured);

        PlatPowerDetail.Text = $"CPU {cpu.Text}　·　PL2 ≤ {PlatformPower.Pl2CeilingW:F0}W　·　"
                             + $"GPU {(gpu.HasValue ? gpu.Text : "未知")}";
        PlatPowerDetail.ToolTip = string.Join(Environment.NewLine, PlatformPower.Detail)
            + Environment.NewLine + Environment.NewLine
            + "0 = 自动探测：CPU 读 MSR 0x614 thermal_spec_power（规格里的 TDP），失败退回 0x610 的 BIOS PL1，"
            + "再退回型号表；GPU 读 nvidia-smi 的功率上限（Optimus 笔记本常为 N/A），失败退回型号表。手填优先于一切。";
        PlatPowerDetail.Foreground = (Brush)FindResource(solid ? "Ok" : "Muted");

        if (PlatPowerNote != null)
        {
            PlatPowerNote.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
            PlatPowerNote.Text = "有数值来自型号表或内置默认，不是本机实测：想更准就在下面手填（0 = 交回自动）。";
        }

        if (Pl1HintText != null)
            Pl1HintText.Text = $"W（0=不改，PL1 ≤ {PlatformPower.CpuDesignW:F0}）";
        if (Pl2HintText != null)
            Pl2HintText.Text = $"W（0=不改，PL2 ≤ {PlatformPower.Pl2CeilingW:F0}）";
    }

    /// <summary>重新探测平台功率（换机器/换显卡/装驱动之后用）。</summary>
    private void BtnDetectPower_Click(object sender, RoutedEventArgs e)
    {
        PlatformPower.Apply(_pc.Committed, _engine.Cpu.Msr, force: true);
        RefreshPowerUi();
        RefreshCpuCapsUi();
        RefreshProfileTable();
        ShowValidation(_pc.Validate());
        ActionText.Text = "已重新探测：" + PlatformPower.Summary;
    }

    private void RefreshCpuCapsUi()
    {
        if (CpuCapTitle == null) return;

        var caps = CpuCapabilities.Current;

        // 标题：这台机器是什么芯片
        CpuCapTitle.Text = "本机 CPU：" + caps.CpuLine;
        CpuCapBadges.Text = caps.BadgeLine;
        bool anyHard = caps.Items.Any(x => x.Hard);
        CpuCapBadges.Foreground = (Brush)FindResource(anyHard ? "Warn" : "Ok");
        CpuCapBox.ToolTip = caps.CpuLine + Environment.NewLine + caps.Details;
        // 只写"哪些被禁用 + 为什么"，逐项原因放进悬浮提示 —— 高级页已经够长了
        var hardNames = caps.Items.Where(x => x.Hard).Select(x => x.Key switch
        {
            "Pl" => "PL1/PL2/τ", "Epp" => "EPP", "Turbo" => "睿频上限", "Uv" => "降压", _ => x.Label
        }).ToList();
        CpuCapDetail.Text = hardNames.Count > 0
            ? $"已禁用：{string.Join("、", hardNames)} —— 本机没有对应寄存器，填了也不会写进硬件。"
              + " 鼠标停在任一项上看原因与替代做法。"
            : caps.AllMsrUnsupported
                ? "本机不支持 MSR 精细调控，控制通道已退回 Windows 电源计划限频。"
                : "其余项均可编辑；写入后都会回读校验，被 BIOS 锁定的会明确报失败。";

        // —— 逐项禁用 ——
        bool pl = !caps.Pl.Hard, epp = !caps.Epp.Hard, turbo = !caps.Turbo.Hard, uv = !caps.Uv.Hard;

        Pl1Box.IsEnabled = pl;
        Pl2Box.IsEnabled = pl;
        TauBox.IsEnabled = pl;
        EppBox.IsEnabled = epp;
        TurboBox.IsEnabled = turbo;
        ChkUv.IsEnabled = uv;
        UvBox.IsEnabled = uv;

        // 睿频提示必须跟着平台走：非 Intel 平台上"本机单核 38 / 全核 34"是错的（0x1AD 不存在）。
        // 注意要区分三种情况 —— 「本机不支持」（平台没有）和「暂时读不到」（Ring0 没加载）
        // 不能都写成"不支持"，否则自检/非管理员时用户会以为自己的 CPU 没有睿频。
        if (TurboHintText != null)
        {
            var st1 = caps.Turbo.State == CapState.Supported ? _engine.Cpu.ReadCurrent() : null;
            TurboHintText.Text = st1 is { TurboRatio1Core: > 0 }
                ? $"×100MHz（0=不改；本机单核 {st1.TurboRatio1Core} / 全核 {st1.TurboRatioAllCore}）"
                : caps.Turbo.State == CapState.Unsupported
                    ? "×100MHz（本机不支持）"
                    : "×100MHz（0=不改）";
        }

        Pl1Box.ToolTip = pl ? "持续功率上限（W），0 = 不改" : caps.Pl.Tip;
        Pl2Box.ToolTip = pl ? "短时突增上限（W），0 = 不改" : caps.Pl.Tip;
        TauBox.ToolTip = pl ? "突增窗口（s，只能取 2 的幂：1/2/4/8/16/32/64），0 = 不改" : caps.Pl.Tip;
        EppBox.ToolTip = epp ? "EPP：0 最积极升频 / 255 最省电，-1 = 不改" : caps.Epp.Tip;
        TurboBox.ToolTip = turbo ? "睿频倍率上限（×100MHz），0 = 不改" : caps.Turbo.Tip;
        UvBox.ToolTip = uv ? "电压偏移（mV，负值降压），0 = 不改" : caps.Uv.Tip;
        ChkUv.ToolTip = uv ? "实验性：写入 0x150，回读校验失败会明确提示" : caps.Uv.Tip;

        // 整套 MSR 调控：三项全废就没意义了
        bool anyMsr = pl || epp || turbo || uv;
        ChkCpuTune.IsEnabled = anyMsr;
        ChkCpuTune.ToolTip = anyMsr
            ? "PL1/PL2/Time/EPP/睿频/降压的单项可用性见上方能力条"
            : "本机没有任何可用的 MSR 调控通道：" + caps.Details;

        // 智能自动至少要能写 PL1，否则闭环没有执行器
        ChkAutoCpu.IsEnabled = pl;
        ChkAutoCpu.ToolTip = pl
            ? "以交付性能为目标、温度为上限，每 3 秒闭环调 PL1/PL2/EPP"
            : "本机无法写 PL1（" + caps.Pl.Detail + "），闭环没有执行器，自动模式不可用。";
        string gpuTxt = PlatformPower.Gpu.HasValue
            ? $"独显 {PlatformPower.GpuDesignW:F0}W"
            : "独显功率未知";
        CapAutoText.Text = pl
            ? $"执行器：MSR 0x610（PL1/PL2/τ）{(epp ? " + 0x773（EPP）" : "；EPP 不可用，只用 PL1 闭环")}"
              + $"　·　CPU 设计功耗 {PlatformPower.CpuDesignW:F0}W（{PlatformPower.Cpu.Badge}，PL2 ≤ {PlatformPower.Pl2CeilingW:F0}W），程序不会越过它"
              + $"　·　{gpuTxt} 共用散热"
            : "本机不支持 PL1 调控 → 自动模式已禁用，只能用「电源计划限频」。";
        if (!pl && AutoStatusText != null)
        {
            AutoStatusText.Text = "自动模式不可用：" + caps.Pl.Detail;
            AutoStatusText.Foreground = (Brush)FindResource("Warn");
        }

        // 通道：MSR 项不可选时把选项本身禁掉，避免选了以后一路失败
        if (ChanMsrItem != null)
        {
            ChanMsrItem.IsEnabled = anyMsr;
            ChanMsrItem.ToolTip = anyMsr ? null : "本机没有可用的 MSR 通道：" + caps.Details;
        }
        if (!anyMsr && ChannelBox.SelectedIndex == 0)
        {
            // 阻断 SelectionChanged 里的 CaptureDraftFromUi：这里只是"把选项归位"，不是用户改配置
            bool prev = _updating;
            _updating = true;
            try { ChannelBox.SelectedIndex = 1; }
            finally { _updating = prev; }
        }
        RefreshChannelHint();
        if (!anyMsr && ChannelHint != null)
            ChannelHint.Text = "本机没有可用的 MSR 通道（" + caps.Items.First(x => x.Hard).Detail + "），"
                             + "已强制使用电源计划限频。MSR 那一项在下拉里是灰的，选不了。";
    }

    /// <summary>刷新底部「待应用」条。</summary>
    private void RefreshPending()
    {
        if (PendingText == null) return;

        var pend = _pc.Diff();
        int n = pend.Count;
        PendingText.Text = n == 0 ? "无待应用变更" : $"● {n} 项待应用";
        PendingText.Foreground = n == 0
            ? (Brush)FindResource("Muted")
            : (Brush)FindResource("Warn");
        BtnApplyDraft.IsEnabled = n > 0;
        BtnDiscardDraft.IsEnabled = n > 0;
        PendingList.Text = n == 0
            ? "参数与硬件当前生效值一致"
            : string.Join("　·　", pend.Select(x => $"{x.Label} {x.From} → {x.To}"));

        RefreshProfileTable();
    }

    /// <summary>
    /// 刷新「⓪ 当前配置档」表：把「内置基线 / 当前值 / 机理约束 / 来源」摊开。
    ///
    /// 这是「哪些参数是启动的、数值多少、约束多少」的唯一答案页 ——
    /// 表格显示的是**已应用并已持久化**的值（Committed），草稿改动只体现在底部待应用条，
    /// 免得用户把"还没写硬件的草稿"当成"已经生效的配置"。
    /// </summary>
    private void RefreshProfileTable()
    {
        if (ProfileList == null) return;

        var cfg = _pc.Committed;

        // 热阻与芯片最大睿频是"锁定基线"的两项输入，能读到就显示真实值
        double rEst = _pc.REstimate;
        if (_pc.MaxTurboRatio <= 0 && !_turboProbed && _engine.IsRunning)
        {
            _turboProbed = true;
            try
            {
                var msr = _engine.Cpu.ReadCurrent();
                // 睿频上限写 0x1AD 时会把**每一档**都压到不超过给定值，
                // 所以"芯片最大睿频"这个约束要用全核档（3.4GHz），不是单核档（3.8GHz）。
                if (msr is { TurboRatioAllCore: > 0 }) _pc.MaxTurboRatio = msr.TurboRatioAllCore;
                else if (msr is { TurboRatio1Core: > 0 }) _pc.MaxTurboRatio = msr.TurboRatio1Core;
            }
            catch { }
        }

        _profileRows.Clear();
        foreach (var row in ProfileInstance.Rows(cfg, rEst, _pc.MaxTurboRatio, CpuCapabilities.Current)) _profileRows.Add(row);

        var inst = _settings.Profile ?? ProfileInstance.Capture(cfg);
        // 计数直接来自表格行，而不是 ProfileInstance.Delta —— 否则会出现"表格标了 3 项自定义、
        // 摘要说 5 项"的两套口径（本机不支持的项已经不算自定义了）。
        int ovRows = _profileRows.Count(r => r.IsOverride);
        int ovCurves = _profileRows.Count(r => r.IsOverride && (r.Label.EndsWith("曲线")));
        int ovParams = ovRows - ovCurves;
        int hardRows = _profileRows.Count(r => r.Source.StartsWith("✗"));
        ProfileSummaryText.Text = ovRows > 0
            ? $"配置档：{inst.DisplayName}　·　{ovParams} 项参数、{ovCurves} 条曲线与基线不同"
            : $"配置档：{inst.DisplayName}　·　全部与内置基线一致";
        if (hardRows > 0)
            ProfileSummaryText.Text += $"　·　{hardRows} 项本机不支持（已禁用）";
        ProfileHintText.Text = _pc.HasPending
            ? $"下表是已应用并已保存的值；草稿还有 {_pc.PendingCount} 项没写进硬件（见底部「待应用」条）。"
            : "下表就是当前真正生效的整份参数：内置基线（模型给定，不可直接改）+ 你的增量。改任何一项都只动增量，基线本身不变。";
        ProfileSavedText.Text = $"　已持久化 {inst.SavedUtc.LocalDateTime:MM-dd HH:mm} · 重启后仍是这套值";
    }

    /// <summary>把机理约束校验结果显示出来（阻断红 / 警告黄 / 通过绿）。</summary>
    private void ShowValidation(IReadOnlyList<ParamViolation> v)
    {
        if (ValidationText == null) return;

        int blocks = v.Count(x => x.Severity == ParamSeverity.Block);
        int warns = v.Count(x => x.Severity == ParamSeverity.Warn);
        int infos = v.Count(x => x.Severity == ParamSeverity.Info);

        if (v.Count == 0)
        {
            ValidationText.Text = "✓ 全部通过机理约束校验";
            ValidationText.Foreground = (Brush)FindResource("Ok");
            return;
        }

        var sb = new System.Text.StringBuilder();
        if (blocks > 0) sb.AppendLine($"✕ {blocks} 项阻断（不能应用）");
        if (warns > 0) sb.AppendLine($"⚠ {warns} 项警告（可以应用，但达不到预期）");
        if (infos > 0) sb.AppendLine($"ℹ {infos} 项本机不支持 / 暂不可用（应用时不写入硬件）");
        foreach (var x in v.OrderBy(x => x.Severity))
            sb.AppendLine($"  · {x.Label}：{x.Message}");

        ValidationText.Text = sb.ToString().TrimEnd();
        // 只有 Info 时用灰色：它不是错误，是"这台机器没这个旋钮"的说明
        ValidationText.Foreground = blocks > 0
            ? (Brush)FindResource("Danger")
            : warns > 0 ? (Brush)FindResource("Warn") : (Brush)FindResource("Muted");
    }

    // ---------------- 智能自动 / CPU 精细微调 ----------------

    private void AutoProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 已废弃：取向统一在「策略」页，这里保留空实现只为兼容旧的 XAML 事件绑定。
    }

    /// <summary>控制通道切换：换个通道要重新应用一次，且会互相关掉其它控制器。</summary>
    private void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        CaptureDraftFromUi(announce: false);
        RefreshChannelHint();
        ActionText.Text = "控制通道已改到草稿 —— 点「应用」生效（三个通道互斥，只会有一个在控制 CPU）";
    }

    private void RefreshChannelHint()
    {
        if (ChannelHint == null) return;
        ChannelHint.Text = ChannelBox.SelectedIndex switch
        {
            1 => "电源计划限频（PROCTHROTTLEMAX）：粗粒度、只写 Windows 电源方案。本机实测该旋钮对频率无效（100/70/40 三档功率温度完全一样），仅作老旧机器兼容。",
            2 => "实测策略模型查表：需要先用实验台产出 model-*.json，按阶梯表下发。没有模型文件时不会生效。",
            _ => "MSR 精细调控（推荐）：直接写 PL1/PL2/τ/EPP/睿频倍率，是这台机器上真正能改变功率与频率的通道。另外两个通道会被自动关闭，避免三套控制器互相覆盖。"
        };
    }

    private void BtnApplyDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyDraft(announce: true))
        {
            ActionText.Text = "应用被拒绝，请看「参数校验」里的阻断原因";
            return;
        }
        LoadConfigToUi(_pc.Committed);
    }

    private void BtnDiscardDraft_Click(object sender, RoutedEventArgs e) => DiscardDraft();

    /// <summary>把当前取向的推荐值重新写进草稿（覆盖手改），不直接写硬件。</summary>
    private void BtnReapplyProfile_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfilePreset.Normalize(ProfileCombo.SelectedItem as string);
        _cpuHandEdited = false;   // 预设套用不算手调，不让智能自动让位
        _pc.Edit(cfg => ProfilePreset.Apply(cfg, name));
        LoadConfigToUi(_pc.Draft);
        RefreshStrategyHints();
        RefreshPending();
        ShowValidation(_pc.Validate());
        ActionText.Text = $"已把「{name}」预设值写回草稿 —— 点「应用」生效";
    }

    /// <summary>清空全部自定义增量，回到当前取向的内置基线（只进草稿）。</summary>
    private void BtnResetProfile_Click(object sender, RoutedEventArgs e)
    {
        var before = ProfileInstance.Capture(_pc.Draft);
        string name = ProfilePreset.Normalize(_pc.Draft.ProfileName);

        _cpuHandEdited = false;   // 基线重置不算手调
        _pc.Edit(c => ProfileInstance.ResetToBaseline(c, name));
        LoadConfigToUi(_pc.Draft);
        RefreshStrategyHints();
        RefreshPending();
        ShowValidation(_pc.Validate());

        ActionText.Text = before.IsCustom
            ? $"已清空 {before.Count} 项自定义，回到「{name}」内置基线 —— 点「应用」才写入硬件"
            : $"当前就是「{name}」内置基线，没有自定义项";
    }

    /// <summary>清空热阻估计：换硅脂/相变片、改散热之后必须做一次，否则预算仍按旧热阻算。</summary>
    private void BtnResetR_Click(object sender, RoutedEventArgs e)
    {
        _engine.AutoController.ResetResistance();
        _pc.REstimate = double.NaN;
        _pc.RConfident = false;
        AutoStatusText.Text = "热阻估计已清空：接下来会在满载时用 ΔT/ΔP 重新拟合（约 1–2 分钟负载）。";
        RefreshProfileTable();
    }

    private void BtnAutoApply_Click(object sender, RoutedEventArgs e)    {
        if (ChkAutoCpu.IsChecked != true)
        {
            ActionText.Text = "请先勾选「启用智能自动」";
            return;
        }

        // 这个按钮的语义就是"要自动"：哪怕手改过 PL1/PL2/τ/EPP，也让自动接管它们
        if (!ApplyDraft(announce: false, autoYield: false))
        {
            ActionText.Text = "智能自动未能启用：参数被机理约束拦下，请到「① 策略」页看原因";
            ShowValidation(_pc.Validate());
            return;
        }
        var st = _engine.State;
        AutoStatusText.Text = string.IsNullOrEmpty(st.CpuHint)
            ? "自动已启用，等待首个采样（约 1 秒）…"
            : st.CpuHint;
        if (!string.IsNullOrEmpty(st.PerfText)) PerfStatusText.Text = st.PerfText;

        ActionText.Text = st.AutoCpuActive
            ? "智能自动已启用"
            : "智能自动已保存；若未生效请查看下方状态（需管理员 + MSR 可用）";
        Log.Info("智能自动已" + (st.AutoCpuActive ? "启用" : "保存（待生效）") + "：" + st.CpuHint);
    }

    private void BtnReadCpu_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsRunning)
        {
            CpuStatusText.Text = "请先启动引擎（右上角「启动」），MSR 驱动在启动后才加载。";
            return;
        }

        var st = _engine.Cpu.ReadCurrent();
        if (st == null)
        {
            CpuStatusText.Text = "读取失败：" + _engine.Cpu.LastError;
            return;
        }

        // 读取不算手调：填框期间挂起草稿捕获，填完统一入草稿（不触发智能自动让位）
        int? uv = _engine.Undervolt.ReadCurrentMv();
        _updating = true;
        try
        {
            Pl1Box.Text = st.Pl1W.ToString("F0");
            Pl2Box.Text = st.Pl2W.ToString("F0");
            TauBox.Text = st.TauS.ToString("F0");
            EppBox.Text = st.Epp.ToString();
            TurboBox.Text = st.TurboRatio1Core.ToString();   // 输入框填"单核档"，这是最直观的上限写法
            if (uv.HasValue) UvBox.Text = uv.Value.ToString();
        }
        finally
        {
            _updating = false;
        }
        CaptureDraftFromUi(announce: false);

        CpuStatusText.Text =
            $"当前：PL1 {st.Pl1W:F0}W · PL2 {st.Pl2W:F0}W · τ {st.TauS:F0}s · EPP {st.Epp}"
            + $" · 睿频 单核 {st.TurboRatio1Core} / 全核 {st.TurboRatioAllCore}（×100MHz）" +
            (uv.HasValue ? $" · 降压 {uv.Value}mV" : "");
        ActionText.Text = "已读取当前 MSR 值";
    }

    private void BtnRestoreCpu_Click(object sender, RoutedEventArgs e)
    {
        _engine.Cpu.Restore();
        _engine.Undervolt.Restore();
        ChkCpuTune.IsChecked = false;
        ChkUv.IsChecked = false;
        ChkAutoCpu.IsChecked = false;
        CaptureDraftFromUi(announce: false);
        CpuStatusText.Text = "已还原 MSR（若原始值仍被占用，请重启系统）";
        AutoStatusText.Text = "已关闭";
        PerfStatusText.Text = "";
        ActionText.Text = "CPU MSR 已还原";
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        if (ProfileCombo.SelectedItem is not string name) return;

        // 取向 = 把一组推荐值套到草稿上（用户能看见每一项变了什么），但不直接写硬件
        _cpuHandEdited = false;   // 预设套用不算手调，不让智能自动让位
        _pc.Edit(cfg => ProfilePreset.Apply(cfg, name));
        LoadConfigToUi(_pc.Draft);
        RefreshPending();
        ShowValidation(_pc.Validate());
        ActionText.Text = $"已套用「{ProfilePreset.Normalize(name)}」推荐值到草稿 —— 点「应用」才生效";
    }

    private void BtnResetCurve_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfilePreset.Normalize(ProfileCombo.SelectedItem as string);
        _pc.Edit(cfg =>
        {
            cfg.FanCurve = Presets.Fan(name);
            cfg.FreqCurve = Presets.Freq(name);
        }, normalize: false);
        LoadConfigToUi(_pc.Draft);
        RefreshPending();
        ActionText.Text = $"已把「{name}」默认曲线放回草稿 —— 点「应用」才生效";
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_updating) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var p in _fanPoints) p.Temperature = Math.Clamp(p.Temperature, 0, 110);
            foreach (var p in _freqPoints) p.Temperature = Math.Clamp(p.Temperature, 0, 110);
            CaptureDraftFromUi(announce: false);
            ActionText.Text = "曲线已更新并生效";
        }), DispatcherPriority.Background);
    }

    private void FanAdd_Click(object sender, RoutedEventArgs e) => AddPoint(_fanPoints, 45);
    private void FreqAdd_Click(object sender, RoutedEventArgs e) => AddPoint(_freqPoints, 60);

    private void AddPoint(ObservableCollection<CurvePoint> list, double fallbackTemp)
    {
        double temp = list.Count == 0 ? fallbackTemp : Math.Min(110, list.Max(p => p.Temperature) + 5);
        double value = list.Count == 0 ? 50 : list.OrderBy(p => p.Temperature).Last().Value;
        list.Add(new CurvePoint(temp, value));
        CaptureDraftFromUi(announce: false);
        ActionText.Text = "已增加曲线点";
    }

    private void FanDel_Click(object sender, RoutedEventArgs e) => DeletePoint(FanGrid, _fanPoints);
    private void FreqDel_Click(object sender, RoutedEventArgs e) => DeletePoint(FreqGrid, _freqPoints);

    private void DeletePoint(DataGrid grid, ObservableCollection<CurvePoint> list)
    {
        if (list.Count <= 2)
        {
            ActionText.Text = "至少保留 2 个曲线点";
            return;
        }
        if (grid.SelectedItem is CurvePoint p)
        {
            list.Remove(p);
            CaptureDraftFromUi(announce: false);
            ActionText.Text = "已删除曲线点";
        }
    }

    // ---------------- 报告 / 日志 ----------------

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReportBox.Text = SupportReport.Build(_engine.Hardware, _engine.Power, _engine.Undervolt, _engine.Config, _engine.Cpu, _settings.Profile);
            File.WriteAllText(Paths.ReportFile, ReportBox.Text);
            ActionText.Text = "报告已导出：" + Paths.ReportFile;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Paths.ReportFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionText.Text = "导出失败：" + ex.Message;
        }
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", Log.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActionText.Text = "打开日志目录失败：" + ex.Message;
        }
    }

    // ---------------- 开机自启 / 托盘 ----------------

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return;

            if (enable)
                Shell2.Run("schtasks", $"/Create /TN \"ThermalPilot\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
            else
                Shell2.Run("schtasks", "/Delete /TN \"ThermalPilot\" /F");

            Log.Info(enable ? "已创建开机自启计划任务（最高权限）。" : "已删除开机自启计划任务。");
        }
        catch (Exception ex)
        {
            Log.Warn("设置开机自启失败：" + ex.Message);
        }
    }

    private static class Shell2
    {
        public static void Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(6000);
        }
    }

    private void SetupTray()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示面板", null, (_, _) => ShowFromTray());
            menu.Items.Add("暂停 / 继续", null, (_, _) => BtnPause_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApplication());

            _tray = new WinForms.NotifyIcon
            {
                Text = "ThermalPilot 温控",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = LoadTrayIcon()
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            Log.Warn("托盘图标创建失败：" + ex.Message);
        }
    }

    private static System.Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null)
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { }
        return null;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || ChkMinTray.IsChecked != true)
        {
            Cleanup();
            return;
        }
        e.Cancel = true;
        Hide();
        if (_tray != null)
        {
            _tray.BalloonTipTitle = "ThermalPilot 仍在后台运行";
            _tray.BalloonTipText = "温控与降频继续生效，双击托盘图标可恢复面板。";
            _tray.ShowBalloonTip(2000);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Cleanup();
        if (_exiting) System.Windows.Application.Current?.Shutdown();
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        try { _cts?.Cancel(); } catch { }
        Log.LineWritten -= OnLogLine;
        _uiTimer.Stop();
        try { _engine.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try
        {
            // 自检模式绝不落盘：自检会用伪造平台（--smoke-caps=amd）跑一遍，
            // 那套"清洗后的配置"一旦存回去，就等于把用户真机上的 MSR 参数抹掉。
            if (!App.SmokeTest)
            {
                _settings.Config = _pc.Committed.Clone();
                SettingsStore.Save(_settings);
            }
        }
        catch { }
    }

    /// <summary>把窗口渲染为 PNG（自检用）。</summary>
    private void CapturePng(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)(ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
