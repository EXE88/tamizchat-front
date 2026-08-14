using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TamizChat.Core.Protocol;
using TamizChat.Services;

namespace TamizChat.Overlays;

/// <summary>
/// Chat messages as fading cards over the screen, so a conversation can be
/// followed without the app's window in front of you.
///
/// Newest at the top. Each card fades on its own timer, but when more arrive
/// than the limit allows, the **oldest is dropped immediately** regardless of
/// how much of its time was left — that was the explicit ask, and it is the
/// right rule: a fixed number of lines that always shows the most recent beats
/// a list that grows past the corner of the screen.
/// </summary>
public sealed class MessagesOverlay : OverlayWindow
{
    private const int Width = 320;

    private readonly StackPanel _stack;
    private readonly List<Card> _cards = [];

    public MessagesOverlay()
    {
        _stack = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(4),

            // Transparent: the cards carry their own background and the rest
            // is the window's acrylic, not an opaque black rectangle.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        Content = _stack;

        ServerSession.Instance.MessageReceived += (_, message) => Show(message);
    }

    public void Apply()
    {
        var settings = SettingsStore.Current;

        SetOpacity(settings.MessagesOverlayOpacity);
        PlaceInCorner(settings.MessagesOverlayCorner, Width, Height());
        SetVisible(settings.MessagesOverlayEnabled && _cards.Count > 0);
    }

    private int Height() => Math.Max(1, _cards.Count) * 56;

    private void Show(ChatMessage message)
    {
        var settings = SettingsStore.Current;
        if (!settings.MessagesOverlayEnabled)
        {
            return;
        }

        // Your own message is already on your screen — you just typed it.
        if (message.Author.ClientUuid == ServerSession.Instance.MyUuid)
        {
            return;
        }

        var card = new Card(message, () => Remove(null));
        _cards.Insert(0, card);
        _stack.Children.Insert(0, card.Root);

        // Over the limit: the oldest goes now, whatever its own timer said.
        while (_cards.Count > Math.Max(1, settings.MessagesOverlayMax))
        {
            Remove(_cards[^1]);
        }

        card.StartFade(TimeSpan.FromSeconds(Math.Max(1, settings.MessagesOverlayFadeSeconds)), () => Remove(card));

        Apply();
    }

    private void Remove(Card? card)
    {
        card ??= _cards.LastOrDefault();
        if (card is null || !_cards.Remove(card))
        {
            return;
        }

        card.Stop();
        _stack.Children.Remove(card.Root);
        Apply();
    }

    /// <summary>One message: who said it, and what they said.</summary>
    private sealed class Card
    {
        private readonly DispatcherTimer _timer = new();
        private Action? _onDone;

        public Card(ChatMessage message, Action onExpired)
        {
            _onDone = onExpired;

            var author = new TextBlock
            {
                Text = message.Author.Username,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TcAccentBrush"],
            };

            var text = new TextBlock
            {
                Text = message.Text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            };

            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(author);
            panel.Children.Add(text);

            // Grid rather than Border: Border is sealed in WinUI, and Grid has
            // the same padding, corner and background properties.
            Root = new Grid
            {
                Padding = new Thickness(12, 8, 12, 8),
                Background = (Brush)Application.Current.Resources["TcBoardBrush"],
                CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"],
            };
            Root.Children.Add(panel);
        }

        public Grid Root { get; }

        public void StartFade(TimeSpan after, Action onExpired)
        {
            _onDone = onExpired;
            _timer.Interval = after;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
        }

        private void OnTick(object? sender, object e)
        {
            Stop();

            // Fade the card out before it is taken away, so lines do not vanish
            // mid-sentence — reading one that disappears is worse than not
            // seeing it at all.
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(350),
                EnableDependentAnimation = true,
            };

            var story = new Storyboard();
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            story.Children.Add(fade);
            story.Completed += (_, _) => _onDone?.Invoke();
            story.Begin();
        }
    }
}
