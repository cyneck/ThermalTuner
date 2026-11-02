using System.Windows;
using System.Windows.Controls;
using ThermalPilot.App.Services;

namespace ThermalPilot.App.Windows;

public partial class FirstRunWizard : Window
{
    private static readonly string[] Titles =
    {
        "第 1 / 4 步 · 识别设备",
        "第 2 / 4 步 · 权限与驱动",
        "第 3 / 4 步 · 基线测量",
        "第 4 / 4 步 · 推荐档位"
    };

    private readonly SessionState _session;
    private readonly Border[] _panels;
    private int _step;
    private string _recommended = "balanced";
    private bool _loading;

    public FirstRunWizard(SessionState session)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;

        _panels = new[] { Panel0, Panel1, Panel2, Panel3 };
        DriverHint.Visibility = session.DriverAvailable ? Visibility.Collapsed : Visibility.Visible;

        ShowStep(0);
    }

    /// <summary>用户是否采纳了推荐档位。</summary>
    public bool Adopted { get; private set; }

    public string RecommendedProfile => _recommended;

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, 3);

        for (int i = 0; i < _panels.Length; i++)
            _panels[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = Titles[_step];
        StepBar.Value = _step + 1;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 3 ? "完成" : "下一步";

        if (_step == 3) BuildRecommendation();
    }

    private void BuildRecommendation()
    {
        string reason;
        if (_session.HasRth)
        {
            double predicted = _session.TempC + (_session.Pl1W - _session.PowerW) * _session.Rth;
            bool tooHot = predicted > _session.Limits.MaxSafeCeiling - 3;
            _recommended = tooHot ? "silent" : "balanced";
            reason = $"测得热阻 {_session.Rth:F2} K/W，预测稳态约 {predicted:F1}°C，" +
                     (tooHot ? "建议先降温。" : "散热余量充足。");
        }
        else
        {
            _recommended = _session.Limits.MaxPL1 <= 35 ? "silent" : "balanced";
            reason = _session.Limits.MaxPL1 <= 35
                ? "本机功耗上限较低（轻薄定位），建议静音优先。"
                : "未做基线测量，按均衡档起步最稳妥。";
        }

        RecommendText.Text = $"推荐「{_recommended switch { "silent" => "静音优先", "performance" => "性能优先", _ => "均衡" }}」—— {reason}";

        _loading = true;
        RadioSilent.IsChecked = _recommended == "silent";
        RadioBalanced.IsChecked = _recommended == "balanced";
        RadioPerformance.IsChecked = _recommended == "performance";
        _loading = false;
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string profile })
            _recommended = profile;
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e) => _session.BeginCalibration();

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            ShowStep(_step + 1);
            return;
        }

        Adopted = true;   // 走到「完成」即视为采纳所选档位
        _session.Settings.AutoTakeover = AutoBox.IsChecked == true;
        SettingsStore.Save(_session.Settings);
        DialogResult = true;
        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _session.CancelCalibration();
        Adopted = false;
        DialogResult = true;
        Close();
    }
}
