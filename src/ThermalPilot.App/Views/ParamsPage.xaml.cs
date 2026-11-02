using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThermalPilot.App.Services;
using ThermalPilot.App.Windows;
using ThermalPilot.Core;

namespace ThermalPilot.App.Views;

public partial class ParamsPage : UserControl
{
    private sealed record ViolationItem(string Text, Brush Brush);

    private readonly ObservableCollection<ViolationItem> _violations = new();
    private bool _unlocked;
    private bool _syncing;

    public ParamsPage()
    {
        InitializeComponent();
        ViolationList.ItemsSource = _violations;
        Loaded += (_, _) => { SyncFromSession(); Validate(); RefreshMsrUi(); };
    }

    private SessionState? Session => DataContext as SessionState;

    // ---------------- 同步 / 编辑态 ----------------

    private void SyncFromSession()
    {
        if (Session is not { } s) return;

        _syncing = true;
        Pl1Box.Text = s.Pl1W.ToString("F1");
        Pl2Box.Text = s.Pl2W.ToString("F1");
        TauBox.Text = s.TauS.ToString("F0");
        EppBox.Text = s.Epp.ToString();
        _syncing = false;

        ApplyLockState(false);   // 注意：这里不能调 SetUnlocked，否则与 SyncFromSession 互相递归
        Validate();
    }

    private void SetUnlocked(bool unlocked)
    {
        ApplyLockState(unlocked);
        if (!unlocked) SyncFromSession();
    }

    /// <summary>只切换编辑态与视觉，不同步数值（避免递归）。</summary>
    private void ApplyLockState(bool unlocked)
    {
        _unlocked = unlocked;
        Pl1Box.IsEnabled = unlocked;
        Pl2Box.IsEnabled = unlocked;
        TauBox.IsEnabled = unlocked;
        EppBox.IsEnabled = unlocked;

        UnlockButton.Content = unlocked ? "锁定（放弃编辑）" : "解锁编辑";
        ModeChip.Background = unlocked ? (Brush)FindResource("WarnSoft") : (Brush)FindResource("BgSubtle");
        ModeChip.BorderBrush = unlocked ? (Brush)FindResource("Warn") : (Brush)FindResource("Border");
        ModeChipText.Text = unlocked ? "编辑中" : "只读";
        ModeChipText.Foreground = unlocked ? (Brush)FindResource("Warn") : (Brush)FindResource("TextSecondary");

    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        SetUnlocked(!_unlocked);
        if (_unlocked)
            ShowOutcome(Session is { RealWriteEnabled: true }
                ? "已解锁：修改后点「应用（真实写入）」，写入会先备份原值并回读校验。"
                : "已解锁：修改后点「应用（演练）」。当前通道不可写，只走校验与确认流程。");
        else
            OutcomeBanner.Visibility = Visibility.Collapsed;
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        SyncFromSession();
        ShowOutcome("已撤销所有未应用修改。");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        // 按当前档位的默认比例推导 PL2 / Tau / EPP，PL1 保持生效值
        _syncing = true;
        Pl2Box.Text = (s.Pl1W * AutoCpuController.Pl2Ratio(s.Profile)).ToString("F1");
        TauBox.Text = AutoCpuController.TauForProfile(s.Profile).ToString("F0");
        EppBox.Text = AutoCpuController.EppForProfile(s.Profile).ToString();
        _syncing = false;

        Validate();
        ShowOutcome($"已按「{s.ProfileLabel}」档位填入默认 PL2 / Tau / EPP，PL1 保持当前值。");
    }

    // ---------------- 校验 ----------------

