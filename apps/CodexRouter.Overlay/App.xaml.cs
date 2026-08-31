using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CodexRouter.Overlay;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            try { _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
        }

        _singleInstance = new Mutex(initiallyOwned: true, "Local\\CodexRouterOverlay-v1", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var controller = new OverlayController();
        var window = new MainWindow(controller);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
