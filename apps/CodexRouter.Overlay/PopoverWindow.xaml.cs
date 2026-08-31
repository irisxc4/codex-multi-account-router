using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexRouter.Control;
using CodexRouter.Host;

namespace CodexRouter.Overlay;

public partial class PopoverWindow : Window
{
    private readonly MainWindow _pill;
    private readonly OverlayController _controller;
    private readonly OverlayViewModel _viewModel;
    private CancellationTokenSource? _migrationPollCts;
    private string? _migrationJobId;
    private string? _migrationTargetAccountId;
    private bool _pinMigrationTargetOnCompletion;
    private bool _migrationRunning;
    private bool _migrationFailed;
    private bool _suppressAutoHide;
    private bool _busy;
    private OfficialLoginCoordinator? _activeLogin;
    private CancellationTokenSource? _loginPollCts;

    public PopoverWindow(MainWindow pill, OverlayController controller, OverlayViewModel viewModel)
    {
        InitializeComponent();
        _pill = pill;
        _controller = controller;
        _viewModel = viewModel;
        Owner = pill;
        DataContext = viewModel;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
        Loaded += (_, _) => UpdateIntegrationState();
    }

    public void UpdateIntegrationState()
    {
        var shim = ResolveShimPath();
        var probe = _controller.ProbeDesktopIntegration(shim);
        var statusText = probe.Status switch
        {
            DesktopIntegrationStatus.Active => UiText.IntegrationOn,
            DesktopIntegrationStatus.Conflict => UiText.IntegrationConflict,
            DesktopIntegrationStatus.ShimMissing => UiText.IntegrationShimMissing,
            _ => UiText.IntegrationOff
        };
        var actionText = probe.Status == DesktopIntegrationStatus.Active
            ? UiText.ReleaseDesktopIntegration
            : UiText.EnableDesktopIntegration;
        _viewModel.SetIntegrationPresentation(statusText, actionText);
    }

