using Microsoft.UI;
using Windows.UI;

namespace TamizChat.Controls;

/// <summary>Small colour helpers shared by the tag renderer and its designer.</summary>
public static class Colours
{
    /// <summary>
    /// Colours worth offering anywhere the user picks one — role tags and bots.
    ///
    /// Chosen to be distinguishable from each other at tag size, which rules out
    /// most of a naive rainbow. `Second` is the partner shade a gradient fades
    /// into, so a picker can offer one choice rather than two.
    /// </summary>
    public static readonly (string Name, string Hex, string Second)[] Palette =
    [
        ("Crimson", "#e74c3c", "#c0392b"),
        ("Amber", "#f39c12", "#e67e22"),
        ("Gold", "#f1c40f", "#d4a20a"),
        ("Lime", "#7ed321", "#4e9a06"),
        ("Emerald", "#2ecc71", "#27ae60"),
        ("Teal", "#1abc9c", "#16a085"),
        ("Sky", "#3498db", "#2980b9"),
        ("Indigo", "#5b6eae", "#3f4d7a"),
        ("Violet", "#9b59b6", "#8e44ad"),
        ("Magenta", "#e91e8c", "#b3126b"),
        ("Rose", "#ff7a9c", "#e05575"),
        ("Slate", "#607d8b", "#455a64"),
        ("Graphite", "#4a4a4a", "#2b2b2b"),
        ("Bone", "#e8e2d5", "#c9c1b0"),
    ];

    public static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var text = hex.TrimStart('#');

        if (text.Length != 6
            || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return fallback;
        }

        return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static string ToHex(Color colour) => $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";

    /// <summary>
    /// Black or white, whichever stands out.
    ///
    /// Weighted for perceived brightness: green looks far lighter than blue at
    /// the same numeric value, and averaging the channels gets that wrong.
    /// </summary>
    public static Color Readable(Color background)
    {
        var brightness = ((background.R * 0.299) + (background.G * 0.587) + (background.B * 0.114)) / 255;
        return brightness > 0.6 ? Colors.Black : Colors.White;
    }

    public static Color WithAlpha(Color colour, byte alpha) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    public static Color Lighten(Color colour, double amount) => Color.FromArgb(
        colour.A,
        (byte)Math.Clamp(colour.R + (255 - colour.R) * amount, 0, 255),
        (byte)Math.Clamp(colour.G + (255 - colour.G) * amount, 0, 255),
        (byte)Math.Clamp(colour.B + (255 - colour.B) * amount, 0, 255));

    public static Color Darken(Color colour, double amount) => Color.FromArgb(
        colour.A,
        (byte)Math.Clamp(colour.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(colour.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(colour.B * (1 - amount), 0, 255));

    /// <summary>A fully saturated colour at the given angle on the hue circle.</summary>
    public static Color FromHue(double degrees)
    {
        var h = (degrees % 360 + 360) % 360 / 60;
        var x = 1 - Math.Abs((h % 2) - 1);

        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };

        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
