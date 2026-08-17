using System.Text.Json.Serialization;

namespace TamizChat.Core.Protocol;

/// <summary>The handshake, which must be the first frame after the socket opens.</summary>
public sealed class Hello
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("protocol")]
    public int Protocol { get; set; } = 1;

    [JsonPropertyName("client_version")]
    public string ClientVersion { get; set; } = "0.1.0";
}

public sealed class User
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>
    /// What they have switched on, as their own client reports it. Present in
    /// the room tree as well as in events, so somebody joining a room sees the
    /// microphone icons immediately rather than after the next change.
    /// </summary>
    [JsonPropertyName("media")]
    public MediaSetState Media { get; set; } = new();

    /// <summary>
    /// The tag of this user's profile picture, empty when they have none.
    ///
    /// Not the picture and not a URL: it changes only when the picture does, so
    /// it is both the cache key and the signal to fetch again. The bytes come
    /// from <c>/api/v1/avatar/{client_uuid}</c>.
    /// </summary>
    [JsonPropertyName("avatar")]
    public string Avatar { get; set; } = "";

    /// <summary>The letter drawn when there is no profile picture.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username.Trim()[..1].ToUpperInvariant();
}

public sealed class Room
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("has_password")]
    public bool HasPassword { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("member_count")]
    public int MemberCount { get; set; }

    [JsonPropertyName("members")]
    public List<User> Members { get; set; } = [];

    [JsonPropertyName("required_role_id")]
    public string RequiredRoleId { get; set; } = "";
}

public sealed class Limits
{
    [JsonPropertyName("username_min")]
    public int UsernameMin { get; set; }

    [JsonPropertyName("username_max")]
    public int UsernameMax { get; set; }

    [JsonPropertyName("max_users")]
    public int MaxUsers { get; set; }

    [JsonPropertyName("message_max")]
    public int MessageMax { get; set; }

    [JsonPropertyName("history_limit")]
    public int HistoryLimit { get; set; }

    [JsonPropertyName("stickers_enabled")]
    public bool StickersEnabled { get; set; }
}

/// <summary>The reply to a successful handshake: the whole world in one frame.</summary>
public sealed class Welcome
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("server_uuid")]
    public string ServerUuid { get; set; } = "";

    [JsonPropertyName("server_name")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("welcome_message")]
    public string WelcomeMessage { get; set; } = "";

    [JsonPropertyName("heartbeat_sec")]
    public int HeartbeatSec { get; set; }

    [JsonPropertyName("you")]
    public User You { get; set; } = new();

    [JsonPropertyName("users")]
    public List<User> Users { get; set; } = [];

    [JsonPropertyName("rooms")]
    public List<Room> Rooms { get; set; } = [];

    [JsonPropertyName("roles")]
    public List<Role> Roles { get; set; } = [];

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    /// <summary>
    /// The server's bots and what each is doing. They arrive here rather than as
    /// room members, so a client that wants to draw them in a room has to keep
    /// this list and follow `bot.state`.
    /// </summary>
    [JsonPropertyName("bots")]
    public List<Bot> Bots { get; set; } = [];

    [JsonPropertyName("limits")]
    public Limits Limits { get; set; } = new();
}

/// <summary>
/// What the server detected about an uploaded file. The MIME type comes from the
/// bytes themselves, not from the name or anything the client claimed.
/// </summary>
public sealed class Attachment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mime")]
    public string Mime { get; set; } = "";

    /// <summary>"image" or "file".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "file";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("has_thumb")]
    public bool HasThumb { get; set; }

    public bool IsImage => Kind == "image";
}

public sealed class ChatMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("author")]
    public User Author { get; set; } = new();

    /// <summary>"text", "sticker" or "file".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>Set on messages of kind "file".</summary>
    [JsonPropertyName("attachment")]
    public Attachment? Attachment { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}

// --- files ---

/// <summary>Asks permission to upload, before a single byte is sent.</summary>
public sealed class FileUploadRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Permission to upload exactly one file, once.</summary>
public sealed class FileUploadTicket
{
    [JsonPropertyName("upload_id")]
    public string UploadId { get; set; } = "";

