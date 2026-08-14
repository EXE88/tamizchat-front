using TamizChat.Pages;

namespace TamizChat.Navigation;

/// <summary>
/// The two item sets the bottom bar switches between.
///
/// The order here is also the left-to-right order on screen, which is what the
/// slide transition uses to decide which side a page arrives from.
///
/// Glyphs are written as escapes rather than pasted characters: they live in
/// Unicode's private use area, and pasted ones get silently stripped by tools
/// along the way, leaving every icon blank.
/// </summary>
public static class ShellItems
{
    // Segoe Fluent Icons.
    private const string GlyphCloud = "";
    private const string GlyphHome = "";
    private const string GlyphSettings = "";
    private const string GlyphGrid = "";
    private const string GlyphMessage = "";
    private const string GlyphMicrophone = "";
    private const string GlyphVolume = "";
    private const string GlyphMute = "";
    private const string GlyphVideo = "";
    private const string GlyphProject = "";
    private const string GlyphMusic = "";
    private const string GlyphBolt = "";
    private const string GlyphEdit = "";
    private const string GlyphCancel = "";

    /// <summary>Home sits in the middle, with the list on one side and settings on the other.</summary>
    public static readonly IReadOnlyList<NavBarItem> PreServer =
    [
        new() { Key = "servers", Glyph = GlyphCloud, Label = "Servers", Page = typeof(ServersPage) },
        new() { Key = "home", Glyph = GlyphHome, Label = "Home", Page = typeof(HomePage) },
        new() { Key = "settings", Glyph = GlyphSettings, Label = "Settings", Page = typeof(SettingsPage) },
    ];

    public static readonly IReadOnlyList<NavBarItem> InServer =
    [
        new() { Key = "room", Glyph = GlyphGrid, Label = "Room", Page = typeof(ServerPage) },
        new() { Key = "chat", Glyph = GlyphMessage, Label = "Chat", Page = typeof(ChatPage) },

        // Live by default: being heard is the normal state, so only the muted
        // state is called out.
        new()
        {
            Key = "mic",
            Glyph = GlyphMicrophone,
            Label = "Mic",
            OffLabel = "Muted",
            Kind = NavItemKind.Toggle,
            StartsOn = true,
            WarnWhenOff = true,
        },
        new()
        {
            Key = "speaker",
            Glyph = GlyphVolume,
            OffGlyph = GlyphMute,
            Label = "Speaker",
            OffLabel = "Deafened",
            Kind = NavItemKind.Toggle,
            StartsOn = true,
            WarnWhenOff = true,
        },

        // Off by default: broadcasting is the notable state, so it gets the pill.
        new() { Key = "camera", Glyph = GlyphVideo, Label = "Camera", Kind = NavItemKind.Toggle },
        new() { Key = "screen", Glyph = GlyphProject, Label = "Screen", Kind = NavItemKind.Toggle },

        new()
        {
            Key = "effects",
            Glyph = GlyphMusic,
            Label = "Effects",
            Kind = NavItemKind.Menu,
            MenuOptions = ["Airhorn", "Applause", "Drum roll", "Rimshot", "Crickets"],
        },
        new()
        {
            Key = "voice",
            Glyph = GlyphBolt,
            Label = "Voice",
            Kind = NavItemKind.Menu,
            MenuOptions = ["None", "Deep", "Chipmunk", "Robot", "Radio"],
        },

        new() { Key = "paint", Glyph = GlyphEdit, Label = "Paint", Page = typeof(PaintPage) },
        new()
        {
            Key = "disconnect",
            Glyph = GlyphCancel,
            Label = "Disconnect",
            Kind = NavItemKind.Command,
            IsDangerous = true,
        },
    ];

    /// <summary>Pages that mean the shell is inside a server.</summary>
    public static bool IsInServer(Type? page) =>
        page == typeof(ServerPage) || page == typeof(ChatPage) || page == typeof(PaintPage);
}
