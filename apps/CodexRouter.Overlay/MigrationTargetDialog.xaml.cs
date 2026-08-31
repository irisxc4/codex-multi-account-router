using System.Windows;
using System.Windows.Input;

namespace CodexRouter.Overlay;

public partial class MigrationTargetDialog : Window
{
    public MigrationTargetDialog(IEnumerable<AccountRowViewModel> accounts)
    {
        InitializeComponent();
        var candidates = accounts
            .Where(static account => account.Enabled && account.RawHealth is not ("AuthRequired" or "Cooldown" or "Disabled"))
            .ToArray();
        AccountList.ItemsSource = candidates;
        if (candidates.Length > 0) AccountList.SelectedIndex = 0;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public string? SelectedAccountId => (AccountList.SelectedItem as AccountRowViewModel)?.Id;

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (SelectedAccountId is null) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SelectedAccountId is not null)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
