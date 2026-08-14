using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TamizChat.Core;

/// <summary>What <c>GET /api/v1/server-info</c> reports about a server.</summary>
public sealed class ServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("server_uuid")]
    public string ServerUuid { get; set; } = "";

    [JsonPropertyName("software_version")]
    public string SoftwareVersion { get; set; } = "";

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("online_users")]
    public int OnlineUsers { get; set; }

    [JsonPropertyName("max_users")]
    public int MaxUsers { get; set; }

    [JsonPropertyName("password_required")]
    public bool PasswordRequired { get; set; }

    [JsonPropertyName("media_enabled")]
    public bool MediaEnabled { get; set; }

    [JsonPropertyName("uploads_enabled")]
    public bool UploadsEnabled { get; set; }

    [JsonPropertyName("welcome")]
    public string Welcome { get; set; } = "";
}

/// <summary>
/// Asks a server who it is without connecting to it.
///
/// This is the cheap read behind the server list: it needs no session, no
/// username and no WebSocket, so a list of a dozen servers can be refreshed
/// without opening a dozen connections.
/// </summary>
public static class ServerProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Returns the server's details, or null when it cannot be reached. A server
    /// being down is an ordinary state for this list, not an error worth throwing
    /// over.
    /// </summary>
    public static async Task<ServerInfo?> TryGetAsync(string httpUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Http
                .GetFromJsonAsync<ServerInfo>($"{httpUrl.TrimEnd('/')}/api/v1/server-info", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