    private async void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _migrationRunning || sender is not Button { Tag: string accountId }) return;
        await RunUiActionAsync(async () =>
        {
            var action = AccountSwitchPolicy.Decide(
                _viewModel.CurrentThreadId,
                _viewModel.CurrentThreadAccountId,
                accountId);
            if (action == AccountSwitchAction.PinOnly)
            {
                await _controller.PinAsync(accountId).ConfigureAwait(true);
                await _pill.RefreshNowAsync().ConfigureAwait(true);
                _viewModel.StatusText = UiText.PinnedNewThreads;
                return;
            }

            await StartMigrationAndTrackAsync(
                _viewModel.CurrentThreadId!,
                accountId,
                pinOnCompletion: true).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnRoutingToggleClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunUiActionAsync(async () =>
        {
            var statusText = UiText.AutoEnabled;
            if (_viewModel.IsRoutingEnabled)
            {
                // Turning the product feature off is a live policy change. Keep
                // Desktop integration installed so it can be re-enabled without
                // rewriting the user's environment again.
                await _controller.SetRouterOffAsync().ConfigureAwait(true);
                statusText = UiText.RouterNativeMode;
            }
            else
            {
                var probe = _controller.ProbeDesktopIntegration(ResolveShimPath());
                var restartRequired = false;
                if (probe.Status != DesktopIntegrationStatus.Active)
                {
                    var shim = ResolveShimPath();
                    if (!File.Exists(shim))
                    {
                        throw new FileNotFoundException(UiText.IntegrationBinaryMissing, shim);
                    }

                    var change = await _controller.EnableDesktopIntegrationAsync(shim).ConfigureAwait(true);
                    if (change.Status != DesktopIntegrationStatus.Active)
                    {
                        _viewModel.StatusText = UiText.IntegrationEnableFailed(change.Message);
                        UpdateIntegrationState();
                        return;
                    }
                    restartRequired = change.Changed;
                }

                await _controller.SetAutoAsync().ConfigureAwait(true);
                statusText = restartRequired
                    ? UiText.RoutingEnabledNeedsRestart
                    : UiText.AutoEnabled;
            }
            await _pill.RefreshNowAsync().ConfigureAwait(true);
            _viewModel.StatusText = statusText;
        }).ConfigureAwait(true);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _suppressAutoHide = true;
        _pill.RequestExit();
    }

    private async void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        if (_activeLogin is not null)
        {
            _viewModel.StatusText = UiText.LoginCanceling;
            _loginPollCts?.Cancel();
            return;
        }
        if (_busy) return;

        _suppressAutoHide = true;
        try
        {
            var proxyCandidate = LoginProxyDetector.TryDetectLocalProxyUrl();
            var dialog = new LoginMethodDialog(proxyCandidate) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedMethod))
            {
                return;
            }

            var aliasDialog = new AccountAliasDialog { Owner = this };
            if (aliasDialog.ShowDialog() != true)
            {
                return;
            }

            await RunUiActionAsync(() => RunOfficialLoginAsync(
                dialog.SelectedMethod,
                dialog.SelectedProxyUrl,
                aliasDialog.Alias)).ConfigureAwait(true);
        }
        finally
        {
            _suppressAutoHide = false;
        }
    }

    private async Task RunOfficialLoginAsync(string loginMethod, string? proxyUrl, string alias)
    {
        await using var login = new OfficialLoginCoordinator(_controller);
        using var loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _activeLogin = login;
        _loginPollCts = loginCts;
        SetLoginButtonState(loginRunning: true);
        DeviceCodeDialog? deviceDialog = null;
        try
        {
            _viewModel.StatusText = UiText.OfficialLoginStarting;
            var start = await login.StartAsync(alias, loginMethod, proxyUrl, loginCts.Token).ConfigureAwait(true);

            if (loginMethod == ControlLoginMethods.Device)
            {
                deviceDialog = new DeviceCodeDialog(start.UserCode!) { Owner = this };
                deviceDialog.Show();
                _viewModel.StatusText = UiText.DeviceLoginOpened(start.UserCode!);
            }
            else
            {
                _viewModel.StatusText = UiText.BrowserLoginOpened;
            }

            var result = await login.WaitForCompletionAsync(cancellationToken: loginCts.Token).ConfigureAwait(true);
            if (!string.Equals(result.State, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.StatusText = UiText.OfficialLoginFailed(result.Error);
                return;
            }

            _viewModel.StatusText = UiText.OfficialLoginSucceeded(result.Email, result.PlanType);
            await _pill.RefreshNowAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (loginCts.IsCancellationRequested)
        {
            _viewModel.StatusText = UiText.LoginCanceled;
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = UiText.OfficialLoginFailed(ex.Message);
        }
        finally
        {
            deviceDialog?.Close();
            if (ReferenceEquals(_activeLogin, login)) _activeLogin = null;
            if (ReferenceEquals(_loginPollCts, loginCts)) _loginPollCts = null;
            SetLoginButtonState(loginRunning: false);
        }
    }

    private async void OnRenameAccountClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string accountId }) return;
        var account = _viewModel.Accounts.FirstOrDefault(item =>
            string.Equals(item.Id, accountId, StringComparison.Ordinal));
        if (account is null) return;

        _suppressAutoHide = true;
        try
        {
            var dialog = new AccountAliasDialog(account.Alias, editing: true) { Owner = this };
            if (dialog.ShowDialog() != true ||
                string.Equals(dialog.Alias, account.Alias, StringComparison.Ordinal))
            {
                return;
            }

            await RunUiActionAsync(async () =>
            {
                await _controller.RenameAccountAsync(accountId, dialog.Alias).ConfigureAwait(true);
                await _pill.RefreshNowAsync().ConfigureAwait(true);
                _viewModel.StatusText = UiText.DisplayNameUpdated;
            }).ConfigureAwait(true);
        }
        finally
        {
            _suppressAutoHide = false;
        }
    }

    private void SetLoginButtonState(bool loginRunning)
    {
        var content = loginRunning ? UiText.CancelCurrentLogin : UiText.AddChatGpt;
        AddAccountButton.Content = content;
        EmptyAddAccountButton.Content = loginRunning ? UiText.CancelCurrentLogin : UiText.ConnectChatGpt;
    }

    private async void OnMigrateCurrentClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_migrationRunning && _migrationJobId is not null)
        {
            await RunUiActionAsync(async () =>
            {
                var status = await _controller.CancelMigrationAsync(_migrationJobId).ConfigureAwait(true);
                _migrationPollCts?.Cancel();
                _migrationRunning = false;
                _migrationFailed = false;
                _migrationTargetAccountId = null;
                _pinMigrationTargetOnCompletion = false;
                MigrationButton.Content = UiText.MigrateCurrent;
                _viewModel.StatusText = UiText.MigrationState(status.State);
            }).ConfigureAwait(true);
            return;
        }

        if (_migrationFailed && _migrationJobId is not null)
        {
            await RunUiActionAsync(async () =>
            {
                var retry = await _controller.RetryMigrationAsync(_migrationJobId).ConfigureAwait(true);
                _migrationRunning = true;
                _migrationFailed = false;
                MigrationButton.Content = UiText.CancelMigration;
                _viewModel.StatusText = UiText.MigrationRetry(retry.State);
                StartMigrationPolling(retry.JobId, _migrationTargetAccountId, _pinMigrationTargetOnCompletion);
            }).ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.CurrentThreadId))
        {
            _viewModel.StatusText = UiText.NoCurrentThread;
            return;
        }
        var candidates = _viewModel.Accounts
            .Where(account => account.Id != (_viewModel.CurrentThreadAccountId ?? _viewModel.CurrentAccountId))
            .ToArray();
        if (candidates.Length == 0)
        {
            _viewModel.StatusText = UiText.NoMigrationTarget;
            return;
        }

        _suppressAutoHide = true;
        try
        {
            var dialog = new MigrationTargetDialog(candidates) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedAccountId is null) return;
            await RunUiActionAsync(async () =>
            {
                await StartMigrationAndTrackAsync(
                    _viewModel.CurrentThreadId!,
                    dialog.SelectedAccountId,
                    pinOnCompletion: true).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        finally
        {
            _suppressAutoHide = false;
        }
    }

    private async void OnIntegrationClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunUiActionAsync(async () =>
        {
            var shim = ResolveShimPath();
            var probe = _controller.ProbeDesktopIntegration(shim);
            if (probe.Status == DesktopIntegrationStatus.Active)
            {
                _ = await _controller.DisableDesktopIntegrationAsync().ConfigureAwait(true);
                _viewModel.StatusText = UiText.IntegrationDisabledStatus;
            }
            else
            {
                if (!File.Exists(shim))
                {
                    throw new FileNotFoundException(UiText.IntegrationBinaryMissing, shim);
                }
                var change = await _controller.EnableDesktopIntegrationAsync(shim).ConfigureAwait(true);
                _viewModel.StatusText = change.Status == DesktopIntegrationStatus.Active
                    ? UiText.IntegrationEnabledStatus
                    : UiText.IntegrationEnableFailed(change.Message);
            }
            UpdateIntegrationState();
        }).ConfigureAwait(true);
    }

    private async Task StartMigrationAndTrackAsync(
        string sourceThreadId,
        string targetAccountId,
        bool pinOnCompletion)
    {
        if (_migrationRunning) return;

        var start = await _controller.StartMigrationAsync(sourceThreadId, targetAccountId).ConfigureAwait(true);
        _migrationJobId = start.JobId;
        _migrationTargetAccountId = targetAccountId;
        _pinMigrationTargetOnCompletion = pinOnCompletion;
        _migrationRunning = true;
        _migrationFailed = false;
        MigrationButton.Content = UiText.CancelMigration;
        _viewModel.StatusText = UiText.MigrationStarted(start.State);
        StartMigrationPolling(start.JobId, targetAccountId, pinOnCompletion);
    }

    private void StartMigrationPolling(string jobId, string? targetAccountId = null, bool pinOnCompletion = false)
    {
        _migrationPollCts?.Cancel();
        _migrationPollCts?.Dispose();
        _migrationPollCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _ = PollMigrationAsync(jobId, targetAccountId, pinOnCompletion, _migrationPollCts.Token);
    }

    private async Task PollMigrationAsync(
        string jobId,
        string? targetAccountId,
        bool pinOnCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await _controller.GetMigrationStatusAsync(jobId, cancellationToken).ConfigureAwait(true);
                _viewModel.StatusText = status.State switch
                {
                    "Completed" => UiText.MigrationCompleted(status.TargetThreadId),
                    "Failed" => UiText.MigrationFailed(status.Error),
                    "Canceled" => UiText.MigrationCanceled,
                    _ => UiText.MigrationState(status.State)
                };

                if (status.State == "Completed")
                {
                    _migrationRunning = false;
                    _migrationFailed = false;
                    _migrationJobId = null;
                    MigrationButton.Content = UiText.MigrateCurrent;
                    if (pinOnCompletion)
                    {
                        if (string.IsNullOrWhiteSpace(targetAccountId) || string.IsNullOrWhiteSpace(status.TargetThreadId))
                        {
                            _migrationTargetAccountId = null;
                            _pinMigrationTargetOnCompletion = false;
                            _viewModel.StatusText = UiText.MigrationPinFailed(UiText.UnknownError);
                            return;
                        }

                        try
                        {
                            // Commit the destination route only after migration has
                            // completed. The source thread is never changed.
                            await _controller.PinAsync(targetAccountId).ConfigureAwait(true);
                            await _pill.RefreshNowAsync().ConfigureAwait(true);
                            try
                            {
                                var uri = ThreadDeepLink.Create(status.TargetThreadId);
                                using var process = Process.Start(new ProcessStartInfo
                                {
                                    FileName = uri.ToString(),
                                    UseShellExecute = true
                                });
                                _viewModel.StatusText = process is null
                                    ? UiText.MigrationCompletedOpenFailed(status.TargetThreadId, UiText.UnknownError)
                                    : UiText.MigrationCompletedAndPinned(status.TargetThreadId);
                            }
                            catch (Exception ex)
                            {
                                _viewModel.StatusText = UiText.MigrationCompletedOpenFailed(status.TargetThreadId, ex.Message);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Pin failure leaves the previous route untouched.
                            _viewModel.StatusText = UiText.MigrationPinFailed(ex.Message);
                        }
                    }
                    else
                    {
                        await _pill.RefreshNowAsync().ConfigureAwait(true);
                    }
                    _migrationTargetAccountId = null;
                    _pinMigrationTargetOnCompletion = false;
                    return;
                }
                if (status.State == "Failed")
                {
                    _migrationRunning = false;
                    _migrationFailed = true;
                    _migrationJobId = jobId;
                    MigrationButton.Content = UiText.RetryMigration;
                    return;
                }
                if (status.State == "Canceled")
                {
                    _migrationRunning = false;
                    _migrationFailed = false;
                    _migrationJobId = null;
                    _migrationTargetAccountId = null;
                    _pinMigrationTargetOnCompletion = false;
                    MigrationButton.Content = UiText.MigrateCurrent;
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_migrationRunning) _viewModel.StatusText = UiText.MigrationPollingStopped;
        }
        catch (Exception ex)
        {
            _migrationRunning = false;
            _migrationFailed = true;
            _migrationJobId = jobId;
            MigrationButton.Content = UiText.RetryMigration;
            _viewModel.StatusText = UiText.MigrationStatusError(ex.Message);
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_suppressAutoHide && !_busy)
        {
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _loginPollCts?.Cancel();
        _migrationPollCts?.Cancel();
        _migrationPollCts?.Dispose();
        _migrationPollCts = null;
        Deactivated -= OnDeactivated;
        Closed -= OnClosed;
    }

    private static string ResolveShimPath() => Path.Combine(AppContext.BaseDirectory, "codex-route.exe");
}
