using System.Windows.Threading;

namespace CodexRouter.Overlay;

public sealed class CodexWindowTracker : IDisposable
{
    private readonly ICodexWindowLocator _locator;
    private readonly DispatcherTimer _timer;
    private readonly int _missingPollTolerance;
    private CodexWindowTarget? _last;
    private int _missingPollCount;
    private bool _disposed;

    public CodexWindowTracker(
        ICodexWindowLocator? locator = null,
        TimeSpan? interval = null,
        int missingPollTolerance = 24)
    {
        if (missingPollTolerance < 0) throw new ArgumentOutOfRangeException(nameof(missingPollTolerance));
        _locator = locator ?? new Win32CodexWindowLocator();
        _missingPollTolerance = missingPollTolerance;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval ?? TimeSpan.FromMilliseconds(120)
        };
        _timer.Tick += OnTick;
    }

    public event EventHandler<CodexWindowTarget?>? TargetChanged;
    public CodexWindowTarget? Current => _last;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = _locator.Find();
        if (current is null && _last is not null)
        {
            if (_missingPollCount++ < _missingPollTolerance)
            {
                return;
            }
        }
        else
        {
            _missingPollCount = 0;
        }

        if (Equivalent(_last, current)) return;
        _last = current;
        TargetChanged?.Invoke(this, current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Poll();

    private static bool Equivalent(CodexWindowTarget? left, CodexWindowTarget? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Hwnd == right.Hwnd &&
               left.Rect == right.Rect &&
               left.Dpi == right.Dpi &&
               left.IsMinimized == right.IsMinimized &&
               left.IsVisible == right.IsVisible;
    }
}
