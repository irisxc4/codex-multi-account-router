using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexRouter.Host;

namespace CodexRouter.Overlay;

public readonly record struct NativeWindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

public sealed record CodexWindowTarget(
    IntPtr Hwnd,
    int ProcessId,
    NativeWindowRect Rect,
    uint Dpi,
    bool IsMinimized,
    bool IsVisible);

public interface ICodexWindowLocator
{
    CodexWindowTarget? Find();
}

public sealed class Win32CodexWindowLocator : ICodexWindowLocator
{
    public CodexWindowTarget? Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = new List<CodexWindowTarget>();
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero)
            {
                return true;
            }
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !IsCodexProcess((int)pid))
            {
                return true;
            }
            if (!GetWindowRect(hwnd, out var rect))
            {
                return true;
            }
            var native = new NativeWindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (native.Width < 400 || native.Height < 200)
            {
                return true;
            }
            uint dpi;
            try { dpi = GetDpiForWindow(hwnd); } catch (EntryPointNotFoundException) { dpi = 96; }
            candidates.Add(new CodexWindowTarget(
                hwnd,
                (int)pid,
                native,
                dpi == 0 ? 96u : dpi,
                IsIconic(hwnd),
                true));
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(static target => target.Rect.Width * (long)target.Rect.Height)
            .ThenBy(static target => target.ProcessId)
            .FirstOrDefault();
    }

    private static bool IsCodexProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            if (CodexDesktopProcessIdentity.Matches(processName, null, null))
            {
                return true;
            }
            if (!string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return CodexDesktopProcessIdentity.Matches(
                processName,
                CodexDesktopProcessIdentity.TryGetPackageFamilyName(processId),
                CodexDesktopProcessIdentity.TryGetProcessImagePath(processId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

}

public readonly record struct OverlayPlacement(double Left, double Top, double DpiScale);
public readonly record struct NativeOverlayPlacement(int Left, int Top, int Width, int Height, double DpiScale);

public readonly record struct NativeMonitorWorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

public static class OverlayPlacementCalculator
{
    public static NativeOverlayPlacement CalculatePhysical(
        CodexWindowTarget target,
        double overlayWidthDip,
        double overlayHeightDip,
        double offsetXDip = 220,
        double offsetYDip = 5,
        double edgeInsetDip = 4)
    {
        if (overlayWidthDip <= 0 || overlayHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(overlayWidthDip));
        if (!double.IsFinite(offsetXDip) || !double.IsFinite(offsetYDip)) throw new ArgumentOutOfRangeException(nameof(offsetXDip));

        var scale = target.Dpi > 0 ? target.Dpi / 96.0 : 1.0;
        var widthPx = Math.Max(1, (int)Math.Ceiling(overlayWidthDip * scale));
        var heightPx = Math.Max(1, (int)Math.Ceiling(overlayHeightDip * scale));
        var desiredLeftPx = target.Rect.Left + (int)Math.Round(offsetXDip * scale);
        var desiredTopPx = target.Rect.Top + (int)Math.Round(offsetYDip * scale);
        return ClampPhysical(target, widthPx, heightPx, desiredLeftPx, desiredTopPx, edgeInsetDip);
    }

    public static NativeOverlayPlacement ClampPhysical(
        CodexWindowTarget target,
        int overlayWidthPx,
        int overlayHeightPx,
        int desiredLeftPx,
        int desiredTopPx,
        double edgeInsetDip = 4)
    {
        if (overlayWidthPx <= 0 || overlayHeightPx <= 0) throw new ArgumentOutOfRangeException(nameof(overlayWidthPx));
        var scale = target.Dpi > 0 ? target.Dpi / 96.0 : 1.0;
        var edgePx = Math.Max(0, (int)Math.Ceiling(edgeInsetDip * scale));
        var minLeft = target.Rect.Left + edgePx;
        var minTop = target.Rect.Top + edgePx;
        var maxLeft = Math.Max(minLeft, target.Rect.Right - edgePx - overlayWidthPx);
        var maxTop = Math.Max(minTop, target.Rect.Bottom - edgePx - overlayHeightPx);
        return new NativeOverlayPlacement(
            Math.Clamp(desiredLeftPx, minLeft, maxLeft),
            Math.Clamp(desiredTopPx, minTop, maxTop),
            overlayWidthPx,
            overlayHeightPx,
            scale);
    }

    public static OverlayPlacement Calculate(
        CodexWindowTarget target,
        double overlayWidthDip,
        double overlayHeightDip,
        double leftInsetDip = 220,
        double topInsetDip = 5)
    {
        return CalculateRelative(target, overlayWidthDip, overlayHeightDip, leftInsetDip, topInsetDip);
    }

    public static OverlayPlacement CalculateRelative(
        CodexWindowTarget target,
        double overlayWidthDip,
        double overlayHeightDip,
        double offsetXDip,
        double offsetYDip,
        double edgeInsetDip = 4)
    {
        if (overlayWidthDip <= 0 || overlayHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(overlayWidthDip));
        if (!double.IsFinite(offsetXDip) || !double.IsFinite(offsetYDip)) throw new ArgumentOutOfRangeException(nameof(offsetXDip));

        var scale = Math.Max(1.0, target.Dpi / 96.0);
        var targetLeft = target.Rect.Left / scale;
        var targetTop = target.Rect.Top / scale;
        var targetRight = target.Rect.Right / scale;
        var targetBottom = target.Rect.Bottom / scale;
        var minLeft = targetLeft + edgeInsetDip;
        var minTop = targetTop + edgeInsetDip;
        var maxLeft = Math.Max(minLeft, targetRight - edgeInsetDip - overlayWidthDip);
        var maxTop = Math.Max(minTop, targetBottom - edgeInsetDip - overlayHeightDip);
        var left = Math.Clamp(targetLeft + offsetXDip, minLeft, maxLeft);
        var top = Math.Clamp(targetTop + offsetYDip, minTop, maxTop);
        return new OverlayPlacement(left, top, scale);
    }

    public static OverlayPlacement ClampToTarget(
        CodexWindowTarget target,
        double overlayWidthDip,
        double overlayHeightDip,
        double desiredLeftDip,
        double desiredTopDip,
        double edgeInsetDip = 4)
    {
        var scale = Math.Max(1.0, target.Dpi / 96.0);
        var targetLeft = target.Rect.Left / scale;
        var targetTop = target.Rect.Top / scale;
        return CalculateRelative(
            target,
            overlayWidthDip,
            overlayHeightDip,
            desiredLeftDip - targetLeft,
            desiredTopDip - targetTop,
            edgeInsetDip);
    }

    public static OverlayPlacement CalculatePopover(
        CodexWindowTarget target,
        double pillLeftDip,
        double pillTopDip,
        double pillWidthDip,
        double pillHeightDip,
        double popoverWidthDip,
        double popoverHeightDip,
        NativeMonitorWorkArea workArea,
        double gapDip = 8,
        double edgeInsetDip = 8)
    {
        if (pillWidthDip <= 0 || pillHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(pillWidthDip));
        if (popoverWidthDip <= 0 || popoverHeightDip <= 0) throw new ArgumentOutOfRangeException(nameof(popoverWidthDip));

        var scale = Math.Max(1.0, target.Dpi / 96.0);
        var workLeft = workArea.Left / scale;
        var workTop = workArea.Top / scale;
        var workRight = workArea.Right / scale;
        var workBottom = workArea.Bottom / scale;

        var minLeft = workLeft + edgeInsetDip;
        var maxLeft = Math.Max(minLeft, workRight - edgeInsetDip - popoverWidthDip);
        var desiredLeft = pillLeftDip + pillWidthDip - popoverWidthDip;
        var left = Math.Clamp(desiredLeft, minLeft, maxLeft);

        var belowTop = pillTopDip + pillHeightDip + gapDip;
        var aboveTop = pillTopDip - gapDip - popoverHeightDip;
        var maxTop = Math.Max(workTop + edgeInsetDip, workBottom - edgeInsetDip - popoverHeightDip);
        var fitsBelow = belowTop + popoverHeightDip <= workBottom - edgeInsetDip;
        var top = fitsBelow ? belowTop : aboveTop;
        top = Math.Clamp(top, workTop + edgeInsetDip, maxTop);

        return new OverlayPlacement(left, top, scale);
    }
}

public static class MonitorWorkAreaProvider
{
    public static NativeMonitorWorkArea GetForWindow(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return new NativeMonitorWorkArea(0, 0, 1920, 1080);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return new NativeMonitorWorkArea(0, 0, 1920, 1080);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return new NativeMonitorWorkArea(0, 0, 1920, 1080);

        return new NativeMonitorWorkArea(
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom);
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor;
        public MonitorRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
