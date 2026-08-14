using Microsoft.UI.Dispatching;
using TamizChat.Core;
using TamizChat.Core.Protocol;

namespace TamizChat.Services;

/// <summary>
/// The app's one live connection to a server, and the room tree it is showing.
///
/// The UI reads state from here and listens for <see cref="Changed"/>; it never
/// talks to <see cref="TamizChatClient"/> directly, so there is a single place
/// that knows how to keep the tree in step with the server.
/// </summary>
public sealed class ServerSession
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private TamizChatClient? _client;
    private bool _refreshQueued;

    public static ServerSession Instance { get; } = new();

    /// <summary>Raised on the UI thread whenever the rooms or their members changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the connection drops for any reason.</summary>
    public event EventHandler<string>? Dropped;

    public bool IsConnected => _client?.IsConnected == true;

    public ServerEntry? Server { get; private set; }

    public string ServerName { get; private set; } = "";

    public IReadOnlyList<Room> Rooms { get; private set; } = [];

    /// <summary>Everyone connected, including people not in any room.</summary>
    public IReadOnlyList<User> Users { get; private set; } = [];

    public string MyUuid => SettingsStore.Current.ClientUuid;

    /// <summary>The room the user is in, or empty when they are in none.</summary>
    public string MyRoomId { get; private set; } = "";

    public async Task ConnectAsync(ServerEntry entry)
    {
        await DisconnectAsync().ConfigureAwait(true);

        var username = string.IsNullOrWhiteSpace(entry.Username)
            ? SettingsStore.Current.Username
            : entry.Username;

        var client = new TamizChatClient();
        client.ServerEvent += OnServerEvent;
        client.Disconnected += OnDisconnected;

        var welcome = await client.ConnectAsync(entry.WebSocketUrl, MyUuid, username).ConfigureAwait(true);

        _client = client;
        Server = entry;
        ServerName = welcome.ServerName;
        Rooms = welcome.Rooms;
        Users = welcome.Users;
        MyRoomId = welcome.You.RoomId;
        Raise();
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        _client = null;
        Server = null;
        Rooms = [];
        Users = [];
        MyRoomId = "";

        if (client is not null)
        {
            client.ServerEvent -= OnServerEvent;
            client.Disconnected -= OnDisconnected;
            await client.DisposeAsync().ConfigureAwait(true);
        }

        Raise();
    }

    /// <summary>Moves into a room. The server sends the leave for the previous one.</summary>
    public async Task JoinRoomAsync(string roomId, string password = "")
    {
        if (_client is null)
        {
            return;
        }

        var reply = await _client
            .RequestAsync(MessageTypes.RoomJoin, new RoomJoinRequest { RoomId = roomId, Password = password })
            .ConfigureAwait(true);

        var joined = TamizChatClient.Deserialize<RoomJoined>(reply);
        if (joined is not null)
        {
            MyRoomId = joined.Room.Id;
        }

        await RefreshRoomsAsync().ConfigureAwait(true);
    }

    public async Task LeaveRoomAsync()
    {
        if (_client is null)
        {
            return;
        }

        await _client.RequestAsync(MessageTypes.RoomLeave).ConfigureAwait(true);
        MyRoomId = "";
        await RefreshRoomsAsync().ConfigureAwait(true);
    }

    /// <summary>The room the user is currently inside, if any.</summary>
    public Room? MyRoom => Rooms.FirstOrDefault(r => r.Id == MyRoomId);

    private void OnServerEvent(object? sender, ServerEventArgs e)
    {
        switch (e.Type)
        {
            // Membership and presence all change the same thing — who is where —
            // so rather than patching the tree from each event, the whole tree is
            // re-read. It arrives in one frame and is always consistent.
            case MessageTypes.RoomMemberJoined:
            case MessageTypes.RoomMemberLeft:
            case MessageTypes.UserJoined:
            case MessageTypes.UserLeft:
            case MessageTypes.UserUpdated:
            case "room.created":
            case "room.updated":
            case "room.deleted":
            case "room.purged":
                QueueRefresh();
                break;

            case MessageTypes.RoomLeft:
                MyRoomId = "";
                QueueRefresh();
                break;
        }
    }

    /// <summary>
    /// Coalesces bursts of events — twenty people joining at once should cost one
    /// round trip, not twenty.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ui.TryEnqueue(async () =>
        {
            await Task.Delay(120).ConfigureAwait(true);
            _refreshQueued = false;
            await RefreshRoomsAsync().ConfigureAwait(true);
        });
    }

    private async Task RefreshRoomsAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            var reply = await _client.RequestAsync(MessageTypes.RoomList).ConfigureAwait(true);
            var list = TamizChatClient.Deserialize<RoomListReply>(reply);
            if (list is not null)
            {
                Rooms = list.Rooms;

                // The server is the authority on where we are; trusting our own
                // copy would drift after a move by a moderator.
                var mine = Rooms.FirstOrDefault(r => r.Members.Any(m => m.ClientUuid == MyUuid));
                MyRoomId = mine?.Id ?? "";
            }
        }
        catch (Exception)
        {
            // A failed refresh leaves the last good tree on screen.
        }

        Raise();
    }

    private void OnDisconnected(object? sender, string reason) =>
        _ui.TryEnqueue(() =>
        {
            _client = null;
            Dropped?.Invoke(this, reason);
            Raise();
        });

    private void Raise() => _ui.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
}
