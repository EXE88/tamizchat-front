using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Controls;
using TamizChat.Core.Protocol;
using TamizChat.Services;

namespace TamizChat.Overlays;

/// <summary>
/// Who is in your room and who is talking, visible while the app is minimised.
///
/// This is the one thing people keep a voice client's window open for, so it is
/// the one thing worth floating above everything else. It shows the room's name,
/// everyone in it, and rings whoever is speaking — the same halo the room grid
/// uses, from the same source.
/// </summary>
public sealed class MembersOverlay : OverlayWindow
{
    private const int Width = 240;

    private readonly StackPanel _root;
    private readonly StackPanel _members;
    private readonly Dictionary<string, AvatarRow> _rows = [];

    public MembersOverlay()
    {
        _members = new StackPanel { Spacing = 6 };

        _root = new StackPanel
        {
            Padding = new Thickness(8),

            // Transparent so the window's acrylic backdrop shows through and
            // the avatars appear to float over whatever is behind.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Spacing = 0,
        };
        _root.Children.Add(_members);

        Content = _root;

        ServerSession.Instance.Changed += (_, _) => Render();
        VoiceService.Instance.Changed += (_, _) => RenderSpeaking();

        Render();
    }

    /// <summary>Re-reads the settings and shows or hides accordingly.</summary>
    public void Apply()
    {
        var settings = SettingsStore.Current;

        SetOpacity(settings.MembersOverlayOpacity);
        PlaceInCorner(settings.MembersOverlayCorner, Width, EstimateHeight());
        SetVisible(settings.MembersOverlayEnabled && ServerSession.Instance.IsConnected);
    }

    /// <summary>
    /// The window is sized to its contents rather than fixed.
    ///
    /// A click-through window cannot be resized by the user, so if it were too
    /// small the names would simply be cut off with no way to fix it.
    /// </summary>
    private int EstimateHeight() => 16 + (Math.Max(1, _rows.Count) * 34);

    private void Render()
    {
        var session = ServerSession.Instance;
        var room = session.MyRoom;

        var members = room?.Members ?? [];

        foreach (var gone in _rows.Keys.Except(members.Select(m => m.ClientUuid)).ToList())
        {
            _members.Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }

        foreach (var member in members)
        {
            if (_rows.TryGetValue(member.ClientUuid, out var existing))
            {
                existing.Update(member);
                continue;
            }

            var row = new AvatarRow(member);
            _rows[member.ClientUuid] = row;
            _members.Children.Add(row.Root);
        }

        RenderSpeaking();
        Apply();
    }

    private void RenderSpeaking()
    {
        var speaking = VoiceService.Instance.Speakers;

        foreach (var (uuid, row) in _rows)
        {
            row.Avatar.IsSpeaking = speaking.Contains(uuid);
        }
    }

    /// <summary>One person: a small avatar and their name.</summary>
    private sealed class AvatarRow
    {
        public AvatarRow(User member)
        {
            Avatar = new AvatarView(member.Username, size: 24);

            Name = new TextBlock
            {
                Text = member.Username,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Always light: the pill behind it is always dark, whatever
                // theme the app itself is using.
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Avatar);
            row.Children.Add(Name);

            // With nothing behind the window, a name can land on any colour the
            // desktop happens to be. A translucent pill keeps it readable
            // without bringing back the solid panel.
            Root = new Grid
            {
                Padding = new Thickness(6, 3, 12, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)),
                CornerRadius = new CornerRadius(16),
            };
            Root.Children.Add(row);
        }

        public Grid Root { get; }

        public AvatarView Avatar { get; }

        private TextBlock Name { get; }

        public void Update(User member)
        {
            // The same badges as the room grid: the overlay is what somebody
            // watches while a game is in front of them, so it is the place a
            // closed microphone matters most.
            var (micOff, deafened) = MediaState.Of(member);
            Avatar.SetSelfMuted(micOff, deafened, member.Username);
            Name.Text = member.Username;
            Avatar.SetMuted(member.Muted);
        }
    }
}
