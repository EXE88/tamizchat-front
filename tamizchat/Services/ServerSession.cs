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
