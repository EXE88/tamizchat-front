using TamizChat.Audio;

namespace TamizChat.Services;

/// <summary>
/// The moments that get their own sound. The names match the files in
/// Assets/Sounds, so adding one is a file plus an enum entry.
///
/// Every one of these is about **you** or about **your own room**. There is
/// deliberately no cue for somebody leaving the server: it fires for people in
/// rooms you cannot see, and on a busy server that is a chime every few seconds
/// for strangers. `user-left-server.ogg` is still in the assets folder, unused,
/// so the decision can be reversed without re-recording anything.
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
}

/// <summary>
/// Notification sounds, played on this machine only.
///
/// These never touch the microphone path — nobody else hears them, unlike the
/// soundboard. They are the TeamSpeak behaviour the project set out to copy: you
/// know somebody walked into your room without looking at the window.
///
/// "Nobody else hears them" is only true because they go through the voice
/// mixer, which the echo canceller has a reference for. See <see cref="Play"/>.
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

        var scaled = clip;
        if (Volume < 0.999)
        {
            scaled = new short[clip.Length];
            for (var i = 0; i < clip.Length; i++)
            {
                scaled[i] = (short)(clip[i] * Volume);
            }
        }

        // Through the call's own mixer whenever there is one.
        //
        // This used to be a second playback device of its own, which sounded
        // identical and was not: audio played on a device the echo canceller
        // knows nothing about is picked up by the microphone and sent to the
        // room, so everybody heard everybody else's join and leave chimes on top
        // of their own. The mixer is also where per-person volume and the
        // canceller's reference live, so a notification belongs in it.
        //
        // PlayClip, not Submit: Submit is the jitter-buffered path for live
        // voice and caps at half a second, which chopped the front off every
        // notification. One-shots mix alongside it and always play whole.
        if (VoiceService.Instance.PlayNotification(scaled))
        {
            return;
        }

        // No call, or deafened, so nothing else is using the speakers: its own
        // output, so a notification still arrives. Being deafened means not
        // hearing *people*, and these are the app talking to you.
        _output.Start();
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
