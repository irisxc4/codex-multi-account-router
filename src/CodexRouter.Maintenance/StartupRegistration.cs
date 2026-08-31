using Microsoft.Win32;

namespace CodexRouter.Maintenance;

public interface IStartupRegistration
{
    bool IsEnabled(string executablePath);
    void Enable(string executablePath);
    void Disable();
}

public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "CodexRouterOverlay";

    public bool IsEnabled(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var current = key?.GetValue(ValueName) as string;
        return string.Equals(Unquote(current), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Enable(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Startup registration is Windows-only.");
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Overlay executable is missing.", executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");
        key.SetValue(ValueName, Quote(executablePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
    private static string? Unquote(string? value) => value?.Trim().Trim('"');
}
