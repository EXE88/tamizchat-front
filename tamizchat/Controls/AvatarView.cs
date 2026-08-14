using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TamizChat.Controls;

/// <summary>
/// A user's stand-in: a circle with the first letter of their name.
///
/// There are no profile pictures in TamizChat, so the circle is the identity.
/// Its colour is derived from the name, which makes people distinguishable at a
/// glance without anyone choosing anything.
/// </summary>
public sealed class AvatarView : Grid
{
    private readonly Border _halo;
    private readonly Border _circle;
    private readonly TextBlock _letter;
    private bool _isSpeaking;

    public AvatarView(string username, double size = 44)
    {
        Width = size;
        Height = size;

        // Sits behind and slightly larger, so a speaking ring can appear without
        // the avatar itself changing size and nudging the layout.
        _halo = new Border
        {
            CornerRadius = new CornerRadius(size),
            BorderThickness = new Thickness(2.5),
            BorderBrush = (Brush)Application.Current.Resources["TcAccentBrush"],
            Margin = new Thickness(-4),
            Opacity = 0,
        };

        _letter = new TextBlock
        {
            Text = Initial(username),
            FontSize = size * 0.4,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _circle = new Border
        {
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(ColorFor(username)),
            Child = _letter,
        };

        Children.Add(_halo);
        Children.Add(_circle);

        ToolTipService.SetToolTip(this, username);
    }

    /// <summary>Draws the ring that marks whoever is talking.</summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value)
            {
                return;
            }

            _isSpeaking = value;
            _halo.Opacity = value ? 1 : 0;
        }
    }

    public void SetMuted(bool muted) => _circle.Opacity = muted ? 0.45 : 1.0;

    private static string Initial(string username) =>
        string.IsNullOrWhiteSpace(username) ? "?" : username.Trim()[..1].ToUpperInvariant();

    /// <summary>
    /// A stable hue per name. Same person, same colour, on every client and every
    /// run — so it can be recognised rather than merely decorative.
    /// </summary>
    private static Color ColorFor(string username)
    {
        var hash = 0;
        foreach (var c in username.Trim().ToLowerInvariant())
        {
            hash = (hash * 31) + c;
        }

        var hue = Math.Abs(hash) % 360;
        return FromHsl(hue, 0.52, 0.48);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = lightness - (c / 2);

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(
            255,
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}
