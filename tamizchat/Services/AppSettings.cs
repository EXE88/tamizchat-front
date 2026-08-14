using System.Text.Json;
using System.Text.Json.Serialization;
using TamizChat.Theming;

namespace TamizChat.Services;

/// <summary>
/// Everything the app remembers between runs.
/// </summary>
public sealed class AppSettings
{
    public ThemeFamily Theme { get; set; } = ThemeFamily.SaltAndPepper;

    public AppThemeMode Mode { get; set; } = AppThemeMode.System;

    /// <summary>Acrylic, per the design. Stored under its user-facing name.</summary>
    public AppBackdrop Backdrop { get; set; } = AppBackdrop.Glass;

    /// <summary>BCP-47 tag. English is the default; "fa" is the other option.</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// This installation's identity, generated once and kept forever. It is the
    /// only thing a server knows the user by — there is no account and no login —
    /// so losing it means becoming a stranger to every server.
    /// </summary>
    public string ClientUuid { get; set; } = "";

    /// <summary>The display name used when a server entry does not override it.</summary>
    public string Username { get; set; } = "";
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under LOCALAPPDATA.
///
/// The app is unpackaged, so <c>ApplicationData.Current</c> is unavailable — it
/// throws without a package identity. A plain file is also easier to inspect and
/// to delete when something goes wrong.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lock Gate = new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TamizChat");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>The live settings object. Mutate it, then call <see cref="Save"/>.</summary>
    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable settings file must not stop the app from
            // starting; the defaults are always a usable configuration.
            Current = new AppSettings();
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(Current.ClientUuid))
        {
            Current.ClientUuid = Guid.NewGuid().ToString();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(Current.Username))
        {
            var name = Environment.UserName?.Trim();
            Current.Username = string.IsNullOrWhiteSpace(name) ? "User" : name;
            changed = true;
        }

        if (changed)
        {
            Save();
        }

        return Current;
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);

                // Write beside the real file and swap, so an interrupted write
                // cannot leave a half-written settings file behind.
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Losing a preference is not worth crashing over.
        }
    }
}
