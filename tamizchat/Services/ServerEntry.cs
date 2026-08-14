using System.Text.Json;

namespace TamizChat.Services;

/// <summary>A server the user has saved.</summary>
public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The user's own label for it, which may differ from the server's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Host and port, without a scheme — "localhost:8080".</summary>
    public string Address { get; set; } = "";

    /// <summary>The name the user connects under. Empty means fall back to the global one.</summary>
    public string Username { get; set; } = "";

    public bool UseTls { get; set; }

    public string HttpUrl => $"{(UseTls ? "https" : "http")}://{Address}";

    public string WebSocketUrl => $"{(UseTls ? "wss" : "ws")}://{Address}/ws";
}

/// <summary>
/// The saved server list, kept in its own file next to settings.json.
///
/// Separate from <see cref="AppSettings"/> because it grows and changes on a
/// different rhythm — losing a theme preference is trivial, losing the server
/// list is not.
/// </summary>
public static class ServerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.Directory, "servers.json");

    public static List<ServerEntry> Servers { get; private set; } = [];

    public static event EventHandler? Changed;

    public static List<ServerEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Servers = JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(FilePath), Options) ?? [];
            }
        }
        catch (Exception)
        {
            Servers = [];
        }

        // First run gets the development backend, so there is something to look
        // at before the user has added anything.
        if (Servers.Count == 0)
        {
            Servers.Add(new ServerEntry { Name = "Development server", Address = "localhost:8080" });
            Save();
        }

        return Servers;
    }

    public static void Add(ServerEntry entry)
    {
        Servers.Add(entry);
        Save();
    }

    public static void Remove(ServerEntry entry)
    {
        Servers.RemoveAll(s => s.Id == entry.Id);
        Save();
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Directory);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Servers, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Not worth crashing over; the list is still correct in memory.
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
