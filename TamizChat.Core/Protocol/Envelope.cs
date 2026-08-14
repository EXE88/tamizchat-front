using System.Text.Json;
using System.Text.Json.Serialization;

namespace TamizChat.Core.Protocol;

/// <summary>
/// The wire packet every frame is wrapped in: <c>{ "t": type, "id": …, "d": … }</c>.
///
/// <see cref="Id"/> is how a reply is matched to its request. Frames the server
/// originates — someone joining, a message arriving — carry no id.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("t")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("d")]
    public JsonElement? Data { get; set; }
}

/// <summary>Message type names, matching docs/PROTOCOL.md.</summary>
public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Rename = "rename";

    public const string RoomList = "room.list";
    public const string RoomJoin = "room.join";
    public const string RoomLeave = "room.leave";
    public const string RoomJoined = "room.joined";
    public const string RoomLeft = "room.left";
    public const string RoomMemberJoined = "room.member_joined";
    public const string RoomMemberLeft = "room.member_left";

    public const string ChatSend = "chat.send";
    public const string ChatMessage = "chat.message";
    public const string ChatHistory = "chat.history";
    public const string ChatTyping = "chat.typing";

    public const string UserJoined = "user.joined";
    public const string UserLeft = "user.left";
    public const string UserUpdated = "user.updated";

    public const string MediaSetState = "media.set_state";
    public const string MediaState = "media.state";

    public const string ServerNotice = "server.notice";
}

/// <summary>The body of an <c>error</c> frame.</summary>
public sealed class ErrorBody
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// Thrown when the server answers a request with an error frame. The
/// <see cref="Code"/> is a stable identifier; the message is for debugging and
/// should not be shown to the user as-is.
/// </summary>
public sealed class TamizChatProtocolException(string code, string message)
    : Exception($"{code}: {message}")
{
    public string Code { get; } = code;

    public string ServerMessage { get; } = message;
}
