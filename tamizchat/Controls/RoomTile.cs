using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Core.Media;
using TamizChat.Core.Protocol;

namespace TamizChat.Controls;

/// <summary>
/// One room in the grid: its name and headcount over the people inside it.
///
/// The people are laid out on the same subdivision the rooms are — one fills the
/// space, two halve it, four make quarters, and past that it scrolls — so a cell
/// is always a frame a camera feed can fill rather than a slot sized to an
/// avatar.
/// </summary>
public sealed class RoomTile : Grid
{
    private readonly MemberGrid _members;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly TextBlock _empty;

    private Room _room;

    /// <summary>Passes the talking ring down to the people in this room.</summary>
    public void SetSpeaking(IReadOnlyList<string> identities) => _members.SetSpeaking(identities);

    /// <summary>Passes a camera or screen frame to whoever sent it.</summary>
    public void SetVideoFrame(RemoteVideoFrame frame) => _members.SetVideoFrame(frame);

    public void ClearVideo(string identity, VideoKind kind) => _members.ClearVideo(identity, kind);

    public RoomTile(Room room, bool isMine)
    {
        _room = room;

        Padding = new Thickness(14);
        Background = (Brush)Application.Current.Resources["TcSurfaceBrush"];
        CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"];

        _title = new TextBlock
        {
            Text = room.Name,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        };

        _count = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        var header = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_title, 0);
        SetColumn(_count, 1);
        header.Children.Add(_title);
        header.Children.Add(_count);

        _members = new MemberGrid();

        _scroller = new ScrollViewer
        {
            Content = _members,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,

            // Once this reaches its end the wheel carries on to the room grid
            // behind it, so a nested scroller does not trap the pointer.
            IsVerticalScrollChainingEnabled = true,
        };
        _scroller.SizeChanged += (_, _) =>
            _members.SetViewport(_scroller.ViewportWidth, _scroller.ViewportHeight);

        _empty = new TextBlock
        {
            Text = "Empty",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        var body = new Grid();
        body.Children.Add(_empty);
        body.Children.Add(_scroller);

        // Grid rather than Border as the base: Border is sealed in WinUI, and
        // Grid carries the same border, corner and padding properties.
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetRow(header, 0);
        SetRow(body, 1);
        Children.Add(header);
        Children.Add(body);

        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Activated?.Invoke(this, _room.Id);
        };

        Update(room, isMine);
    }

    /// <summary>Raised when the user double-clicks the tile to move into the room.</summary>
    public event EventHandler<string>? Activated;

    public string RoomId => _room.Id;

    public void Update(Room room, bool isMine)
    {
        _room = room;
        _title.Text = room.Name;
        _count.Text = room.Capacity > 0
            ? $"{room.MemberCount}/{room.Capacity}"
            : room.MemberCount.ToString();

        BorderThickness = new Thickness(isMine ? 2 : 1);
        BorderBrush = (Brush)Application.Current.Resources[isMine ? "TcAccentBrush" : "TcBorderBrush"];

        var hasMembers = room.Members.Count > 0;
        _empty.Visibility = hasMembers ? Visibility.Collapsed : Visibility.Visible;
        _scroller.Visibility = hasMembers ? Visibility.Visible : Visibility.Collapsed;

        _members.SetMembers(room.Members);
        _members.SetViewport(_scroller.ViewportWidth, _scroller.ViewportHeight);
    }
}
