using TamizChat.Localization;
using System.Runtime.InteropServices;
using System.Text;

namespace TamizChat.Video;

/// <summary>What the user picked to share: a whole monitor, or one window.</summary>
public sealed class ShareTarget
{
    public required string Title { get; init; }

    /// <summary>Zero for a monitor; the window handle otherwise.</summary>
    public nint Window { get; init; }

    public bool IsWindow => Window != nint.Zero;

    /// <summary>Screen coordinates of the monitor. Meaningless for a window.</summary>
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Subtitle => IsWindow ? Loc.Get("Share.Window") : Loc.Get("Share.ScreenSubtitle", Width, Height);
}

/// <summary>
/// Enumerates what can be shared.
///
/// This is our own list rather than the system's <c>GraphicsCapturePicker</c>:
/// that one hands back a Direct3D surface, which is a different capture stack
/// from the GDI path everything here uses. A picker of our own also follows the
/// app's theme instead of appearing as a stock Windows dialog.
/// </summary>
public static class ShareTargets
{
    public static IReadOnlyList<ShareTarget> List()
    {
        var targets = new List<ShareTarget>();
        var index = 1;

        EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (nint monitor, nint _, ref Rect rect, nint _) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                var primary = GetMonitorInfo(monitor, ref info) && (info.Flags & 1) != 0;

                targets.Add(new ShareTarget
                {
                    Title = primary ? Loc.Get("Share.MainScreen") : Loc.Get("Share.ScreenN", index),
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Right - rect.Left,
                    Height = rect.Bottom - rect.Top,
                });

                index++;
                return true;
            },
            nint.Zero);

        // The primary screen first: it is what nearly everyone means.
        // Compared on the flag rather than the label, which is translated.
        targets = [.. targets.OrderByDescending(t => !t.IsWindow && t.Width > 0)];

        var self = GetCurrentProcessId();


        EnumWindows(
            (window, _) =>
            {
                if (!IsWindowVisible(window) || IsIconic(window))
                {
                    return true;
                }

                // Sharing TamizChat's own window inside TamizChat is a hall of
                // mirrors and never what anyone wants.
                GetWindowThreadProcessId(window, out var owner);
                if (owner == self)
                {
                    return true;
                }

                // Windows keeps a lot of invisible top-level windows around; a
                // cloaked one is the usual sign of a UWP shell window that is
                // not really on screen.
                if (DwmGetWindowAttribute(window, DwmCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                // A tool window is a palette or a tray helper, not a document.
                if ((GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0)
                {
                    return true;
                }

                var length = GetWindowTextLength(window);
                if (length == 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);

                targets.Add(new ShareTarget { Title = title.ToString(), Window = window });
                return true;
            },
            nint.Zero);

        return targets;
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

    private delegate bool WindowEnumProc(nint window, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
