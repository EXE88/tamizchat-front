using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TamizChat.Services;

namespace TamizChat.Overlays;

/// <summary>Which corner of the screen an overlay sits in.</summary>
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// The shared behaviour of a floating overlay: on top of everything, in a
/// corner, see-through, and impossible to click.
///
/// Click-through is the part that makes an overlay usable rather than a
/// nuisance. Without it, a panel sitting over somebody's game or document eats
/// every click that lands on it — which is exactly where they were trying to
/// click. `WS_EX_TRANSPARENT` passes the mouse straight through to whatever is
/// underneath, so the overlay can only ever be looked at.
///
/// Opacity is applied to the whole window with `SetLayeredWindowAttributes`
/// rather than to XAML elements. A WinUI window paints its own background, so
/// fading the content alone still leaves an opaque rectangle behind it.
/// </summary>
public abstract class OverlayWindow : Window
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;

    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x00000002;

    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaBorderColour = 34;

    /// <summary>DWM's "no border at all", as opposed to a colour.</summary>
    private const uint ColorNone = 0xFFFFFFFE;

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    private readonly nint _handle;

    protected OverlayWindow()
    {
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle);
        var appWindow = AppWindow.GetFromWindowId(id);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // TOOLWINDOW keeps it out of Alt-Tab and the taskbar; NOACTIVATE stops
        // it stealing focus from whatever the user is actually working in.
        var style = GetWindowLong(_handle, GwlExStyle);
        SetWindowLong(
            _handle,
            GwlExStyle,
            style | WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate);

        // DevWinUI's transparent backdrop, so the window has no surface of its
        // own and only the content is visible.
        //
        // Two other routes were tried and neither works here. A colour key via
        // `SetLayeredWindowAttributes` never sees the pixels — WinUI composes
        // through DirectComposition rather than painting into the window's
        // surface, so the key colour stayed plainly visible. Plain acrylic does
        // render, but it is a *surface*: it tints the whole rectangle dark, which
        // is the black panel this was meant to remove.
        SystemBackdrop = new DevWinUI.TransparentBackdrop();

        // Windows 11 rounds every window and draws a thin border around it. On a
        // normal window that is what you want; on a transparent overlay it is a
        // ghost frame around nothing, and its corner radius does not match the
        // cards inside — which reads as a misaligned edge even though the window
        // itself is invisible.
        //
        // Both are DWM attributes, so this is plain Win32 rather than anything
        // DevWinUI provides. DWMWA_COLOR_NONE removes the border outright.
        var doNotRound = DwmWindowCornerPreference.DoNotRound;
        DwmSetWindowAttribute(_handle, DwmaWindowCornerPreference, ref doNotRound, sizeof(int));

        var noBorder = ColorNone;
        DwmSetWindowAttribute(_handle, DwmaBorderColour, ref noBorder, sizeof(uint));

        // The DWM attributes above are not enough on their own: a thin light
        // outline survived them, because it comes from the window's own frame
        // styles rather than from DWM. WS_POPUP is a window with no frame at
        // all, which is what an overlay actually is. The style only takes effect
        // once the frame is recalculated, hence SWP_FRAMECHANGED.
        SetWindowLong(_handle, GwlStyle, WsPopup | WsVisible);
        SetWindowPos(
            _handle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);

        AppWindow = appWindow;
    }

    protected AppWindow AppWindow { get; }

    /// <summary>0 to 1. Applied to the whole window, backdrop included.</summary>
    public void SetOpacity(double opacity)
    {
        var alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        SetLayeredWindowAttributes(_handle, 0, alpha, LwaAlpha);
    }

    /// <summary>
    /// Puts the window in a corner of the work area.
    ///
    /// The *work* area, not the whole screen, so a bottom corner does not end up
    /// underneath the taskbar. Sizes are physical pixels here, like everywhere
    /// else in AppWindow, so the caller passes layout units and this scales them.
    /// </summary>
    public void PlaceInCorner(OverlayCorner corner, int width, int height, int margin = 16)
    {
        var scale = GetDpiForWindow(_handle) / 96.0;
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        var m = (int)(margin * scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        var x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.X + m
            : area.X + area.Width - w - m;

        var y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Y + m
            : area.Y + area.Height - h - m;

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>Shows or hides without destroying, so state survives being turned off and on.</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            AppWindow.Show(activateWindow: false);
        }
        else
        {
            AppWindow.Hide();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colour, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref DwmWindowCornerPreference value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
}
