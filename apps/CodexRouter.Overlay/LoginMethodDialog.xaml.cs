using System.Windows;
using CodexRouter.Control;

namespace CodexRouter.Overlay;

public partial class LoginMethodDialog : Window
{
    private readonly string? _proxyCandidate;
    private readonly bool _routeSelectionRequired;

    public LoginMethodDialog(string? proxyCandidate = null)
    {
        InitializeComponent();
        _proxyCandidate = proxyCandidate;
        _routeSelectionRequired = !string.IsNullOrWhiteSpace(proxyCandidate);
        if (_routeSelectionRequired)
        {
            ProxyCandidateText.Text = UiText.LoginProxyOption(proxyCandidate!);
            ProxyPanel.Visibility = Visibility.Visible;
            SetLoginButtonsEnabled(false);
        }
    }

    public string? SelectedMethod { get; private set; }
    public string? SelectedProxyUrl { get; private set; }

    private void OnBrowserClick(object sender, RoutedEventArgs e) => Select(ControlLoginMethods.Browser);
    private void OnDeviceClick(object sender, RoutedEventArgs e) => Select(ControlLoginMethods.Device);

    private void OnRouteSelected(object sender, RoutedEventArgs e)
    {
        if (!_routeSelectionRequired) return;
        SetLoginButtonsEnabled(DirectRouteRadioButton.IsChecked == true || ProxyRouteRadioButton.IsChecked == true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Select(string method)
    {
        if (_routeSelectionRequired && DirectRouteRadioButton.IsChecked != true && ProxyRouteRadioButton.IsChecked != true)
        {
            return;
        }

        SelectedMethod = method;
        SelectedProxyUrl = ProxyRouteRadioButton.IsChecked == true ? _proxyCandidate : null;
        DialogResult = true;
        Close();
    }

    private void SetLoginButtonsEnabled(bool enabled)
    {
        BrowserLoginButton.IsEnabled = enabled;
        DeviceLoginButton.IsEnabled = enabled;
    }
}
