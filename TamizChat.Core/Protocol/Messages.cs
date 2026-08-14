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

    /// <summary>The circle avatar's letter. Users have no profile pictures.</summary>
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

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("limits")]
    public Limits Limits { get; set; } = new();
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

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
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

public sealed class MediaSetState
{
    [JsonPropertyName("mic")]
    public bool Mic { get; set; }

    [JsonPropertyName("cam")]
    public bool Cam { get; set; }

    [JsonPropertyName("screen")]
    public bool Screen { get; set; }
}
