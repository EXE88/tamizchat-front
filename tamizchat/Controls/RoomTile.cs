using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TamizChat.Core.Protocol;

namespace TamizChat.Controls;

/// <summary>
/// One room in the grid: its name and headcount, over a showcase of the people
/// inside it.
///
/// The showcase fits as many avatars as the tile is wide and collapses the rest
/// into a "+N" circle, so a busy room stays readable at a quarter of the screen.
/// </summary>
public sealed class RoomTile : Grid
{
    private const double AvatarSize = 44;
    private const double AvatarGap = 8;

    private readonly StackPanel _avatars;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly TextBlock _empty;

    private Room _room;

    public RoomTile(Room room, bool isMine)
    {
        _room = room;

        Padding = new Thickness(16);
        Background = (Brush)Application.Current.Resources["TcSurfaceBrush"];
        BorderThickness = new Thickness(isMine ? 2 : 1);
        BorderBrush = (Brush)Application.Current.Resources[isMine ? "TcAccentBrush" : "TcBorderBrush"];
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

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_title);
        header.Children.Add(_count);

        _avatars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AvatarGap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _empty = new TextBlock
        {
            Text = "Empty",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(_empty);
        body.Children.Add(_avatars);

        // Grid rather than Border as the base: Border is sealed in WinUI, and
        // Grid carries the same border, corner and padding properties.
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetRow(header, 0);
        SetRow(body, 1);
        Children.Add(header);
        Children.Add(body);

        // The showcase depends on how wide the tile ended up, so it is rebuilt
        // whenever that changes rather than assumed.
        SizeChanged += (_, _) => BuildShowcase();
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

        BuildShowcase();
    }

    private void BuildShowcase()
    {
        _avatars.Children.Clear();

        var members = _room.Members;
        _empty.Visibility = members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (members.Count == 0)
        {
            return;
        }

        // How many fit across the tile, leaving room for the "+N" circle.
        var available = ActualWidth - Padding.Left - Padding.Right;
        var perAvatar = AvatarSize + AvatarGap;
        var fits = available > 0 ? Math.Max(1, (int)((available + AvatarGap) / perAvatar)) : members.Count;

        var shown = members.Count <= fits ? members.Count : Math.Max(1, fits - 1);

        for (var i = 0; i < shown; i++)
        {
            var member = members[i];
            var avatar = new AvatarView(member.Username, AvatarSize);
            avatar.SetMuted(member.Muted);
            _avatars.Children.Add(avatar);
        }

        var hidden = members.Count - shown;
        if (hidden > 0)
        {
            _avatars.Children.Add(Overflow(hidden));
        }
    }

    private static Border Overflow(int count) => new()
    {
        Width = AvatarSize,
        Height = AvatarSize,
        CornerRadius = new CornerRadius(AvatarSize / 2),
        Background = (Brush)Application.Current.Resources["TcSurfaceHoverBrush"],
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["TcBorderBrush"],
        Child = new TextBlock
        {
            Text = $"+{count}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}
