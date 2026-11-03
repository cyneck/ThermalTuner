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

        // 待应用框以「固件实读值」为基线；MSR 读不到时退回决策值。
        // EPP 例外：0x774 读不到时不能拿决策默认值冒充「用户微调值」，否则会被误持久化。
        _syncing = true;
        Pl1Box.Text = (double.IsNaN(s.FwPl1W) ? s.Pl1W : s.FwPl1W).ToString("F1");
        Pl2Box.Text = (double.IsNaN(s.FwPl2W) ? s.Pl2W : s.FwPl2W).ToString("F1");
        TauBox.Text = (double.IsNaN(s.FwTauS) ? s.TauS : s.FwTauS).ToString("F0");
        EppBox.Text = s.EppEditable ? s.FwEpp.ToString("0") : "--";
        // 睿频框填「当前实读的全核档位」：出厂 34 → 填 34，用户改 38 即把各档拉平到 38x
        TurboBox.Text = s.TurboEditable
            ? s.FwTurboAllCoreRatio.ToString(CultureInfo.InvariantCulture) : "--";
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
        EppBox.IsEnabled = unlocked && Session?.EppEditable == true;
        TurboBox.IsEnabled = unlocked && Session?.TurboEditable == true;

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
        EppBox.Text = s.EppEditable ? AutoCpuController.EppForProfile(s.Profile).ToString() : "--";
        TurboBox.Text = s.TurboEditable
            ? s.FwTurboAllCoreRatio.ToString(CultureInfo.InvariantCulture) : "--";
        _syncing = false;

        Validate();
        ShowOutcome($"已按「{s.ProfileLabel}」档位填入默认 PL2 / Tau / EPP，PL1 与睿频倍率保持当前实读值。");
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
        // EPP 不可读时框里是 "--"：不参与解析与校验，用决策值做占位
        int epp = s.EppEditable ? (int)Parse(EppBox.Text, "EPP", ref parseOk) : s.Epp;
        // 睿频倍率不可读时框里是 "--"：不参与解析与校验
        int turbo = s.TurboEditable ? (int)Parse(TurboBox.Text, "睿频倍率", ref parseOk) : 0;
        bool turboRangeOk = true;
        if (s.TurboEditable && parseOk)
        {
            int min = ThermalPilot.Core.Msr.TurboRatioLimit.MinRatio;
            if (turbo < min || turbo > s.FwTurboMaxRatio)
            {
                _violations.Add(new ViolationItem(
                    $"✕ 睿频倍率必须在 [{min}, {s.FwTurboMaxRatio}]（出厂单核上限 {s.FwTurboMaxRatio / 10.0:F1}GHz）；超出属于超频，产品不执行",
                    (Brush)FindResource("Danger")));
                turboRangeOk = false;
            }
        }

        var diffs = BuildDiff();

        if (parseOk)
        {
            foreach (var v in ParamConstraints.Validate(pl1, pl2, tau, epp, s.CeilingC, s.Limits))
            {
                // 固件自带的合法编码（如 Tau 28672s）只是超出建议步长，不是用户输入错误；
                // 未改动该项时不警告，避免「什么都没改却报违规」。
                if (v.Severity == ParamSeverity.Warn && v.Key == "Tau" &&
                    !diffs.Any(d => d.StartsWith("Tau ")))
                    continue;

                _violations.Add(new ViolationItem(
                    (v.Severity == ParamSeverity.Block ? "✕ " : "! ") + v.Message,
                    v.Severity == ParamSeverity.Block
                        ? (Brush)FindResource("Danger")
                        : (Brush)FindResource("Warn")));
            }
        }

        if (_violations.Count == 0)
            _violations.Add(new ViolationItem(
                diffs.Count == 0 ? "✓ 沿用固件当前值，无待应用改动" : "✓ 全部参数通过物理约束校验",
                (Brush)FindResource("Accent")));

        DiffText.Text = diffs.Count == 0 ? "无改动" : $"待应用 {diffs.Count} 项改动";
        ApplyButton.IsEnabled = _unlocked && parseOk && turboRangeOk
                                && !_violations.Any(v => v.Text.StartsWith("✕")) && diffs.Count > 0;
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

        // 改动基线 = 固件实读值（这才是写入会改变的东西）
        double basePl1 = double.IsNaN(s.FwPl1W) ? s.Pl1W : s.FwPl1W;
        double basePl2 = double.IsNaN(s.FwPl2W) ? s.Pl2W : s.FwPl2W;
        double baseTau = double.IsNaN(s.FwTauS) ? s.TauS : s.FwTauS;
        int baseEpp = double.IsNaN(s.FwEpp) ? s.Epp : (int)Math.Round(s.FwEpp);

        if (double.TryParse(Pl1Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl1)
            && Math.Abs(pl1 - basePl1) > 0.05)
            list.Add($"PL1 {basePl1:F1}W → {pl1:F1}W");

        if (double.TryParse(Pl2Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl2)
            && Math.Abs(pl2 - basePl2) > 0.05)
            list.Add($"PL2 {basePl2:F1}W → {pl2:F1}W");

        if (double.TryParse(TauBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tau)
            && Math.Abs(tau - baseTau) > 0.05)
            list.Add($"Tau {baseTau:F0}s → {tau:F0}s");

        // 0x774 读不到时 EPP 不可编辑，也不参与 diff，避免把决策默认值当成用户改动
        if (s.EppEditable && int.TryParse(EppBox.Text, out int epp) && epp != baseEpp)
            list.Add($"EPP {baseEpp} → {epp}");

        // 睿频：基线是「当前实读的全核档位」，目标是把各档统一拉平到输入值
        if (s.TurboEditable && int.TryParse(TurboBox.Text, out int turbo)
            && turbo != s.FwTurboAllCoreRatio)
            list.Add($"睿频 {s.FwTurboAllCoreRatio} → {turbo}x（{turbo / 10.0:F1}GHz）");

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
        // 时间窗口是否改动要与固件实读值比，而不是与决策目标值比
        double tauBase = double.IsNaN(s.FwTauS) ? s.TauS : s.FwTauS;
        bool tauChanged = double.TryParse(TauBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tau)
                          && Math.Abs(tau - tauBase) > 0.05;

        var items = new List<string>(diffs);
        items.Add(real
            ? "写入前自动备份 0x610 原值；回读不一致会立即回滚"
            : "演练模式：不写硬件，仅验证校验与确认流程");

        bool ok = ConfirmDialog.Ask(
            Window.GetWindow(this),
            real ? "确认写入 CPU 寄存器" : "确认应用参数",
            real
                ? "这是真实写入：会立即改变 CPU 功耗上限与睿频倍率表，影响性能、温度与功耗。请确认你知道如何恢复原值。"
                : "以下改动将通过完整确认流程（演练，不写硬件）。",
            items,
            real ? "写入硬件" : "确认应用");

        if (!ok) return;

        if (!real)
        {
            ShowOutcome($"演练完成：{diffs.Count} 项改动已通过校验与确认，未写入硬件，也不会保存到开机重放。当前通道状态：{s.MsrStatusText} —— {s.MsrDetail}");
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
        // 0x774 读不到时用 -1 表示「未微调、不写入、不持久化」，绝不把决策默认值存成用户优化值
        int epp = (s.EppEditable && int.TryParse(EppBox.Text, out int parsedEpp)) ? parsedEpp : -1;
        string eppNote = string.Empty;
        string turboNote = string.Empty;
        if (result.Ok)
        {
            // 睿频倍率：仅当本次 UI 有改动才下发 0x1AD；写入失败不影响功耗参数结果
            if (s.TurboEditable && int.TryParse(TurboBox.Text, out int tv)
                && tv != s.FwTurboAllCoreRatio)
            {
                var tr = s.ApplyTurboIfSupported(tv);
                turboNote = tr.Ok
                    ? $"；睿频倍率已设为 {tv}x（{tv / 10.0:F1}GHz）"
                    : $"；睿频未写入（{tr.Message}）";
            }
            else turboNote = "；睿频倍率未改动";

            // 持久化以「当前实读」为准：即使本次没动睿频，只要仍偏离出厂默认就继续开机重放
            s.SaveManualTuning(pl1, pl2, tau, epp, tauChanged, s.PersistedTurboRatio());
            if (epp >= 0)
            {
                var er = s.ApplyEppIfSupported(epp);
                eppNote = er.Ok ? $"；EPP 已写入 {epp}" : $"；EPP 未写入（{er.Message}）";
            }
            else
            {
                eppNote = "；EPP 本机不可读，未写入也未保存（PL1/PL2/Tau 不受影响）";
            }
        }

        ShowOutcome(result.Ok
            ? $"写入成功：{result.Message}" + (tauChanged ? "" : "（时间窗口保持原值）") +
              eppNote + turboNote + "；参数已保存到本机，开机自动重放。"
            : $"写入失败：{result.Message}（参数未保存）");

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
            items.Add("把 0x1AD 睿频倍率表恢复为出厂档位；0x774 EPP 同样恢复（如可读）");
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
