using TamizChat.Audio;

namespace TamizChat.Services;

/// <summary>
/// The moments that get their own sound. The names match the files in
/// Assets/Sounds, so adding one is a file plus an enum entry.
/// </summary>
public enum AppSound
{
    YouJoinedServer,
    YouSwitchedRoom,
    YouWereMoved,
    YouWereKicked,
    YouWereBanned,
    UserJoinedYourRoom,
    UserLeftYourRoom,
    UserMovedToYourRoom,
    UserMovedOutOfYourRoom,
    UserLeftServer,
}

/// <summary>
/// Notification sounds, played on this machine only.
///
/// These never touch the microphone path — nobody else hears them, unlike the
/// soundboard. They are the TeamSpeak behaviour the project set out to copy: you
/// know somebody walked into your room without looking at the window.
///
/// Every clip is decoded once, on first use, and kept. They are small, there are
/// ten of them, and decoding an Opus file on the UI thread every time somebody
/// joins would be an obvious stutter.
/// </summary>
public sealed class EventSounds
{
    private static readonly Dictionary<AppSound, string> Files = new()
    {
        [AppSound.YouJoinedServer] = "you-joined-server",
        [AppSound.YouSwitchedRoom] = "you-switched-room",
        [AppSound.YouWereMoved] = "you-were-moved",
        [AppSound.YouWereKicked] = "you-were-kicked",
        [AppSound.YouWereBanned] = "you-were-banned",
        [AppSound.UserJoinedYourRoom] = "user-joined-your-room",
        [AppSound.UserLeftYourRoom] = "user-left-your-room",
        [AppSound.UserMovedToYourRoom] = "user-moved-to-your-room",
        [AppSound.UserMovedOutOfYourRoom] = "user-moved-out-of-your-room",
        [AppSound.UserLeftServer] = "user-left-server",
    };

    private readonly Dictionary<AppSound, short[]> _clips = [];
    private readonly SpeakerPlayback _output = new();
    private readonly object _gate = new();

    public static EventSounds Instance { get; } = new();

    /// <summary>Turned off wholesale from Settings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>0 to 1, applied on top of whatever the clip was recorded at.</summary>
    public double Volume { get; set; } = 1.0;

    public void Play(AppSound sound)
    {
        if (!Enabled)
        {
            return;
        }

        // TAMIZCHAT_SOUNDLOG=1 records which cues fired, because "did the right
        // sound play" is otherwise only answerable by a human with speakers.
        if (Environment.GetEnvironmentVariable("TAMIZCHAT_SOUNDLOG") == "1")
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "soundlog.txt"),
                    $"{DateTime.Now:HH:mm:ss} {sound}\n");
            }
            catch (Exception)
            {
                // Diagnostics must never break the thing being diagnosed.
            }
        }

        short[] clip;

        lock (_gate)
        {
            if (!_clips.TryGetValue(sound, out var cached))
            {
                cached = Load(sound);
                _clips[sound] = cached;
            }

            clip = cached;
        }

        if (clip.Length == 0)
        {
            return;
        }

        // Its own playback device, separate from the voice mixer, so a
        // notification is not silenced by deafening yourself — being deafened
        // means not hearing *people*, and these are the app talking to you.
        _output.Start();

        var scaled = clip;
        if (Volume < 0.999)
        {
            scaled = new short[clip.Length];
            for (var i = 0; i < clip.Length; i++)
            {
                scaled[i] = (short)(clip[i] * Volume);
            }
        }

        // PlayClip, not Submit: Submit is the jitter-buffered path for live
        // voice and caps at half a second, which chopped the front off every
        // notification. One-shots mix alongside it and always play whole.
        _output.PlayClip(scaled);
    }

    private static short[] Load(AppSound sound)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", $"{Files[sound]}.ogg");
            return File.Exists(path) ? AudioClip.Load(path) : [];
        }
        catch (Exception)
        {
            // A missing or unreadable notification sound is not worth an error
            // in the user's face; the app simply stays quiet.
            return [];
        }
    }
}
