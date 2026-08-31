using System.Runtime.InteropServices;
using System.Text;

namespace CodexRouter.Host;

public static class CodexDesktopProcessIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    public static bool Matches(string processName, string? packageFamilyName, string? executablePath)
    {
        if (string.Equals(processName, "Codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (packageFamilyName?.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalized = executablePath.Replace('/', '\\');
        return normalized.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryGetPackageFamilyName(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            uint length = 0;
            var result = GetPackageFamilyName(handle, ref length, null);
            if (result != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            result = GetPackageFamilyName(handle, ref length, buffer);
            return result == 0 ? buffer.ToString() : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    public static string? TryGetProcessImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var capacity = 32768u;
            var buffer = new StringBuilder((int)capacity);
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executableName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