    /// <summary>Path to POST the raw bytes to.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("max_size")]
    public long MaxSize { get; set; }
}

/// <summary>
/// Permission to upload exactly one profile picture, once.
///
/// There is no matching download ticket: a picture is fetched from
/// <c>/api/v1/avatar/{client_uuid}</c> with no token, because everyone on the
/// server is shown it beside a name they can already see.
/// </summary>
public sealed class AvatarUploadTicket
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("max_size")]
    public long MaxSize { get; set; }
}

public sealed class FileDownloadRequest
{
    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";
}

public sealed class FileDownload
{
    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("thumb_url")]
    public string ThumbUrl { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
}

// --- paint ---

public sealed class PaintPoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

/// <summary>One continuous mark on the board.</summary>
public sealed class Stroke
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    /// <summary>The author's client UUID.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "pen";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#ffffff";

    /// <summary>Normalized like the coordinates, not pixels.</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("points")]
    public List<PaintPoint> Points { get; set; } = [];

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

public sealed class PaintBegin
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "pen";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#ffffff";

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("points")]
    public List<PaintPoint> Points { get; set; } = [];
}

public sealed class PaintAppend
{
    [JsonPropertyName("stroke_id")]
    public string StrokeId { get; set; } = "";

    [JsonPropertyName("points")]
    public List<PaintPoint> Points { get; set; } = [];
}

public sealed class PaintEnd
{
    [JsonPropertyName("stroke_id")]
    public string StrokeId { get; set; } = "";
}

public sealed class PaintUndo
{
    [JsonPropertyName("stroke_id")]
    public string StrokeId { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";
}

public sealed class PaintClear
{
    /// <summary>"mine" or "all"; "all" needs moderation rights.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "mine";
}

public sealed class PaintCleared
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("by")]
    public string By { get; set; } = "";
}

public sealed class PaintState
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("strokes")]
    public List<Stroke> Strokes { get; set; } = [];

    [JsonPropertyName("max_strokes")]
    public int MaxStrokes { get; set; }
}

public sealed class RoomJoinRequest
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public sealed class RoomJoined
{
    [JsonPropertyName("room")]
    public Room Room { get; set; } = new();

    /// <summary>
    /// Empty when the user asked to be here, and <c>moved_by_admin</c> when they
    /// did not. The same frame arrives both as the reply to our own
    /// <c>room.join</c> and, unsolicited, when somebody moves this user — this
    /// is the only thing that tells the two apart.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>The reply to <c>room.list</c>: the whole tree, members included.</summary>
public sealed class RoomListReply
{
    [JsonPropertyName("rooms")]
    public List<Room> Rooms { get; set; } = [];
}

public sealed class ChatSend
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

/// <summary>Pages backwards through a room's buffer.</summary>
public sealed class ChatHistoryRequest
{
    /// <summary>The seq of the oldest message already held. Zero means "the newest page".</summary>
    [JsonPropertyName("before_seq")]
    public long BeforeSeq { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 50;
}

public sealed class ChatHistoryReply
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

public sealed class ChatTyping
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("typing")]
    public bool Typing { get; set; }
}

/// <summary>
/// The reply to <c>media.token</c>: everything needed to enter the LiveKit room
/// for the room the user is in <em>right now</em>.
///
/// The token lasts fifteen minutes and is only needed at connect time, so it is
/// asked for on joining and never cached across rooms.
/// </summary>
public sealed class MediaToken
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>The LiveKit room name, which is exactly the TamizChat room id.</summary>
    [JsonPropertyName("room")]
    public string Room { get; set; } = "";

    /// <summary>The LiveKit participant identity, which is the client UUID.</summary>
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    // These exist so the client does not offer a button that cannot work. They
    // are not the enforcement: the token carries the same restrictions, so
    // ignoring them gets the publish rejected by LiveKit itself.
    [JsonPropertyName("can_speak")]
    public bool CanSpeak { get; set; }

    [JsonPropertyName("can_publish_video")]
    public bool CanPublishVideo { get; set; }

    [JsonPropertyName("can_share_screen")]
    public bool CanShareScreen { get; set; }
}

