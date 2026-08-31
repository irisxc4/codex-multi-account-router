using System.Windows;

namespace CodexRouter.Overlay;

public partial class DeviceCodeDialog : Window
{
    public DeviceCodeDialog(string userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode))
        {
            throw new ArgumentException("Device code cannot be empty.", nameof(userCode));
        }
        InitializeComponent();
        CodeTextBox.Text = userCode.Trim();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeTextBox.Text);
        }
        catch
        {
            // Clipboard can be temporarily unavailable when another process owns it.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
