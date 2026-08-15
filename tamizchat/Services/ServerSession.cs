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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private TamizChatClient? _client;
    private bool _refreshQueued;

    public static ServerSession Instance { get; } = new();

    /// <summary>Raised on the UI thread whenever the rooms or their members changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the connection drops for any reason.</summary>
    public event EventHandler<string>? Dropped;

    /// <summary>Raised for each message arriving in the room the user is in.</summary>
    public event EventHandler<ChatMessage>? MessageReceived;

    /// <summary>Raised as people start and stop typing.</summary>
    public event EventHandler<ChatTyping>? TypingChanged;

    public bool IsConnected => _client?.IsConnected == true;

    public ServerEntry? Server { get; private set; }

    public string ServerName { get; private set; } = "";

    public IReadOnlyList<Room> Rooms { get; private set; } = [];

    /// <summary>Everyone connected, including people not in any room.</summary>
    public IReadOnlyList<User> Users { get; private set; } = [];

    public string MyUuid => SettingsStore.Current.ClientUuid;

    /// <summary>
    /// What this user is allowed to do, as the server sees it.
    ///
    /// Used only to decide which menu entries are worth showing. The server
    /// checks every action again regardless — this is politeness, not security.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    /// <summary>Every role the server defines, for the grant/revoke menu.</summary>
    public IReadOnlyList<Role> Roles { get; private set; } = [];

    public bool Can(string permission) => Permissions.Contains(permission);

    /// <summary>True when any moderation action at all is available.</summary>
    public bool IsModerator =>
        Can("kick") || Can("ban") || Can("mute") || Can("move_users") || Can("manage_roles");

    // --- moderation ---

    public Task KickAsync(string clientUuid, string reason) =>
        RequestVoidAsync(MessageTypes.AdminKick, new AdminTarget { ClientUuid = clientUuid, Reason = reason });

    public Task BanAsync(string clientUuid, string reason, long seconds) =>
        RequestVoidAsync(MessageTypes.AdminBan, new AdminSanction
        {
            ClientUuid = clientUuid,
            Reason = reason,
            DurationSec = seconds,
        });

    public Task MuteAsync(string clientUuid, string reason, long seconds) =>
        RequestVoidAsync(MessageTypes.AdminMute, new AdminSanction
        {
            ClientUuid = clientUuid,
            Reason = reason,
            DurationSec = seconds,
        });

    public Task UnmuteAsync(string clientUuid) =>
        RequestVoidAsync(MessageTypes.AdminUnmute, new AdminTarget { ClientUuid = clientUuid });

    /// <summary>An empty room id takes them out of every room.</summary>
    public Task MoveAsync(string clientUuid, string roomId) =>
        RequestVoidAsync(MessageTypes.AdminMove, new AdminMove { ClientUuid = clientUuid, RoomId = roomId });

    public Task GrantRoleAsync(string clientUuid, string roleId) =>
        RequestVoidAsync(MessageTypes.RoleGrant, new RoleAssignment { ClientUuid = clientUuid, RoleId = roleId });

    public Task RevokeRoleAsync(string clientUuid, string roleId) =>
        RequestVoidAsync(MessageTypes.RoleRevoke, new RoleAssignment { ClientUuid = clientUuid, RoleId = roleId });

    /// <summary>
    /// Sends a moderation request and waits for the acknowledgement.
    ///
    /// Requested rather than sent: the reply is what carries the refusal when
    /// the server says no — a moderator pressing Kick on somebody above them
    /// needs to be told, not silently ignored.
    /// </summary>
    private async Task RequestVoidAsync(string type, object payload)
    {
        if (_client is null)
        {
            return;
        }

        await _client.RequestAsync(type, payload).ConfigureAwait(true);
        await RefreshRoomsAsync().ConfigureAwait(true);
    }

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
        Permissions = welcome.Permissions;
        Roles = welcome.Roles;
        Raise();

        Cue(AppSound.YouJoinedServer);
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

        // Whether this is the first room of the session or a move between rooms
        // is only knowable here, before MyRoomId is overwritten.
        var roomIdBefore = MyRoomId;
        var switching = !string.IsNullOrEmpty(roomIdBefore);

        var joined = TamizChatClient.Deserialize<RoomJoined>(reply);
        if (joined is not null)
        {
            MyRoomId = joined.Room.Id;

            // Only on a genuine move. Rejoining the room you are already in
            // happens on reconnects and should be silent.
            if (switching && joined.Room.Id != roomIdBefore)
            {
                Cue(AppSound.YouSwitchedRoom);
            }
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

    public async Task SendMessageAsync(string text)
    {
        if (_client is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // The reply is the posted message, and it also arrives as a broadcast to
        // everyone else — so the sender's own copy comes from here, not the event.
        var reply = await _client
            .RequestAsync(MessageTypes.ChatSend, new ChatSend { Text = text.Trim() })
            .ConfigureAwait(true);

        if (TamizChatClient.Deserialize<ChatMessage>(reply) is { } posted)
        {
            MessageReceived?.Invoke(this, posted);
        }
    }

    /// <summary>
    /// Reads backwards through the room's buffer. Pass the seq of the oldest
    /// message held to get the page before it.
    /// </summary>
    public async Task<ChatHistoryReply> LoadHistoryAsync(long beforeSeq = 0, int limit = 50)
    {
        if (_client is null)
        {
            return new ChatHistoryReply();
        }

        var reply = await _client
            .RequestAsync(MessageTypes.ChatHistory, new ChatHistoryRequest { BeforeSeq = beforeSeq, Limit = limit })
            .ConfigureAwait(true);

        return TamizChatClient.Deserialize<ChatHistoryReply>(reply) ?? new ChatHistoryReply();
    }

    /// <summary>Fire and forget: the protocol defines no reply, and a lost one is harmless.</summary>
    public Task SendTypingAsync(bool typing) =>
        _client is null
            ? Task.CompletedTask
            : _client.SendAsync(MessageTypes.ChatTyping, new { typing });

    /// <summary>
    /// Uploads a file: permission over the socket, bytes over HTTP.
    ///
    /// The server checks the size, the quota and the permission before a single
    /// byte is sent, and posts the file into the room itself — so there is no
    /// message to send afterwards, it arrives as a normal chat message.
    /// </summary>
    public async Task UploadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_client is null || Server is null)
        {
            return;
        }

        var info = new FileInfo(path);

        var reply = await _client.RequestAsync(
            MessageTypes.FileUploadRequest,
            new FileUploadRequest { Name = info.Name, Size = info.Length },
            cancellationToken).ConfigureAwait(true);

        var ticket = TamizChatClient.Deserialize<FileUploadTicket>(reply)
                     ?? throw new InvalidOperationException("the server did not return an upload ticket");

        // The ticket carries a path; the host comes from the server we are on.
        var url = ticket.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? ticket.Url
            : $"{Server.HttpUrl.TrimEnd('/')}/{ticket.Url.TrimStart('/')}";

        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ticket.Token);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            throw new InvalidOperationException($"upload refused ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Asks for a short-lived link to a file. The link only works for the room the
    /// user is in right now, so it cannot be kept or passed on.
    /// </summary>
    public async Task<FileDownload?> GetDownloadAsync(string fileId)
    {
        if (_client is null)
        {
            return null;
        }

        var reply = await _client
            .RequestAsync(MessageTypes.FileDownloadToken, new FileDownloadRequest { FileId = fileId })
            .ConfigureAwait(true);

        var download = TamizChatClient.Deserialize<FileDownload>(reply);
        if (download is null || Server is null)
        {
            return download;
        }

        download.Url = Absolute(download.Url);
        download.ThumbUrl = Absolute(download.ThumbUrl);
        return download;
    }

    // --- media ---

    /// <summary>Raised as people turn their microphone or camera on and off.</summary>
    public event EventHandler<MediaStateEvent>? MediaStateChanged;

    /// <summary>
    /// Credentials for the LiveKit room matching the room the user is in right
    /// now. They expire in fifteen minutes and are room-scoped, so this is asked
    /// again on every join rather than cached.
    /// </summary>
    public async Task<MediaToken?> GetMediaTokenAsync()
    {
        if (_client is null)
        {
            return null;
        }

        var reply = await _client.RequestAsync(MessageTypes.MediaToken).ConfigureAwait(true);
        return TamizChatClient.Deserialize<MediaToken>(reply);
    }

    /// <summary>
    /// Reports what we have switched on, so everyone's room tree can show the
    /// right icons. It is our own report because only this client knows whether
    /// the microphone is really open; what we are *allowed* to publish is decided
    /// server-side and enforced by LiveKit.
    /// </summary>
    public Task SetMediaStateAsync(bool mic, bool cam = false, bool screen = false) =>
        _client is null
            ? Task.CompletedTask
            : _client.SendAsync(MessageTypes.MediaSetState, new MediaSetState { Mic = mic, Cam = cam, Screen = screen });

    // --- paint ---

    /// <summary>Raised for every stroke event from the board, ours excluded.</summary>
    public event EventHandler<Stroke>? StrokeStarted;

    public event EventHandler<PaintAppend>? StrokeAppended;

    public event EventHandler<PaintEnd>? StrokeEnded;

    public event EventHandler<PaintUndo>? StrokeUndone;

    public event EventHandler<PaintCleared>? BoardCleared;

    /// <summary>Starts a stroke and returns it, with the id the server assigned.</summary>
    public async Task<Stroke?> BeginStrokeAsync(PaintBegin begin)
    {
        if (_client is null)
        {
            return null;
        }

        var reply = await _client.RequestAsync(MessageTypes.PaintBegin, begin).ConfigureAwait(true);
        return TamizChatClient.Deserialize<Stroke>(reply);
    }

    /// <summary>
    /// Adds points to a stroke in progress. Deliberately has no reply: this is
    /// the most frequent message in the system and the client has already drawn
    /// the point locally.
    /// </summary>
    public Task AppendStrokeAsync(string strokeId, List<PaintPoint> points) =>
        _client is null
            ? Task.CompletedTask
            : _client.SendAsync(MessageTypes.PaintAppend, new PaintAppend { StrokeId = strokeId, Points = points });

    /// <summary>
    /// Finishes a stroke. Also fire and forget: the server only answers
    /// <c>paint.end</c> when it fails, so waiting for a reply would wait forever.
    /// A failure arrives as an uncorrelated error frame instead.
    /// </summary>
    public Task EndStrokeAsync(string strokeId) =>
        _client is null
            ? Task.CompletedTask
            : _client.SendAsync(MessageTypes.PaintEnd, new PaintEnd { StrokeId = strokeId });

    /// <summary>
    /// Undoes the caller's last stroke.
    ///
    /// The server excludes whoever asked from the broadcast and answers them with
    /// an id-correlated reply instead — the project's standard pattern, so nobody
    /// gets the same event twice. That means the reply has to be turned back into
    /// the local event, or the caller's own board never updates.
    /// </summary>
    public async Task UndoStrokeAsync()
    {
        if (_client is null)
        {
            return;
        }

        var reply = await _client.RequestAsync(MessageTypes.PaintUndo).ConfigureAwait(true);
        if (TamizChatClient.Deserialize<PaintUndo>(reply) is { } undone)
        {
            StrokeUndone?.Invoke(this, undone);
        }
    }

    /// <summary>Clears the board. Same reply-not-broadcast rule as undo.</summary>
    public async Task ClearBoardAsync(string scope)
    {
        if (_client is null)
        {
            return;
        }

        var reply = await _client
            .RequestAsync(MessageTypes.PaintClear, new PaintClear { Scope = scope })
            .ConfigureAwait(true);

        if (TamizChatClient.Deserialize<PaintCleared>(reply) is { } cleared)
        {
            BoardCleared?.Invoke(this, cleared);
        }
    }

    /// <summary>The whole board. The server never pushes this; it must be asked for.</summary>
    // --- administration ---
    //
    // Every one of these is a *request*, never a send. The server re-checks the
    // permission and the priority rule on each, and a refusal comes back as the
    // reply — an admin acting on somebody above them has to be told, not
    // silently ignored.

    public async Task<IReadOnlyList<Sanction>> GetSanctionsAsync()
    {
        if (_client is null)
        {
            return [];
        }

        var reply = await _client.RequestAsync(MessageTypes.AdminSanctions).ConfigureAwait(true);
        return TamizChatClient.Deserialize<SanctionList>(reply)?.Sanctions ?? [];
    }

    public Task UnbanAsync(string clientUuid) =>
        RequireClient().RequestAsync(MessageTypes.AdminUnban, new AdminTarget { ClientUuid = clientUuid });

    public async Task<Role?> CreateRoleAsync(RoleSpec spec)
    {
        var reply = await RequireClient().RequestAsync(MessageTypes.RoleCreate, spec).ConfigureAwait(true);
        await RefreshRolesAsync().ConfigureAwait(true);
        return TamizChatClient.Deserialize<Role>(reply);
    }

    public async Task<Role?> UpdateRoleAsync(RoleSpec spec)
    {
        var reply = await RequireClient().RequestAsync(MessageTypes.RoleUpdate, spec).ConfigureAwait(true);
        await RefreshRolesAsync().ConfigureAwait(true);
        return TamizChatClient.Deserialize<Role>(reply);
    }

    public async Task DeleteRoleAsync(string roleId)
    {
        await RequireClient().RequestAsync(MessageTypes.RoleDelete, new RoleDelete { RoleId = roleId }).ConfigureAwait(true);
        await RefreshRolesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-reads the role list.
    ///
    /// `admin.role.list` is open to everyone, because every client needs the
    /// names and colours to render somebody's tag — not only administrators.
    /// </summary>
    public async Task RefreshRolesAsync()
    {
        if (_client is null)
        {
            return;
        }

        var reply = await _client.RequestAsync(MessageTypes.RoleList).ConfigureAwait(true);
        var roles = TamizChatClient.Deserialize<List<Role>>(reply);
        if (roles is not null)
        {
            Roles = roles;
            Raise();
        }
    }

    public async Task CreateRoomAsync(RoomCreate room)
    {
        await RequireClient().RequestAsync(MessageTypes.RoomCreate, room).ConfigureAwait(true);
        await RefreshRoomsAsync().ConfigureAwait(true);
    }

    public async Task UpdateRoomAsync(RoomUpdate room)
    {
        await RequireClient().RequestAsync(MessageTypes.RoomUpdate, room).ConfigureAwait(true);
        await RefreshRoomsAsync().ConfigureAwait(true);
    }

    public async Task DeleteRoomAsync(string roomId)
    {
        await RequireClient().RequestAsync(MessageTypes.RoomDelete, new RoomDelete { RoomId = roomId }).ConfigureAwait(true);
        await RefreshRoomsAsync().ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<Bot>> GetBotsAsync()
    {
        if (_client is null)
        {
            return [];
        }

        var reply = await _client.RequestAsync(MessageTypes.BotList).ConfigureAwait(true);
        return TamizChatClient.Deserialize<BotListReply>(reply)?.Bots ?? [];
    }

    public Task ControlBotAsync(string botId, string action, int? trackIndex = null) =>
        RequireClient().RequestAsync(
            MessageTypes.BotControl,
            new BotControl { BotId = botId, Action = action, TrackIndex = trackIndex });

    public Task MoveBotAsync(string botId, string roomId) =>
        RequireClient().RequestAsync(MessageTypes.BotMove, new BotMove { BotId = botId, RoomId = roomId });

    // --- bots an administrator configures ---
    //
    // A bot created from here has no folder path: the server gives it storage of
    // its own and the music arrives by upload, below. A path typed by a client
    // would be a path on somebody else's machine, and the server refuses one.

    public Task<Bot?> CreateBotAsync(BotSpec spec) => BotRequestAsync(MessageTypes.BotCreate, spec);

    public Task<Bot?> UpdateBotAsync(BotSpec spec) => BotRequestAsync(MessageTypes.BotUpdate, spec);

    public Task DeleteBotAsync(string botId) =>
        RequireClient().RequestAsync(MessageTypes.BotDelete, new BotRef { BotId = botId });

    /// <summary>The tracks a bot would play, in order.</summary>
    public Task<IReadOnlyList<BotTrack>> GetBotQueueAsync(string botId) => TracksAsync(botId, "");

    /// <summary>
    /// The tracks of one playlist, which need not be the one playing — that is
    /// how a playlist can be filled and tidied before it is switched to.
    /// </summary>
    public Task<IReadOnlyList<BotTrack>> GetPlaylistTracksAsync(string botId, string playlistId) =>
        TracksAsync(botId, playlistId);

    private async Task<IReadOnlyList<BotTrack>> TracksAsync(string botId, string playlistId)
    {
        var reply = await RequireClient()
            .RequestAsync(MessageTypes.BotQueue, new BotRequest { BotId = botId, PlaylistId = playlistId })
            .ConfigureAwait(true);

        return TamizChatClient.Deserialize<BotQueueReply>(reply)?.Tracks ?? [];
    }

    public async Task<BotPlaylistList> GetPlaylistsAsync(string botId)
    {
        var reply = await RequireClient()
            .RequestAsync(MessageTypes.BotPlaylistList, new BotRequest { BotId = botId })
            .ConfigureAwait(true);

        return TamizChatClient.Deserialize<BotPlaylistList>(reply) ?? new BotPlaylistList { BotId = botId };
    }

    public Task CreatePlaylistAsync(string botId, string name) =>
        RequireClient().RequestAsync(
            MessageTypes.BotPlaylistCreate,
            new BotPlaylistSpec { BotId = botId, Name = name });

    public Task RenamePlaylistAsync(string botId, string playlistId, string name) =>
        RequireClient().RequestAsync(
            MessageTypes.BotPlaylistRename,
            new BotPlaylistSpec { BotId = botId, PlaylistId = playlistId, Name = name });

    public Task DeletePlaylistAsync(string botId, string playlistId) =>
        RequireClient().RequestAsync(
            MessageTypes.BotPlaylistDelete,
            new BotPlaylistSpec { BotId = botId, PlaylistId = playlistId });

    /// <summary>
    /// Picks what the bot plays from; an empty playlist id means its own library.
    /// This stops playback server-side, so the reply is the state to draw.
    /// </summary>
    public Task<Bot?> SelectPlaylistAsync(string botId, string playlistId) =>
        BotRequestAsync(
            MessageTypes.BotPlaylistSelect,
            new BotPlaylistSpec { BotId = botId, PlaylistId = playlistId });

    public Task DeleteTrackAsync(string botId, string playlistId, int index) =>
        RequireClient().RequestAsync(
            MessageTypes.BotTrackDelete,
            new BotTrackRef { BotId = botId, PlaylistId = playlistId, Index = index });

    /// <summary>
    /// Adds one track to a playlist: permission over the socket, bytes over HTTP
    /// — the same two steps as a room file, and for the same reason. Everything
    /// that can be refused is refused before a byte leaves here.
    ///
    /// Unlike a room file, the name matters: the server stores the track under
    /// it and a listener sees it as the title.
    /// </summary>
    public async Task UploadTrackAsync(string botId, string playlistId, string path,
        CancellationToken cancellationToken = default)
    {
        if (Server is null)
        {
            return;
        }

        var info = new FileInfo(path);

        var reply = await RequireClient().RequestAsync(
            MessageTypes.BotTrackUploadRequest,
            new BotTrackUploadRequest
            {
                BotId = botId,
                PlaylistId = playlistId,
                Name = info.Name,
                Size = info.Length,
            },
            cancellationToken).ConfigureAwait(true);

        var ticket = TamizChatClient.Deserialize<BotTrackUploadTicket>(reply)
                     ?? throw new InvalidOperationException("the server did not return an upload ticket");

        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        using var request = new HttpRequestMessage(HttpMethod.Post, Absolute(ticket.Url)) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ticket.Token);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            throw new InvalidOperationException($"upload refused ({(int)response.StatusCode}): {body}");
        }
    }

    private async Task<Bot?> BotRequestAsync(string type, object payload)
    {
        var reply = await RequireClient().RequestAsync(type, payload).ConfigureAwait(true);
        return TamizChatClient.Deserialize<Bot>(reply);
    }

    /// <summary>Throws rather than silently doing nothing when there is no connection.</summary>
    private TamizChatClient RequireClient() =>
        _client ?? throw new InvalidOperationException("not connected to a server");

    public async Task<PaintState> GetBoardAsync()
    {
        if (_client is null)
        {
            return new PaintState();
        }

        var reply = await _client.RequestAsync(MessageTypes.PaintState).ConfigureAwait(true);
        return TamizChatClient.Deserialize<PaintState>(reply) ?? new PaintState();
    }

    private string Absolute(string pathOrUrl)
    {
        if (string.IsNullOrEmpty(pathOrUrl) || Server is null
            || pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        return $"{Server.HttpUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
    }

    /// <summary>
    /// Decides which notification a membership change deserves.
    ///
    /// Only events about **your own** room make a sound. A busy server has
    /// people moving constantly and a chime for each one is unusable — the point
    /// is to know who walked into the room you are sitting in.
    /// </summary>
    private void CueMembership(ServerEventArgs e)
    {
        var change = e.As<RoomMemberEvent>();
        if (change is null || string.IsNullOrEmpty(MyRoomId) || change.RoomId != MyRoomId)
        {
            return;
        }

        // Your own arrival is announced by the room.joined path instead, which
        // knows whether it was a first join or a switch.
        if (change.User.ClientUuid == MyUuid)
        {
            return;
        }

        var moved = change.Reason is "moved_by_admin";

        Cue(e.Type == MessageTypes.RoomMemberJoined
            ? moved ? AppSound.UserMovedToYourRoom : AppSound.UserJoinedYourRoom
            : moved ? AppSound.UserMovedOutOfYourRoom : AppSound.UserLeftYourRoom);
    }

    private void Cue(AppSound sound) => _ui.TryEnqueue(() => EventSounds.Instance.Play(sound));

    private void OnServerEvent(object? sender, ServerEventArgs e)
    {
        switch (e.Type)
        {
            // Membership and presence all change the same thing — who is where —
            // so rather than patching the tree from each event, the whole tree is
            // re-read. It arrives in one frame and is always consistent.
            case MessageTypes.RoomMemberJoined:
            case MessageTypes.RoomMemberLeft:
                CueMembership(e);
                QueueRefresh();
                break;

            case MessageTypes.UserLeft:
                // Someone disconnecting entirely. Whoever was in your room also
                // produces room.member_left, so this is only the server-wide
                // departure and is deliberately the quieter of the two.
                if (e.As<UserLeftEvent>() is { } gone && gone.ClientUuid != MyUuid)
                {
                    Cue(AppSound.UserLeftServer);
                }

                QueueRefresh();
                break;

            case MessageTypes.UserJoined:
            case MessageTypes.UserUpdated:
            case "room.created":
            case "room.updated":
            case "room.deleted":
            case "room.purged":
                QueueRefresh();
                break;

            case MessageTypes.RoomLeft:
                // The only way to tell "a moderator moved me" from "I left" is
                // this reason; both otherwise look identical from here.
                if (e.As<RoomLeftEvent>()?.Reason == "moved_by_admin")
                {
                    Cue(AppSound.YouWereMoved);
                }

                MyRoomId = "";
                QueueRefresh();
                break;

            case MessageTypes.UserRolesChanged:
                // Only ever sent to the person it concerns, so this is always
                // our own new permission set.
                if (e.As<RolesChanged>() is { } changed && changed.ClientUuid == MyUuid)
                {
                    Permissions = changed.Permissions;
                    _ui.TryEnqueue(Raise);
                }

                break;

            case "user.kicked":
            case "user.banned":
                if (e.As<SanctionEvent>() is { } sanction && sanction.ClientUuid == MyUuid)
                {
                    Cue(e.Type == "user.banned" ? AppSound.YouWereBanned : AppSound.YouWereKicked);
                }

                break;

            case MessageTypes.ChatMessage:
                if (e.As<ChatMessage>() is { } message)
                {
                    _ui.TryEnqueue(() => MessageReceived?.Invoke(this, message));
                }

                break;

            case MessageTypes.ChatTyping:
                if (e.As<ChatTyping>() is { } typing)
                {
                    _ui.TryEnqueue(() => TypingChanged?.Invoke(this, typing));
                }

                break;

            case MessageTypes.MediaState:
                if (e.As<MediaStateEvent>() is { } media)
                {
                    _ui.TryEnqueue(() => MediaStateChanged?.Invoke(this, media));
                }

                break;

            case MessageTypes.PaintBegin:
                if (e.As<Stroke>() is { } stroke)
                {
                    _ui.TryEnqueue(() => StrokeStarted?.Invoke(this, stroke));
                }

                break;

            case MessageTypes.PaintAppend:
                if (e.As<PaintAppend>() is { } append)
                {
                    _ui.TryEnqueue(() => StrokeAppended?.Invoke(this, append));
                }

                break;

            case MessageTypes.PaintEnd:
                if (e.As<PaintEnd>() is { } ended)
                {
                    _ui.TryEnqueue(() => StrokeEnded?.Invoke(this, ended));
                }

                break;

            case MessageTypes.PaintUndo:
                if (e.As<PaintUndo>() is { } undone)
                {
                    _ui.TryEnqueue(() => StrokeUndone?.Invoke(this, undone));
                }

                break;

            case MessageTypes.PaintClear:
                if (e.As<PaintCleared>() is { } cleared)
                {
                    _ui.TryEnqueue(() => BoardCleared?.Invoke(this, cleared));
                }

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