public sealed class MediaSetState
{
    [JsonPropertyName("mic")]
    public bool Mic { get; set; }

    [JsonPropertyName("cam")]
    public bool Cam { get; set; }

    [JsonPropertyName("screen")]
    public bool Screen { get; set; }

    /// <summary>Speakers off: they are hearing nobody.</summary>
    [JsonPropertyName("deaf")]
    public bool Deaf { get; set; }
}

/// <summary>
/// The broadcast form of the above: somebody's microphone or camera changed.
/// Leaving a room resets it, so no explicit "off" arrives on the way out.
/// </summary>
public sealed class MediaStateEvent
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("state")]
    public MediaSetState State { get; set; } = new();
}

/// <summary>`room.member_joined` / `room.member_left`.</summary>
public sealed class RoomMemberEvent
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("user")]
    public User User { get; set; } = new();

    /// <summary>
    /// Why they arrived or left: `switched_room`, `left`, `disconnected`,
    /// `moved_by_admin`, `room_deleted`. This is the only thing that separates a
    /// moderator moving somebody from that person walking out on their own.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>`room.left` — sent only to the person who left.</summary>
public sealed class RoomLeftEvent
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>`user.left` — a disconnection from the server, not from a room.</summary>
public sealed class UserLeftEvent
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>`user.kicked` / `user.banned` / `user.muted` / `user.unmuted`.</summary>
public sealed class SanctionEvent
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("by_uuid")]
    public string ByUuid { get; set; } = "";

    [JsonPropertyName("by_username")]
    public string ByUsername { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
}

/// <summary>A role as the server defines it.</summary>
public sealed class Role
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    /// <summary>
    /// How the client draws this role's tag, as opaque JSON.
    ///
    /// The server stores and echoes it without interpreting it, so a new visual
    /// option is a client change and nothing else.
    /// </summary>
    [JsonPropertyName("tag_style")]
    public string TagStyle { get; set; } = "";

    /// <summary>
    /// Lower numbers act on higher ones, never the reverse.
    ///
    /// The server enforces this and refuses anything that breaks it; the client
    /// uses it only to avoid offering an action that is certain to be rejected.
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }
}

/// <summary>`admin.kick` — and the shape `admin.unban`/`admin.unmute` also use.</summary>
public sealed class AdminTarget
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>`admin.ban` and `admin.mute`.</summary>
public sealed class AdminSanction
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>Seconds. Zero means permanent.</summary>
    [JsonPropertyName("duration_sec")]
    public long DurationSec { get; set; }
}

/// <summary>`admin.move`. An empty room means "out of every room".</summary>
public sealed class AdminMove
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";
}

/// <summary>`admin.role.grant` and `admin.role.revoke`.</summary>
public sealed class RoleAssignment
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("role_id")]
    public string RoleId { get; set; } = "";
}

/// <summary>`user.roles_changed` — the caller's own new permissions.</summary>
public sealed class RolesChanged
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];
}

/// <summary>`admin.role.create`, and `admin.role.update` with the id filled in.</summary>
public sealed class RoleSpec
{
    /// <summary>Empty when creating.</summary>
    [JsonPropertyName("role_id")]
    public string RoleId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("tag_style")]
    public string TagStyle { get; set; } = "";

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];
}

/// <summary>`admin.role.delete`.</summary>
public sealed class RoleDelete
{
    [JsonPropertyName("role_id")]
    public string RoleId { get; set; } = "";
}

