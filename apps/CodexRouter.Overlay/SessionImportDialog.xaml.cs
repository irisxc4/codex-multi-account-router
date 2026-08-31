using System.Diagnostics;
using System.Windows;
using CodexRouter.Control;

namespace CodexRouter.Overlay;

public partial class SessionImportDialog : Window
{
    private const string SessionPageUrl = "https://chatgpt.com/api/auth/session";
    private readonly string? _proxyCandidate;
    private string? _sessionJson;
    private bool _routeSelected;

    public SessionImportDialog(string? proxyCandidate)
    {
        InitializeComponent();
        _proxyCandidate = string.IsNullOrWhiteSpace(proxyCandidate) ? null : proxyCandidate.Trim();
        if (_proxyCandidate is null)
        {
            DirectRouteRadioButton.IsChecked = true;
            _routeSelected = true;
            ProxyPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ProxyPanel.Visibility = Visibility.Visible;
            ProxyCandidateText.Text = UiText.LocalProxyRoute(_proxyCandidate);
        }
        UpdateImportButton();
    }

    public string? SessionJson => _sessionJson;

    public string? SelectedProxyUrl =>
        _proxyCandidate is not null && ProxyRouteRadioButton.IsChecked == true
            ? _proxyCandidate
            : null;

    public void ClearSensitiveState()
    {
        _sessionJson = null;
        SessionStateText.Text = UiText.SessionNotLoaded;
        UpdateImportButton();
    }

    public static void ClearClipboardIfMatches(string? sessionJson)
    {
        if (string.IsNullOrEmpty(sessionJson)) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var current = Clipboard.GetText();
            if (string.Equals(current, sessionJson, StringComparison.Ordinal)) Clipboard.Clear();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard ownership can change between calls. Never fail a successful account import
            // merely because another process has the clipboard locked.
        }
    }

    private void OnOpenSessionPageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = SessionPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SessionStateText.Text = UiText.SessionPageOpenFailed(ex.Message);
        }
    }

    private void OnReadClipboardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                _sessionJson = null;
                SessionStateText.Text = UiText.SessionClipboardEmpty;
                UpdateImportButton();
                return;
            }

            var parsed = ChatGptSessionImportParser.Parse(text);
            _sessionJson = text;
            SessionStateText.Text = UiText.SessionLoaded(parsed.Email, parsed.PlanType, parsed.ExpiresAt);
            UpdateImportButton();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            _sessionJson = null;
            SessionStateText.Text = UiText.SessionClipboardInvalid(ex.Message);
            UpdateImportButton();
        }
    }

    private void OnRouteSelected(object sender, RoutedEventArgs e)
    {
        _routeSelected = true;
        UpdateImportButton();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sessionJson) || !_routeSelected) return;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearSensitiveState();
        DialogResult = false;
    }

    private void UpdateImportButton()
    {
        ImportButton.IsEnabled = !string.IsNullOrWhiteSpace(_sessionJson) && _routeSelected;
    }
}
