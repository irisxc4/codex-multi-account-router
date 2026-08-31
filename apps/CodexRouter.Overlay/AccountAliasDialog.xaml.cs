using System.Windows;
using System.Windows.Input;

namespace CodexRouter.Overlay;

public partial class AccountAliasDialog : Window
{
    public AccountAliasDialog(string? initialAlias = null, bool editing = false)
    {
        InitializeComponent();
        if (editing)
        {
            Title = UiText.EditDisplayName;
            HeadingText.Text = UiText.EditDisplayName;
            DescriptionText.Text = UiText.EditDisplayNameDescription;
            ConfirmButton.Content = UiText.Save;
        }
        AliasBox.Text = initialAlias?.Trim() ?? string.Empty;
        Loaded += (_, _) =>
        {
            AliasBox.Focus();
            Keyboard.Focus(AliasBox);
            AliasBox.SelectAll();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public string Alias => AliasBox.Text.Trim();

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AliasBox.Text))
        {
            AliasBox.Focus();
            return;
        }
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
        else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(AliasBox.Text))
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