/// <summary>One active ban or mute.</summary>
public sealed class Sanction
{
    [JsonPropertyName("client_uuid")]
    public string ClientUuid { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    /// <summary>`ban` or `mute`.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("by_username")]
    public string ByUsername { get; set; } = "";

    /// <summary>Unix seconds; zero means it never expires.</summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
}

/// <summary>The reply to `admin.sanctions`.</summary>
public sealed class SanctionList
{
    [JsonPropertyName("sanctions")]
    public List<Sanction> Sanctions { get; set; } = [];
}

/// <summary>`room.create`.</summary>
public sealed class RoomCreate
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    /// <summary>Zero means the server's default.</summary>
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("required_role_id")]
    public string RequiredRoleId { get; set; } = "";
}

/// <summary>
/// `room.update`. Only the fields sent are changed, so everything is nullable —
/// a room with no password and a room whose password is simply not being touched
/// have to be distinguishable.
/// </summary>
public sealed class RoomUpdate
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("capacity")]
    public int? Capacity { get; set; }

    [JsonPropertyName("required_role_id")]
    public string? RequiredRoleId { get; set; }
}

/// <summary>`room.delete`.</summary>
public sealed class RoomDelete
{
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";
}

/// <summary>A bot and what it is currently doing.</summary>
public sealed class Bot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "music";

    /// <summary>How the client draws the bot; the server never interprets it.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    /// <summary>`idle`, `playing`, or `stopped`.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("track")]
    public BotTrack? Track { get; set; }

    [JsonPropertyName("track_count")]
    public int TrackCount { get; set; }

    /// <summary>The playlist the bot plays from; empty means its own library.</summary>
    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; set; } = "";

    [JsonPropertyName("playlist_name")]
    public string PlaylistName { get; set; } = "";

    [JsonPropertyName("loop")]
    public bool Loop { get; set; }

    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class BotTrack
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}

/// <summary>The reply to `bot.list`.</summary>
public sealed class BotListReply
{
    [JsonPropertyName("bots")]
    public List<Bot> Bots { get; set; } = [];
}

/// <summary>`bot.control`.</summary>
public sealed class BotControl
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    /// <summary>`play`, `stop`, `next`, `prev`, `loop`, `shuffle`.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("track_index")]
    public int? TrackIndex { get; set; }
}

/// <summary>`bot.move`.</summary>
public sealed class BotMove
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";
}

/// <summary>`bot.create` and `bot.update`. A null field is left unchanged.</summary>
public sealed class BotSpec
{
    [JsonPropertyName("bot_id")]
    public string? BotId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("loop")]
    public bool? Loop { get; set; }

    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

/// <summary>`bot.delete`, and the `bot.removed` event.</summary>
public sealed class BotRef
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";
}

/// <summary>
/// `bot.queue` and `bot.playlist.list`. `bot.queue` also takes a playlist id,
/// to look inside a playlist the bot is not currently playing.
/// </summary>
public sealed class BotRequest
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; set; } = "";
}

/// <summary>The reply to `bot.queue`.</summary>
public sealed class BotQueueReply
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("tracks")]
    public List<BotTrack> Tracks { get; set; } = [];
}

/// <summary>A named group of tracks belonging to one bot.</summary>
public sealed class BotPlaylist
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("track_count")]
    public int TrackCount { get; set; }
}

/// <summary>The reply to `bot.playlist.list`.</summary>
public sealed class BotPlaylistList
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("playlists")]
    public List<BotPlaylist> Playlists { get; set; } = [];

    /// <summary>Which one plays; empty means the bot's own library.</summary>
    [JsonPropertyName("active_playlist_id")]
    public string Active { get; set; } = "";
}

/// <summary>Creates, renames, deletes or selects a playlist.</summary>
public sealed class BotPlaylistSpec
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>`bot.track.upload_request`.</summary>
public sealed class BotTrackUploadRequest
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Single-use permission to POST the bytes of one track.</summary>
public sealed class BotTrackUploadTicket
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("max_size")]
    public long MaxSize { get; set; }
}

/// <summary>`bot.track.delete` — a track by its position in the playlist.</summary>
public sealed class BotTrackRef
{
    [JsonPropertyName("bot_id")]
    public string BotId { get; set; } = "";

    [JsonPropertyName("playlist_id")]
    public string PlaylistId { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }
}
