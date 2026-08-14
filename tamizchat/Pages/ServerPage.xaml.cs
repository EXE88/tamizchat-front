using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TamizChat.Controls;
using TamizChat.Core.Protocol;
using Room = TamizChat.Core.Protocol.Room;
using TamizChat.Services;

namespace TamizChat.Pages;

/// <summary>
/// The room grid.
///
/// One room fills the view; each room after that halves the tiles, down to a
/// quarter each at four. From five on, the tiles stay a quarter and the grid
/// scrolls — otherwise a busy server would shrink every room into a stamp.
///
/// Once the user is inside a room, that room takes over the view. They can drop
/// back to the grid to see everyone else and double-click another room to move.
/// </summary>
public sealed partial class ServerPage : Page
{
    private const double TileGap = 12;

    private readonly Dictionary<string, RoomTile> _tiles = [];
    private bool _showAll;

    public ServerPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += (_, _) => ServerSession.Instance.Changed -= OnSessionChanged;
        GridScroller.SizeChanged += (_, _) => Relayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ServerSession.Instance.Changed += OnSessionChanged;
        Render();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Render();

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        _showAll = !_showAll;
        Render();
    }

    private void Render()
    {
        var session = ServerSession.Instance;

        ServerTitle.Text = string.IsNullOrEmpty(session.ServerName)
            ? session.Server?.Name ?? "Server"
            : session.ServerName;

        if (!session.IsConnected)
        {
            Message.Text = "Not connected.";
            Message.Visibility = Visibility.Visible;
            GridScroller.Visibility = Visibility.Collapsed;
            ExpandButton.Visibility = Visibility.Collapsed;
            StatusLine.Text = "";
            return;
        }

        var rooms = session.Rooms;
        Message.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Message.Text = "This server has no rooms yet.";
        GridScroller.Visibility = rooms.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var inRoom = session.MyRoom;
        StatusLine.Text = inRoom is null
            ? $"{rooms.Count} rooms · {session.Users.Count} online · not in a room"
            : $"In {inRoom.Name} · {rooms.Count} rooms · {session.Users.Count} online";

        // The expand control only means anything while a room has taken over.
        ExpandButton.Visibility = inRoom is null ? Visibility.Collapsed : Visibility.Visible;
        ExpandButton.Content = _showAll ? "Focus my room" : "Show all rooms";

        SyncTiles(rooms, session.MyRoomId);
        Relayout();
    }

    /// <summary>Creates, updates and removes tiles so existing ones keep their state.</summary>
    private void SyncTiles(IReadOnlyList<Room> rooms, string myRoomId)
    {
        foreach (var stale in _tiles.Keys.Except(rooms.Select(r => r.Id)).ToList())
        {
            RoomGrid.Children.Remove(_tiles[stale]);
            _tiles.Remove(stale);
        }

        foreach (var room in rooms)
        {
            if (_tiles.TryGetValue(room.Id, out var existing))
            {
                existing.Update(room, room.Id == myRoomId);
                continue;
            }

            var tile = new RoomTile(room, room.Id == myRoomId);
            tile.Activated += OnTileActivated;
            _tiles[room.Id] = tile;
            RoomGrid.Children.Add(tile);
        }
    }

    private async void OnTileActivated(object? sender, string roomId)
    {
        try
        {
            await ServerSession.Instance.JoinRoomAsync(roomId);
            _showAll = false;
            Render();
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"Could not join: {ex.Message}";
        }
    }

    /// <summary>
    /// Sizes and positions the tiles. Called on every size change, because the
    /// tile size is a fraction of the viewport rather than a fixed number.
    /// </summary>
    private void Relayout()
    {
        var session = ServerSession.Instance;
        var rooms = session.Rooms;
        if (rooms.Count == 0 || GridScroller.ActualWidth <= 0)
        {
            return;
        }

        var width = GridScroller.ActualWidth;
        var height = GridScroller.ActualHeight;

        // Inside a room and not asked to see everything: that room owns the view.
        var focused = session.MyRoom is not null && !_showAll ? session.MyRoomId : null;
        if (focused is not null)
        {
            foreach (var (id, tile) in _tiles)
            {
                var visible = id == focused;
                tile.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible)
                {
                    Place(tile, 0, 0, width, height);
                }
            }

            RoomGrid.Width = width;
            RoomGrid.Height = height;
            return;
        }

        // The same subdivision the people inside a room use.
        var slots = TileLayout.Arrange(rooms.Count, width, height, TileGap);

        for (var i = 0; i < rooms.Count && i < slots.Length; i++)
        {
            if (!_tiles.TryGetValue(rooms[i].Id, out var tile))
            {
                continue;
            }

            tile.Visibility = Visibility.Visible;
            Place(tile, slots[i].X, slots[i].Y, slots[i].Width, slots[i].Height);
        }

        RoomGrid.Width = width;
        RoomGrid.Height = TileLayout.ContentHeight(rooms.Count, height, TileGap);
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        element.Margin = new Thickness(x, y, 0, 0);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
