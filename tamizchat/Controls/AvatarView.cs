using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TamizChat.Core.Protocol;
using TamizChat.Localization;
using Windows.UI;

namespace TamizChat.Controls;

/// <summary>
/// A user's stand-in: their profile picture, or a circle with the first letter
/// of their name when they have not uploaded one.
///
/// The letter is not a placeholder to be ashamed of — its colour is derived from
/// the name, so the same person is the same colour on every client and every
/// run, and a room of people with no pictures is still readable at a glance.
/// A picture simply replaces the fill when there is one.
/// </summary>
public sealed class AvatarView : Grid
{
    private readonly Border _halo;
    private readonly Border _circle;
    private readonly TextBlock _letter;
    private readonly Border _badge;
    private readonly TextBlock _badgeGlyph;
    private readonly Ellipse _picture;
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

        // The corner badge: one glyph, bottom right, for a closed microphone or
        // switched-off speakers. Deliberately a single badge rather than a row —
        // at the size an avatar is drawn in a full room there is space for one
        // thing, and "cannot hear you at all" is the more important of the two.
        _badgeGlyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };

        _badge = new Border
        {
            Background = (Brush)Application.Current.Resources["TcDangerBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _badgeGlyph,
            Visibility = Visibility.Collapsed,
        };

        // The picture sits over the coloured circle rather than replacing it, so
        // the letter is still there underneath while the bytes are on their way
        // and there is never a blank hole in the room.
        //
        // An Ellipse with an ImageBrush, not an Image with a clip: a brush
        // fills the shape and the shape is already a circle, which is one
        // element instead of a clipped image inside a border.
        _picture = new Ellipse { Visibility = Visibility.Collapsed };

        Children.Add(_halo);
        Children.Add(_circle);
        Children.Add(_picture);
        Children.Add(_badge);

        ScaleBadge(size);
        ToolTipService.SetToolTip(this, username);
    }

    /// <summary>
    /// Points this avatar at a person, and keeps it pointed there.
    ///
    /// The picture is almost never ready the first time it is asked for — the
    /// first call starts the download and returns nothing — so a caller that
    /// only asks once draws the letter for ever. The room grid got away with it
    /// because it redraws constantly; the members overlay and a chat row do not,
    /// and that is exactly where the picture never appeared.
    ///
    /// So the control watches for its own person's picture arriving rather than
    /// leaving every call site to remember. Subscribed only while the control is
    /// in the tree: an avatar in a room that emptied would otherwise be held
    /// alive by the event for as long as the app runs.
    /// </summary>
    public void SetUser(User member)
    {
        _clientUuid = member.ClientUuid;
        SetPicture(Services.Avatars.Get(member));

        if (_watching)
        {
            return;
        }

        _watching = true;

        Loaded += (_, _) => Services.Avatars.Loaded += OnAvatarLoaded;
        Unloaded += (_, _) => Services.Avatars.Loaded -= OnAvatarLoaded;

        // Already in the tree when this is first called — a cell built during a
        // redraw rather than at page load — so Loaded has been and gone.
        if (IsLoaded)
        {
            Services.Avatars.Loaded += OnAvatarLoaded;
        }
    }

    private string _clientUuid = "";
    private bool _watching;

    private void OnAvatarLoaded(object? sender, string clientUuid)
    {
        if (clientUuid == _clientUuid && Services.Avatars.TryGet(clientUuid) is { } image)
        {
            SetPicture(image);
        }
    }

    /// <summary>
    /// Shows a picture directly, or goes back to the letter when there is none.
    /// </summary>
    public void SetPicture(BitmapImage? image)
    {
        if (image is null)
        {
            _picture.Fill = null;
            _picture.Visibility = Visibility.Collapsed;
            return;
        }

        _picture.Fill = new ImageBrush
        {
            ImageSource = image,

            // The stored picture is square, but Fill would still distort
            // anything that is not — and UniformToFill crops instead, which is
            // what a circular avatar wants.
            Stretch = Stretch.UniformToFill,
        };

        _picture.Visibility = Visibility.Visible;
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

    public void SetMuted(bool muted)
    {
        _circle.Opacity = muted ? 0.45 : 1.0;
        _picture.Opacity = muted ? 0.45 : 1.0;
    }

    /// <summary>
    /// Marks what this person has switched off.
    ///
    /// Deafened wins when both are true, and not only because there is room for
    /// one glyph: somebody with their speakers off cannot hear you whether or not
    /// their microphone is open, which is the thing worth knowing before you
    /// start talking to them.
    /// </summary>
    public void SetSelfMuted(bool micOff, bool deafened, string username)
    {
        if (!micOff && !deafened)
        {
            _badge.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(this, username);
            return;
        }

        // Segoe MDL2: a crossed-out speaker, and a crossed-out microphone.
        // E198 was the first choice for the microphone and turned out to be
        // another speaker — the two badges were indistinguishable — so both are
        // now checked by eye rather than by name.
        _badgeGlyph.Text = deafened ? "" : "";
        _badge.Visibility = Visibility.Visible;

        ToolTipService.SetToolTip(this, deafened
            ? Loc.Get("Voice.TheyAreDeafened", username)
            : Loc.Get("Voice.TheirMicIsOff", username));
    }

    /// <summary>
    /// Rescales the whole avatar. Cells change size as people join and leave, and
    /// rebuilding the control for each new size would throw away its state.
    /// </summary>
    public void Resize(double size)
    {
        if (Math.Abs(Width - size) < 0.5)
        {
            return;
        }

        Width = size;
        Height = size;
        _circle.CornerRadius = new CornerRadius(size / 2);
        _halo.CornerRadius = new CornerRadius(size);
        _letter.FontSize = size * 0.4;
        ScaleBadge(size);
    }

    /// <summary>
    /// Keeps the badge proportional to the avatar, with a floor: below about
    /// eleven pixels the glyph is a smudge, and a smudge that means "muted" is
    /// worse than none. It sits mostly outside the circle so it covers as little
    /// of the letter as possible — on the 24-pixel avatars in the members
    /// overlay there is not much letter to spare.
    /// </summary>
    private void ScaleBadge(double size)
    {
        var diameter = Math.Max(11, size * 0.34);

        _badge.Width = diameter;
        _badge.Height = diameter;
        _badge.CornerRadius = new CornerRadius(diameter / 2);
        _badge.Margin = new Thickness(0, 0, -diameter * 0.3, -diameter * 0.3);
        _badgeGlyph.FontSize = diameter * 0.55;
    }

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
