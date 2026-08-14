using System.Text.Json.Serialization;
using TamizChat.Audio;

namespace TamizChat.Services;

/// <summary>
/// One entry on the soundboard.
///
/// A clip is either generated in code (<see cref="BuiltIn"/> set) or a file the
/// user added. Storing the built-ins as entries rather than as a hard-coded list
/// is what lets somebody delete the ones they do not like — the point of making
/// this editable at all.
/// </summary>
public sealed class SoundboardEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Set for a generated clip; null for a file.</summary>
    [JsonPropertyName("built_in")]
    public SoundEffect? BuiltIn { get; set; }

    /// <summary>File name inside the sounds folder. Set for a user's own clip.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }
}

/// <summary>
/// The soundboard's contents: which clips exist, in what order, and where their
/// audio comes from.
///
/// User files are **copied** into the app's own folder rather than referenced
/// where they were picked. Someone adds a clip from their Downloads folder and
/// empties it a week later; a soundboard that then falls silent would look like
/// our bug, not theirs.
/// </summary>
public sealed class SoundboardLibrary
{
    private readonly Dictionary<string, short[]> _decoded = [];

    public static SoundboardLibrary Instance { get; } = new();

    /// <summary>Raised when clips are added or removed, so the bar's menu can rebuild.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SoundboardEntry> Entries => SettingsStore.Current.Soundboard;

    /// <summary>Where user clips are kept, beside the settings file.</summary>
    public static string Folder
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TamizChat",
                "sounds");

            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Fills the list with the generated clips the first time the app runs.
    ///
    /// Only when it has never been set up — an empty list afterwards means the
    /// user deleted everything, and putting the defaults back would be the app
    /// arguing with them.
    /// </summary>
    public void EnsureDefaults()
    {
        var settings = SettingsStore.Current;
        if (settings.SoundboardInitialised)
        {
            return;
        }

        settings.Soundboard =
        [
            new() { Name = "Airhorn", BuiltIn = SoundEffect.Airhorn },
            new() { Name = "Applause", BuiltIn = SoundEffect.Applause },
            new() { Name = "Drum roll", BuiltIn = SoundEffect.DrumRoll },
            new() { Name = "Rimshot", BuiltIn = SoundEffect.Rimshot },
            new() { Name = "Crickets", BuiltIn = SoundEffect.Crickets },
        ];

        // The two extra clips that shipped with the notification sounds. They
        // are ordinary file entries, so they can be removed like any other.
        foreach (var (file, name) in new[] { ("clip-akh", "Akh"), ("clip-vadafak", "Vadafak") })
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", $"{file}.ogg");
            if (System.IO.File.Exists(bundled))
            {
                settings.Soundboard.Add(new SoundboardEntry { Name = name, File = $"bundled:{file}.ogg" });
            }
        }

        settings.SoundboardInitialised = true;
        SettingsStore.Save();
    }

    /// <summary>
    /// Copies a chosen file into the sounds folder and adds it to the list.
    ///
    /// The audio is decoded here, before anything is saved, so a file that
    /// cannot be read is rejected while the user is still looking at the picker
    /// rather than silently doing nothing when they press the button later.
    /// </summary>
    public SoundboardEntry Add(string sourcePath, string name)
    {
        var pcm = AudioClip.Load(sourcePath);
        if (pcm.Length == 0)
        {
            throw new InvalidOperationException("that file contains no audio");
        }

        var stored = $"{Guid.NewGuid():n}{Path.GetExtension(sourcePath)}";
        System.IO.File.Copy(sourcePath, Path.Combine(Folder, stored), overwrite: true);

        var entry = new SoundboardEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(),
            File = stored,
        };

        SettingsStore.Current.Soundboard.Add(entry);
        SettingsStore.Save();

        _decoded[entry.Id] = pcm;
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public void Remove(string id)
    {
        var entry = SettingsStore.Current.Soundboard.FirstOrDefault(e => e.Id == id);
        if (entry is null)
        {
            return;
        }

        SettingsStore.Current.Soundboard.Remove(entry);
        SettingsStore.Save();
        _decoded.Remove(id);

        // Only the user's own copy is deleted, never a bundled file — another
        // entry may point at the same one.
        if (entry.File is { } file && !file.StartsWith("bundled:", StringComparison.Ordinal))
        {
            try
            {
                System.IO.File.Delete(Path.Combine(Folder, file));
            }
            catch (Exception)
            {
                // A file already gone, or held open, is not worth reporting.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string id, string name)
    {
        var entry = SettingsStore.Current.Soundboard.FirstOrDefault(e => e.Id == id);
        if (entry is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        entry.Name = name.Trim();
        SettingsStore.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The audio for an entry, decoded on first use and kept.
    ///
    /// Returns empty if the file has gone missing — a soundboard button that
    /// does nothing is better than one that throws in the middle of a call.
    /// </summary>
    public short[] Pcm(SoundboardEntry entry)
    {
        if (_decoded.TryGetValue(entry.Id, out var cached))
        {
            return cached;
        }

        short[] pcm;

        try
        {
            if (entry.BuiltIn is { } kind)
            {
                pcm = BuiltInClips.Render(kind);
            }
            else if (entry.File is { } file)
            {
                var path = file.StartsWith("bundled:", StringComparison.Ordinal)
                    ? Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", file["bundled:".Length..])
                    : Path.Combine(Folder, file);

                pcm = System.IO.File.Exists(path) ? AudioClip.Load(path) : [];
            }
            else
            {
                pcm = [];
            }
        }
        catch (Exception)
        {
            pcm = [];
        }

        _decoded[entry.Id] = pcm;
        return pcm;
    }
}
