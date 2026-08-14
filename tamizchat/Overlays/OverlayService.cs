namespace TamizChat.Overlays;

/// <summary>
/// Owns the overlay windows.
///
/// They are created **lazily**, the first time one is switched on, and then kept
/// for the rest of the session rather than destroyed when switched off. Creating
/// a window is slow enough to be visible, and someone toggling an overlay is
/// usually about to toggle it back.
/// </summary>
public sealed class OverlayService
{
    private MembersOverlay? _members;
    private MessagesOverlay? _messages;

    public static OverlayService Instance { get; } = new();

    /// <summary>Re-reads the settings and brings both overlays into line.</summary>
    public void Apply()
    {
        var settings = Services.SettingsStore.Current;

        if (settings.MembersOverlayEnabled)
        {
            _members ??= new MembersOverlay();
        }

        if (settings.MessagesOverlayEnabled)
        {
            _messages ??= new MessagesOverlay();
        }

        _members?.Apply();
        _messages?.Apply();
    }

    /// <summary>
    /// Closes them for good, on the way out.
    ///
    /// An overlay is a real top-level window: left open, it keeps the process
    /// alive after the main window is gone, and the app appears to hang around
    /// invisibly.
    /// </summary>
    public void Close()
    {
        _members?.Close();
        _messages?.Close();
        _members = null;
        _messages = null;
    }
}
