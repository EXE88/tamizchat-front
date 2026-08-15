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

    // --- moderation ---

    public const string AdminKick = "admin.kick";
    public const string AdminBan = "admin.ban";
    public const string AdminUnban = "admin.unban";
    public const string AdminMute = "admin.mute";
    public const string AdminUnmute = "admin.unmute";
    public const string AdminMove = "admin.move";
    public const string AdminSanctions = "admin.sanctions";

    public const string RoleList = "admin.role.list";
    public const string RoleGrant = "admin.role.grant";
    public const string RoleRevoke = "admin.role.revoke";
    public const string RoleCreate = "admin.role.create";
    public const string RoleUpdate = "admin.role.update";
    public const string RoleDelete = "admin.role.delete";

    public const string RoomCreate = "room.create";
    public const string RoomUpdate = "room.update";
    public const string RoomDelete = "room.delete";

    public const string BotList = "bot.list";
    public const string BotControl = "bot.control";
    public const string BotMove = "bot.move";
    public const string BotCreate = "bot.create";
    public const string BotUpdate = "bot.update";
    public const string BotDelete = "bot.delete";
    public const string BotQueue = "bot.queue";

    /// <summary>One bot changed, or appeared: an unknown id means "add it".</summary>
    public const string BotState = "bot.state";
    public const string BotRemoved = "bot.removed";

    public const string BotPlaylistList = "bot.playlist.list";
    public const string BotPlaylistCreate = "bot.playlist.create";
    public const string BotPlaylistRename = "bot.playlist.rename";
    public const string BotPlaylistDelete = "bot.playlist.delete";
    public const string BotPlaylistSelect = "bot.playlist.select";

    public const string BotTrackUploadRequest = "bot.track.upload_request";
    public const string BotTrackUploadTicket = "bot.track.upload_ticket";
    public const string BotTrackDelete = "bot.track.delete";

    /// <summary>Sent only to the user whose roles changed.</summary>
    public const string UserRolesChanged = "user.roles_changed";

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

    public const string FileUploadRequest = "file.upload_request";
    public const string FileUploadTicket = "file.upload_ticket";
    public const string FileDownloadToken = "file.download_token";
    public const string FileDownload = "file.download";

    // The same names are used in both directions: a paint.begin sent is a
    // request, a paint.begin received is somebody else drawing.
    public const string PaintBegin = "paint.begin";
    public const string PaintAppend = "paint.append";
    public const string PaintEnd = "paint.end";
    public const string PaintUndo = "paint.undo";
    public const string PaintClear = "paint.clear";
    public const string PaintState = "paint.state";

    public const string MediaToken = "media.token";
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
