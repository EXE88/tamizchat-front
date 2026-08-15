using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TamizChat.Core.Protocol;
using TamizChat.Localization;

namespace TamizChat.Controls;

/// <summary>
/// A bot in a room, drawn beside the people.
///
/// It deliberately looks like a member cell rather than a control panel: to
/// anybody in the room a music bot is another participant making sound, and the
/// buttons for it belong on right-click, where a person's moderation menu is.
/// The disc carries the bot's own colour — the same one the admin panel shows —
/// so the two views are recognisably the same bot.
/// </summary>
internal sealed class BotCell : OccupantCell
{
    private readonly Ellipse _disc;
    private readonly TextBlock _glyph;
    private readonly TextBlock _name;
    private readonly TextBlock _track;
    private readonly StackPanel _stack;
    private readonly Grid _badge;

    private Bot _bot;

    public BotCell(Bot bot)
    {
        _bot = bot;

        Background = (Brush)Application.Current.Resources["TcSurfaceHoverBrush"];
        CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"];

        _disc = new Ellipse
        {
            Width = 48,
            Height = 48,
            Fill = new SolidColorBrush(Colours.Parse(bot.Color, Microsoft.UI.Colors.SlateGray)),
        };

        // A musical note rather than an initial: a bot is not a person, and the
        // avatar letter would read as one at a glance.
        _glyph = new TextBlock
        {
            Text = "♪",
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(
                Colours.Readable(Colours.Parse(bot.Color, Microsoft.UI.Colors.SlateGray))),
        };

        _badge = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _badge.Children.Add(_disc);
        _badge.Children.Add(_glyph);

        _name = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        // What is playing, under the name. It is the one thing anybody in the
        // room actually wants to know about a music bot.
        _track = new TextBlock
        {
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcAccentBrush"],
        };

        _stack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _stack.Children.Add(_badge);
        _stack.Children.Add(_name);
        _stack.Children.Add(_track);
        Children.Add(_stack);

        BotMenu.Attach(this, () => _bot);
        Update(bot);
    }

    public void Update(Bot bot)
    {
        _bot = bot;
        _name.Text = bot.Name;

        _disc.Fill = new SolidColorBrush(Colours.Parse(bot.Color, Microsoft.UI.Colors.SlateGray));
        _glyph.Foreground = new SolidColorBrush(
            Colours.Readable(Colours.Parse(bot.Color, Microsoft.UI.Colors.SlateGray)));

        var playing = bot.State == "playing" && bot.Track is not null;
        _track.Text = playing ? bot.Track!.Title : Loc.Get("Bot.Idle");
        _track.Opacity = playing ? 1 : 0.7;

        // A stopped bot is dimmed rather than hidden: it is still in the room,
        // and a tile that vanishes on stop makes the room look like it lost
        // something.
        _stack.Opacity = bot.Enabled ? 1 : 0.5;
    }

    /// <summary>
    /// The ring while the bot is publishing.
    ///
    /// It comes from LiveKit's active-speaker list like everyone else's, because
    /// the bot is a participant there — no separate "is the music playing"
    /// signal has to be invented.
    /// </summary>
    public override void SetSpeaking(bool speaking)
    {
        _disc.StrokeThickness = speaking ? 3 : 0;
        _disc.Stroke = speaking
            ? (Brush)Application.Current.Resources["TcAccentBrush"]
            : null;
    }

    public override void Resize(double width, double height)
    {
        var shortest = Math.Min(width, height);
        var size = Math.Clamp(shortest * 0.38, 22, 96);

        _disc.Width = size;
        _disc.Height = size;
        _glyph.FontSize = Math.Clamp(size * 0.45, 10, 40);

        _name.Visibility = shortest > 92 ? Visibility.Visible : Visibility.Collapsed;
        _name.FontSize = Math.Clamp(shortest * 0.10, 10, 14);
        _name.MaxWidth = Math.Max(24, width - 12);

        // The track title is the first thing to go when the tile is small: the
        // name matters more, and two clipped lines are worse than one.
        _track.Visibility = shortest > 130 ? Visibility.Visible : Visibility.Collapsed;
        _track.MaxWidth = Math.Max(24, width - 12);
    }
}
