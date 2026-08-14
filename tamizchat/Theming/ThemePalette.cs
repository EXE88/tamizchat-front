using Windows.UI;

namespace TamizChat.Theming;

/// <summary>
/// One resolved colour scheme: a theme family in either its light or its dark
/// variant. Every brush the app draws with comes from one of these roles, so a
/// new theme is a new entry in <see cref="Table"/> and nothing else.
/// </summary>
/// <param name="Tint">
/// Painted over the window backdrop. Deliberately semi-transparent: a solid
/// colour here would hide the Mica or Acrylic material entirely.
/// </param>
public sealed record ThemePalette(
    Color Tint,
    Color Surface,
    Color SurfaceHover,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color Accent,
    Color AccentHover,
    Color OnAccent)
{
    public static ThemePalette Get(ThemeFamily family, bool isDark) => Table[(family, isDark)];

    /// <summary>Is this family's variant a dark one? Used to pick the WinUI element theme.</summary>
    private static readonly Dictionary<(ThemeFamily, bool), ThemePalette> Table = new()
    {
        // --- Salt and Pepper: #FFFFFF, #D4D4D4, #B3B3B3, #2B2B2B ---
        // The one family with four given colours, so nothing has to be derived
        // beyond the alpha values.
        [(ThemeFamily.SaltAndPepper, false)] = new ThemePalette(
            Tint: Hex("#59FFFFFF"),
            Surface: Hex("#B0FFFFFF"),
            SurfaceHover: Hex("#B0D4D4D4"),
            Border: Hex("#80B3B3B3"),
            TextPrimary: Hex("#2B2B2B"),
            TextSecondary: Hex("#992B2B2B"),
            Accent: Hex("#2B2B2B"),
            AccentHover: Hex("#404040"),
            OnAccent: Hex("#FFFFFF")),

        [(ThemeFamily.SaltAndPepper, true)] = new ThemePalette(
            Tint: Hex("#592B2B2B"),
            Surface: Hex("#B02B2B2B"),
            SurfaceHover: Hex("#B0404040"),
            Border: Hex("#59B3B3B3"),
            TextPrimary: Hex("#FFFFFF"),
            TextSecondary: Hex("#B3B3B3"),
            Accent: Hex("#FFFFFF"),
            AccentHover: Hex("#D4D4D4"),
            OnAccent: Hex("#2B2B2B")),

        // --- Violet and Lavender: Lavender #D2C3F6, Violet #36205C ---
        // The mid tones are derived by blending the two given colours.
        [(ThemeFamily.VioletAndLavender, false)] = new ThemePalette(
            Tint: Hex("#59D2C3F6"),
            Surface: Hex("#B0E4DCFA"),
            SurfaceHover: Hex("#B0C0AEEF"),
            Border: Hex("#4036205C"),
            TextPrimary: Hex("#36205C"),
            TextSecondary: Hex("#9936205C"),
            Accent: Hex("#36205C"),
            AccentHover: Hex("#4A2D7D"),
            OnAccent: Hex("#D2C3F6")),

        [(ThemeFamily.VioletAndLavender, true)] = new ThemePalette(
            Tint: Hex("#5936205C"),
            Surface: Hex("#B0462D73"),
            SurfaceHover: Hex("#B0573A8A"),
            Border: Hex("#40D2C3F6"),
            TextPrimary: Hex("#D2C3F6"),
            TextSecondary: Hex("#99D2C3F6"),
            Accent: Hex("#D2C3F6"),
            AccentHover: Hex("#E4DCFA"),
            OnAccent: Hex("#36205C")),

        // --- Carbon and Lime: Carbon #171717, Lime #C6FF34 ---
        // Lime is an accent, never body text: at text sizes it is painful to
        // read on either background. The greys are derived from Carbon.
        [(ThemeFamily.CarbonAndLime, false)] = new ThemePalette(
            Tint: Hex("#59F7F7F7"),
            Surface: Hex("#B0FFFFFF"),
            SurfaceHover: Hex("#B0EDEDED"),
            Border: Hex("#40171717"),
            TextPrimary: Hex("#171717"),
            TextSecondary: Hex("#99171717"),
            Accent: Hex("#C6FF34"),
            AccentHover: Hex("#B4EA22"),
            OnAccent: Hex("#171717")),

        [(ThemeFamily.CarbonAndLime, true)] = new ThemePalette(
            Tint: Hex("#59171717"),
            Surface: Hex("#B0202020"),
            SurfaceHover: Hex("#B02E2E2E"),
            Border: Hex("#40C6FF34"),
            TextPrimary: Hex("#F2F2F2"),
            TextSecondary: Hex("#9AF2F2F2"),
            Accent: Hex("#C6FF34"),
            AccentHover: Hex("#D8FF6B"),
            OnAccent: Hex("#171717")),
    };

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
    private static Color Hex(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return hex.Length == 7
            ? Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
