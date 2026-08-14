using System.Text.Json;
using System.Text.Json.Serialization;
using TamizChat.Theming;

using TamizChat.Overlays;

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

    /// <summary>The soundboard's contents, in the order the menu lists them.</summary>
    public List<SoundboardEntry> Soundboard { get; set; } = [];

    /// <summary>
    /// Whether the built-in clips have ever been put in place.
    ///
    /// Separate from the list being empty, because an empty list is a valid
    /// choice — somebody who deleted every clip should not have them all
    /// reappear on the next launch.
    /// </summary>
    public bool SoundboardInitialised { get; set; }

    /// <summary>Notification sounds: on/off and how loud, 0 to 1.</summary>
    public bool EventSoundsEnabled { get; set; } = true;

    public double EventSoundsVolume { get; set; } = 1.0;

    // --- overlays ---

    public bool MembersOverlayEnabled { get; set; }

    public OverlayCorner MembersOverlayCorner { get; set; } = OverlayCorner.TopRight;

    public double MembersOverlayOpacity { get; set; } = 0.9;

    public bool MessagesOverlayEnabled { get; set; }

    public OverlayCorner MessagesOverlayCorner { get; set; } = OverlayCorner.TopLeft;

    public double MessagesOverlayOpacity { get; set; } = 0.9;

    /// <summary>How many message cards may be on screen at once.</summary>
    public int MessagesOverlayMax { get; set; } = 4;

    /// <summary>How long a card stays before it fades.</summary>
    public double MessagesOverlayFadeSeconds { get; set; } = 6;

    /// <summary>The send-only chat strip above the bottom bar.</summary>
    public bool InlineChatEnabled { get; set; }

    /// <summary>Per-person playback volume, by client UUID. 1 is unchanged.</summary>
    public Dictionary<string, double> UserVolumes { get; set; } = [];

    /// <summary>Empty means "follow the Windows default".</summary>
    public string InputDeviceId { get; set; } = "";

    public string OutputDeviceId { get; set; } = "";

    /// <summary>Gain applied to your own microphone before anyone hears it.</summary>
    public double MicGain { get; set; } = 1.0;

    /// <summary>
    /// Action name to virtual-key code. Stored by name rather than by enum
    /// value so adding an action later cannot silently rebind an existing key.
    /// </summary>
    public Dictionary<string, int> HotKeys { get; set; } = [];

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