    private void Value_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Validate();
    }

    private void Validate()
    {
        if (Session is not { } s) return;

        _violations.Clear();
        bool parseOk = true;

        double pl1 = Parse(Pl1Box.Text, "PL1", ref parseOk);
        double pl2 = Parse(Pl2Box.Text, "PL2", ref parseOk);
        double tau = Parse(TauBox.Text, "Tau", ref parseOk);
        int epp = (int)Parse(EppBox.Text, "EPP", ref parseOk);

        if (parseOk)
        {
            foreach (var v in ParamConstraints.Validate(pl1, pl2, tau, epp, s.CeilingC, s.Limits))
            {
                _violations.Add(new ViolationItem(
                    (v.Severity == ParamSeverity.Block ? "✕ " : "! ") + v.Message,
                    v.Severity == ParamSeverity.Block
                        ? (Brush)FindResource("Danger")
                        : (Brush)FindResource("Warn")));
            }
        }

        if (_violations.Count == 0)
            _violations.Add(new ViolationItem("✓ 全部参数通过物理约束校验", (Brush)FindResource("Accent")));

        var diffs = BuildDiff();
        DiffText.Text = diffs.Count == 0 ? "无改动" : $"待应用 {diffs.Count} 项改动";
        ApplyButton.IsEnabled = _unlocked && parseOk && !_violations.Any(v => v.Text.StartsWith("✕")) && diffs.Count > 0;
    }

    private double Parse(string text, string key, ref bool ok)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        ok = false;
        _violations.Add(new ViolationItem($"✕ {key} 不是合法数字", (Brush)FindResource("Danger")));
        return double.NaN;
    }

    private List<string> BuildDiff()
    {
        var list = new List<string>();
        if (Session is not { } s) return list;

        if (double.TryParse(Pl1Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl1)
            && Math.Abs(pl1 - s.Pl1W) > 0.05)
            list.Add($"PL1 {s.Pl1W:F1}W → {pl1:F1}W");

        if (double.TryParse(Pl2Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl2)
            && Math.Abs(pl2 - s.Pl2W) > 0.05)
            list.Add($"PL2 {s.Pl2W:F1}W → {pl2:F1}W");

        if (double.TryParse(TauBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tau)
            && Math.Abs(tau - s.TauS) > 0.05)
            list.Add($"Tau {s.TauS:F0}s → {tau:F0}s");

        if (int.TryParse(EppBox.Text, out int epp) && epp != s.Epp)
            list.Add($"EPP {s.Epp} → {epp}");

        return list;
    }

    // ---------------- 危险操作 ----------------

    private void RefreshMsrUi()
    {
        if (Session is not { } s) return;

        bool real = s.RealWriteEnabled;
        MsrChipText.Text = s.MsrSummary;
        MsrChip.Background = real ? (Brush)FindResource("AccentSoft") : (Brush)FindResource("WarnSoft");
        MsrChip.BorderBrush = real ? (Brush)FindResource("Accent") : (Brush)FindResource("Warn");
        MsrChipText.Foreground = real ? (Brush)FindResource("AccentDeep") : (Brush)FindResource("Warn");

        ApplyButton.Content = real ? "应用（真实写入）" : "应用（演练）";
        MsrHint.Text = real
            ? "真实写入已开启：会先备份原值，写入后回读校验，失败自动回滚。"
            : $"当前为演练模式（{s.MsrStatusText}）：会完整走校验与确认，但不写硬件。";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        var diffs = BuildDiff();
        if (diffs.Count == 0) { ShowOutcome("没有需要应用的改动。"); return; }

        bool real = s.RealWriteEnabled;
        bool tauChanged = double.TryParse(TauBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tau)
                          && Math.Abs(tau - s.TauS) > 0.05;

        var items = new List<string>(diffs);
        items.Add(real
            ? "写入前自动备份 0x610 原值；回读不一致会立即回滚"
            : "演练模式：不写硬件，仅验证校验与确认流程");

        bool ok = ConfirmDialog.Ask(
            Window.GetWindow(this),
            real ? "确认写入 CPU 功耗寄存器" : "确认应用参数",
            real
                ? "这是真实写入：会立即改变 CPU 功耗上限，影响性能与温度。请确认你知道如何恢复原值。"
                : "以下改动将通过完整确认流程（演练，不写硬件）。",
            items,
            real ? "写入硬件" : "确认应用");

        if (!ok) return;

        if (!real)
        {
            ShowOutcome($"演练完成：{diffs.Count} 项改动已通过校验与确认，未写入硬件。当前通道状态：{s.MsrStatusText} —— {s.MsrDetail}");
            s.AddAlert("info", "参数应用（演练）", string.Join("；", diffs));
            return;
        }

        if (!double.TryParse(Pl1Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl1) ||
            !double.TryParse(Pl2Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl2))
        {
            ShowOutcome("参数解析失败，已中止写入。");
            return;
        }

        var result = s.ApplyLimits(pl1, pl2, tau, writeTimeWindow: tauChanged);
        ShowOutcome(result.Ok
            ? $"写入成功：{result.Message}" + (tauChanged ? "" : "（时间窗口保持原值）")
            : $"写入失败：{result.Message}");

        RefreshMsrUi();
        SyncFromSession();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;

        var items = new List<string>();
        if (s.HasMsrBackup && s.MsrWritable)
        {
            items.Add("将把 0x610 写回启动时备份的原始值（PL1/PL2/时间窗口）");
            items.Add("恢复后立即回读校验，不一致则报错并保留审计记录");
        }
        else
        {
            items.Add("当前为演练模式，不会写硬件");
            items.Add($"通道状态：{s.MsrStatusText} —— {s.MsrDetail}");
        }

        bool ok = ConfirmDialog.Ask(
            Window.GetWindow(this),
            "恢复原值",
            "此操作会把全部功耗参数恢复为启动时备份，属于不可逆写操作，请输入确认词。",
            items,
            "恢复原值",
            requireTyped: true,
            typedWord: "恢复");

        if (!ok) return;

        if (s.HasMsrBackup && s.MsrWritable)
        {
            var result = s.RestoreOriginal();
            ShowOutcome(result.Ok ? $"已恢复：{result.Message}" : $"恢复失败：{result.Message}");
            SyncFromSession();
            RefreshMsrUi();
            return;
        }

        ShowOutcome($"演练完成：已请求恢复原值（当前通道 {s.MsrStatusText}，不写硬件）。真实恢复将依次执行 写入备份值 → 回读校验 → 审计日志。");
        s.AddAlert("warn", "恢复原值（演练）", "用户请求恢复启动时备份参数。");
    }

    private void ShowOutcome(string text)
    {
        OutcomeText.Text = text;
        OutcomeBanner.Visibility = Visibility.Visible;
    }
}
