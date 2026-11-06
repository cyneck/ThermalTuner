using System.Windows;
using System.Windows.Controls;

namespace ThermalTuner.App.Windows;

/// <summary>
/// 危险操作二次确认（应用参数 / 恢复原值）。
/// 恢复原值类操作必须输入指定词才解锁确认按钮（PRD AC-13）。
/// </summary>
public partial class ConfirmDialog : Window
{
    private readonly bool _requireTyped;
    private readonly string _typedWord;

    public ConfirmDialog(string title, string summary, IEnumerable<string> items,
        string confirmText = "确认", bool requireTyped = false, string typedWord = "确认")
    {
        InitializeComponent();

        _requireTyped = requireTyped;
        _typedWord = typedWord;

        TitleText.Text = title;
        SummaryText.Text = summary;
        ItemsList.ItemsSource = items.Select(i => "· " + i).ToList();
        ConfirmButton.Content = confirmText;

        if (requireTyped)
        {
            TypedPanel.Visibility = Visibility.Visible;
            TypedHint.Text = $"请输入「{typedWord}」以继续";
            ConfirmButton.IsEnabled = false;
        }
        else
        {
            ConfirmButton.IsEnabled = true;
        }
    }

    private void Typed_Changed(object sender, TextChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = !_requireTyped ||
                                  string.Equals(TypedBox.Text.Trim(), _typedWord, StringComparison.Ordinal);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Ask(Window? owner, string title, string summary, IEnumerable<string> items,
        string confirmText = "确认", bool requireTyped = false, string typedWord = "确认")
    {
        var dlg = new ConfirmDialog(title, summary, items, confirmText, requireTyped, typedWord);
        if (owner is not null) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }
}
