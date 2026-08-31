using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexRouter.Overlay;

public partial class MainWindow : Window
{
    private readonly OverlayController _controller;
    private readonly OverlayViewModel _viewModel;
    private readonly CodexWindowTracker _tracker;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _quotaRefreshTimer;
    private readonly SemaphoreSlim _quotaRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _quotaRefreshCts = new();
    private readonly OverlayPositionStore _positionStore = new();
    private OverlayPositionPreference? _customPosition;
    private PopoverWindow? _popover;
    private bool _loadedOnce;
    private bool _closing;
    private bool _dragCandidate;
    private bool _dragging;
    private NativePoint _dragStartCursor;
    private int _dragStartWindowLeft;
    private int _dragStartWindowTop;

    public MainWindow(OverlayController controller)
    {
        InitializeComponent();
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _viewModel = new OverlayViewModel();
        DataContext = _viewModel;
        _tracker = new CodexWindowTracker();
        _customPosition = _positionStore.TryLoad();
        _tracker.TargetChanged += OnTargetChanged;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += OnRefreshTick;
        _quotaRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(3)
        };
        _quotaRefreshTimer.Tick += OnQuotaRefreshTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
    }

    public OverlayViewModel ViewModel => _viewModel;
    public OverlayController Controller => _controller;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        Hide();
        _tracker.Start();
        _refreshTimer.Start();
        await RefreshAsync().ConfigureAwait(true);
        // Populate the cache as soon as the overlay starts. The 2-second
        // snapshot timer only paints; this separate job owns network quota sync.
        await RefreshQuotaBatchAsync(force: false).ConfigureAwait(true);
        _quotaRefreshTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        exStyle |= 0x00000080L; // WS_EX_TOOLWINDOW
        exStyle |= 0x08000000L; // WS_EX_NOACTIVATE
        _ = SetWindowLongPtr(hwnd, -20, new IntPtr(exStyle));
    }

    private void OnTargetChanged(object? sender, CodexWindowTarget? target)
    {
        if (_closing) return;
        if (target is null || target.IsMinimized || !target.IsVisible)
        {
            HidePopover();
            Hide();
            return;
        }

        var placement = _customPosition is { IsValid: true } saved
            ? OverlayPlacementCalculator.CalculatePhysical(target, Width, Height, saved.OffsetXDip, saved.OffsetYDip)
            : OverlayPlacementCalculator.CalculatePhysical(target, Width, Height);
        ApplyNativePlacement(placement.Left, placement.Top);
        if (!IsVisible)
        {
            Show();
        }
        if (_popover is { IsVisible: true })
        {
            PositionPopover();
        }
    }

    private async void OnRefreshTick(object? sender, EventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async void OnQuotaRefreshTick(object? sender, EventArgs e) => await RefreshQuotaBatchAsync(force: false).ConfigureAwait(true);

    public Task RefreshNowAsync() => RefreshAsync();

    public void RequestExit()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _controller.GetSnapshotAsync().ConfigureAwait(true);
            _viewModel.Apply(snapshot);
            StatusDot.Fill = StatusBrush(_viewModel.RemainingPercent, _viewModel.SelectedHealth);
            if (_popover is { IsVisible: true })
            {
                _popover.UpdateIntegrationState();
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ex.Message;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 180, 87));
        }
    }

    private async Task<bool> RefreshQuotaBatchAsync(bool force)
    {
        if (!await _quotaRefreshGate.WaitAsync(0, _quotaRefreshCts.Token).ConfigureAwait(true))
        {
            return false;
        }

        try
        {
            // Use the current projection so a newly-added account is included
            // without coupling quota sync to the paint timer.
            var accountIds = _viewModel.Accounts
                .Where(account => account.Enabled && (force || account.NeedsQuotaRefresh))
                .Select(static account => account.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var accountId in accountIds)
            {
                _viewModel.MarkQuotaRefreshStarted(accountId);
                try
                {
                    await _controller.RefreshQuotaAsync(accountId, _quotaRefreshCts.Token).ConfigureAwait(true);
                    _viewModel.MarkQuotaRefreshSucceeded(accountId);
                }
                catch (OperationCanceledException) when (_quotaRefreshCts.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    // A failed poll must not replace a last-known-good snapshot.
                    // Keep the old percentages and expose a retry/stale state.
                    _viewModel.MarkQuotaRefreshFailed(accountId, ex.Message);
                }
            }

            await RefreshAsync().ConfigureAwait(true);
            return accountIds.Length > 0;
        }
        catch (OperationCanceledException) when (_quotaRefreshCts.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _quotaRefreshGate.Release();
        }
    }

    private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !GetCursorPos(out _dragStartCursor)) return;

        var target = _tracker.Current;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (target is null || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect)) return;

        _dragCandidate = true;
        _dragging = false;
        _dragStartWindowLeft = windowRect.Left;
        _dragStartWindowTop = windowRect.Top;
        _ = PillRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var cursor)) return;

        var target = _tracker.Current;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (target is null || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect)) return;

        var deltaX = cursor.X - _dragStartCursor.X;
        var deltaY = cursor.Y - _dragStartCursor.Y;
        if (!_dragging && Math.Abs(deltaX) < 4 && Math.Abs(deltaY) < 4) return;

        _dragging = true;
        HidePopover();
        var placement = OverlayPlacementCalculator.ClampPhysical(
            target,
            Math.Max(1, windowRect.Right - windowRect.Left),
            Math.Max(1, windowRect.Bottom - windowRect.Top),
            _dragStartWindowLeft + deltaX,
            _dragStartWindowTop + deltaY);
        ApplyNativePlacement(placement.Left, placement.Top);
        e.Handled = true;
    }

    private void OnPillMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragCandidate || e.ChangedButton != MouseButton.Left) return;

        var wasDragging = _dragging;
        _dragCandidate = false;
        _dragging = false;
        PillRoot.ReleaseMouseCapture();
        e.Handled = true;

        if (wasDragging)
        {
            SaveCurrentPosition();
            return;
        }

        TogglePopover();
    }

    private void OnPillLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate) return;
        var wasDragging = _dragging;
        _dragCandidate = false;
        _dragging = false;
        if (wasDragging)
        {
            SaveCurrentPosition();
        }
    }

    private void TogglePopover()
    {
        if (_popover is null)
        {
            _popover = new PopoverWindow(this, _controller, _viewModel);
            _popover.IsVisibleChanged += OnPopoverVisibilityChanged;
            _popover.Closed += OnPopoverClosed;
        }
        if (_popover.IsVisible)
        {
            HidePopover();
            return;
        }
        _popover.Show();
        PositionPopover();
        _popover.Activate();
    }

    private void HidePopover()
    {
        if (_popover is { IsVisible: true })
        {
            _popover.Hide();
        }
        SetPopoverOpen(false);
    }

    private void OnPopoverVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SetPopoverOpen(_popover?.IsVisible == true);

    private void OnPopoverClosed(object? sender, EventArgs e)
    {
        if (sender is PopoverWindow popover)
        {
            popover.IsVisibleChanged -= OnPopoverVisibilityChanged;
            popover.Closed -= OnPopoverClosed;
        }
        _popover = null;
        SetPopoverOpen(false);
    }

    private void SetPopoverOpen(bool isOpen)
    {
        ChevronRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation
            {
                To = isOpen ? 180 : 0,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void SaveCurrentPosition()
    {
        var target = _tracker.Current;
        if (target is null) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect)) return;

        var scale = target.Dpi > 0 ? target.Dpi / 96.0 : 1.0;
        var preference = new OverlayPositionPreference(
            (windowRect.Left - target.Rect.Left) / scale,
            (windowRect.Top - target.Rect.Top) / scale);
        _customPosition = preference;
        try
        {
            _positionStore.Save(preference);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = $"{UiText.PositionSaveFailed}: {ex.Message}";
        }
    }

    private void PositionPopover()
    {
        if (_popover is null) return;
        var target = _tracker.Current;
        if (target is null)
        {
            _popover.Left = Left + Width - _popover.Width;
            _popover.Top = Top + Height + 8;
            return;
        }

        var placement = OverlayPlacementCalculator.CalculatePopover(
            target,
            Left,
            Top,
            Width,
            Height,
            _popover.Width,
            Math.Max(_popover.ActualHeight, 520),
            MonitorWorkAreaProvider.GetForWindow(target.Hwnd));
        _popover.Left = placement.Left;
        _popover.Top = placement.Top;
    }

    private void ApplyNativePlacement(int leftPx, int topPx)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            leftPx,
            topPx,
            0,
            0,
            SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    private static Brush StatusBrush(int? remaining, string? selectedHealth)
    {
        if (string.Equals(selectedHealth, "AuthRequired", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedHealth, "Cooldown", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
        if (remaining is null)
        {
            return new SolidColorBrush(Color.FromRgb(148, 153, 163));
        }
        if (remaining < 10)
        {
            return new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
        if (remaining <= 30)
        {
            return new SolidColorBrush(Color.FromRgb(239, 180, 87));
        }
        return new SolidColorBrush(Color.FromRgb(110, 231, 183));
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _quotaRefreshTimer.Stop();
        _quotaRefreshTimer.Tick -= OnQuotaRefreshTick;
        _quotaRefreshCts.Cancel();
        _tracker.TargetChanged -= OnTargetChanged;
        _tracker.Dispose();
        if (_popover is not null)
        {
            _popover.IsVisibleChanged -= OnPopoverVisibilityChanged;
            _popover.Closed -= OnPopoverClosed;
            _popover.Close();
            _popover = null;
        }
        try
        {
            await _controller.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _quotaRefreshCts.Dispose();
            _quotaRefreshGate.Dispose();
            Application.Current.Shutdown();
        }
    }

    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr newLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, newLong) : SetWindowLong32(hWnd, nIndex, newLong);
}
