using Microsoft.UI.Dispatching;
using TamizChat.Audio;
using TamizChat.Core.Media;
using TamizChat.Video;

namespace TamizChat.Services;

/// <summary>
/// Voice for the room the user is in: the one place that owns the microphone,
/// the speakers and the LiveKit connection, and keeps them in step with
/// <see cref="ServerSession"/>.
///
/// It mirrors ServerSession's shape on purpose — one instance, events raised on
/// the UI thread, pages read state and never touch <see cref="MediaSession"/>
/// themselves.
/// </summary>
public sealed class VoiceService
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly MicrophoneCapture _microphone = new();

    /// <summary>Serializes joining and leaving; see JoinAsync for why.</summary>
    private readonly SemaphoreSlim _joining = new(1, 1);
    private readonly SpeakerPlayback _speakers = new();

    private MediaSession? _session;
    private string _roomId = "";

    /// <summary>
    /// The echo canceller, alive only while there is a call to cancel.
    ///
    /// Null is a working state: on a machine whose native library predates the
    /// audio processing module, voice runs exactly as it did before.
    /// </summary>
    private AudioProcessor? _processor;

    public static VoiceService Instance { get; } = new();

    private bool _following;

    /// <summary>
    /// Ties voice to the session for the whole life of the app.
    ///
    /// It used to be the room page that did this, and only while that page was
    /// on screen. Anybody who left a room — or lost the server entirely — from
    /// the chat page, the paint board or the admin panel kept a live LiveKit
    /// connection with an open microphone: still heard by a room they were no
    /// longer in, still counted as present, with nothing on their own screen to
    /// suggest it. That is the "he disconnected and we could still hear him"
    /// report, and it can only be fixed somewhere that does not come and go.
    /// </summary>
    public void Follow()
    {
        if (_following)
        {
            return;
        }

        _following = true;

        ServerSession.Instance.Changed += (_, _) => _ = SyncToSessionAsync();
        ServerSession.Instance.Dropped += (_, _) => _ = LeaveAsync();
    }

    /// <summary>Joins, leaves or moves so that voice matches the room the session says we are in.</summary>
    private async Task SyncToSessionAsync()
    {
        try
        {
            if (!ServerSession.Instance.IsConnected
                || string.IsNullOrEmpty(ServerSession.Instance.MyRoomId))
            {
                await LeaveAsync().ConfigureAwait(true);
                return;
            }

            await JoinAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Voice failing must never take the rest of the session with it.
        }
    }

    /// <summary>Raised on the UI thread when connection, mute or speaker state changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Who LiveKit says is talking right now, by client UUID.</summary>
    public IReadOnlyList<string> Speakers { get; private set; } = [];

    public bool IsConnected => _session?.IsConnected == true;

    public bool IsMuted { get; private set; } = true;

    /// <summary>Deafened: still connected and subscribed, just not playing anything.</summary>
    public bool IsDeafened { get; private set; }

    /// <summary>False when the server's token withholds the speak permission.</summary>
    public bool CanSpeak { get; private set; }

    /// <summary>The microphone's current peak, for a level meter.</summary>
    public float MicLevel => _microphone.Level;

    /// <summary>
    /// Reopens the audio devices, for when the choice changes mid-call.
    ///
    /// Stopping and starting is the only way: WASAPI binds a client to one
    /// endpoint when it is opened, so a device cannot be swapped underneath it.
    /// </summary>
    public void ReopenDevices()
    {
        if (!IsConnected)
        {
            return;
        }

        if (_speakers.IsRunning)
        {
            _speakers.Stop();
            _speakers.Start(AudioDevices.Resolve(SettingsStore.Current.OutputDeviceId, input: false));

            foreach (var (uuid, volume) in SettingsStore.Current.UserVolumes)
            {
                _speakers.SetGain(uuid, volume);
            }
        }

        if (_microphone.IsRunning)
        {
            _microphone.Stop();
            try
            {
                _microphone.Start(AudioDevices.Resolve(SettingsStore.Current.InputDeviceId, input: true));
            }
            catch (Exception)
            {
                // The chosen microphone has gone; stay quiet rather than crash.
            }
        }
    }

    /// <summary>
    /// Joins the voice side of the room the session is already in.
    ///
    /// The microphone goes live straight away when the user is allowed to speak,
    /// because the bottom bar declares Mic as on by default and the two must not
    /// disagree — an icon claiming you are live while you are not is worse than
    /// either state on its own. Walking into a room is walking into a
    /// conversation; TeamSpeak and Discord both behave this way.
    /// </summary>
    public async Task JoinAsync()
    {
        // One join at a time.
        //
        // This is driven by every change to the room tree, which now includes
        // somebody else opening their microphone — so several calls can be in
        // flight at once. Without the gate a second call would arrive while the
        // first was still connecting, see IsConnected as false because _session
        // is only assigned at the end, tear down the half-built session and
        // start again. What that looked like was a microphone that appeared open
        // in the bottom bar while the room saw it closed, and audio that stopped
        // going out until it was toggled by hand.
        await _joining.WaitAsync().ConfigureAwait(true);

        try
        {
            await JoinCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _joining.Release();
        }
    }

    private async Task JoinCoreAsync()
    {
        var session = ServerSession.Instance;
        if (string.IsNullOrEmpty(session.MyRoomId))
        {
            return;
        }

        if (_roomId == session.MyRoomId && IsConnected)
        {
            return;
        }

        await LeaveCoreAsync().ConfigureAwait(true);

        var credentials = await session.GetMediaTokenAsync().ConfigureAwait(true);
        if (credentials is null || string.IsNullOrEmpty(credentials.Token))
        {
            // Voice is off on this server, or LiveKit is not configured. Chat
            // carries on regardless — media must never take the room down.
            return;
        }

        var media = new MediaSession();
        media.FrameReceived += OnFrameReceived;
        media.SpeakersChanged += OnSpeakersChanged;
        media.Closed += OnMediaClosed;
        media.VideoFrameReceived += OnVideoFrameReceived;
        media.VideoTrackEnded += OnVideoTrackEnded;

        await media.ConnectAsync(credentials).ConfigureAwait(true);

        _session = media;
        _roomId = credentials.Room;
        CanSpeak = credentials.CanSpeak;
        CanPublishVideo = credentials.CanPublishVideo;
        CanShareScreen = credentials.CanShareScreen;
        IsMuted = true;

        StartProcessor();

        _speakers.Start(AudioDevices.Resolve(SettingsStore.Current.OutputDeviceId, input: false));

        // Re-apply whatever this listener had set for the people already here.
        foreach (var (uuid, volume) in SettingsStore.Current.UserVolumes)
        {
            _speakers.SetGain(uuid, volume);
        }

        _processor?.SetStreamDelay(_speakers.LatencyMilliseconds + CaptureDelayMs);
        StartSpeakingWatch();
        Raise();

        if (CanSpeak)
        {
            await SetMutedAsync(false).ConfigureAwait(true);
        }
        else
        {
            // Still told, even when nothing is switched on. Otherwise the room
            // keeps whatever it last heard about this user, and somebody who may
            // not speak at all would be drawn however they were before.
            await ReportStateAsync().ConfigureAwait(true);
        }

        // Dev-only, alongside TAMIZCHAT_AUTOJOIN: turns the camera or screen on
        // at startup, which is how the publishing side gets tested without a
        // human clicking the bar.
        switch (Environment.GetEnvironmentVariable("TAMIZCHAT_AUTOSHARE"))
        {
            case "screen":
                // No dialog on this path: it exists so the publishing side can
                // be tested without a human, so it takes the first target.
                await SetScreenShareAsync(true, ShareTargets.List().FirstOrDefault())
                    .ConfigureAwait(true);
                break;
            case "camera":
                await SetCameraAsync(true).ConfigureAwait(true);
                break;
        }

        // Same idea for the two menus. A flyout cannot be driven from a script:
        // taking a screenshot steals focus, which dismisses it.
        if (Environment.GetEnvironmentVariable("TAMIZCHAT_AUTOVOICE") is { } preset
            && Enum.TryParse<VoiceEffectKind>(preset, true, out var kind))
        {
            SetVoiceEffect(kind);
        }

        if (Environment.GetEnvironmentVariable("TAMIZCHAT_AUTOEFFECT") is { } name)
        {
            var entry = SoundboardLibrary.Instance.Entries
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                await PlayEffectAsync(entry).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Roughly how long the microphone's own buffering adds on top of the
    /// output's latency. The canceller only wants the right order of magnitude
    /// to start from; it measures the rest itself.
    /// </summary>
    private const int CaptureDelayMs = 20;

    /// <summary>
    /// Brings up the echo canceller and points the speaker mix at it.
    ///
    /// Both halves are needed and neither does anything alone: the render tap
    /// teaches it what is coming out of the loudspeaker, and the capture call in
    /// <see cref="OnFrameReady"/> is where that is subtracted from what the
    /// microphone heard. Without it, everybody on speakers sends the whole room
    /// back to the room — which is why one person joining could be heard twice,
    /// and why a music bot's track arrived doubled.
    /// </summary>
    private void StartProcessor()
    {
        StopProcessor();

        if (!SettingsStore.Current.EchoCancellation)
        {
            return;
        }

        _processor = AudioProcessor.TryCreate(
            echoCancellation: true,
            noiseSuppression: SettingsStore.Current.NoiseSuppression,
            highPassFilter: true,
            gainControl: false);

        if (_processor is not null)
        {
            _speakers.RenderTap = OnRendered;
        }
    }

    private void StopProcessor()
    {
        _speakers.RenderTap = null;
        _processor?.Dispose();
        _processor = null;
    }

    /// <summary>
    /// One 10 ms frame of exactly what the speakers are playing, on the playback
    /// thread. It is handed straight over; the processor does its own locking.
    /// </summary>
    private void OnRendered(short[] frame) => _processor?.ProcessRender(frame);

    /// <summary>
    /// Reopens the canceller, for when it is switched on or off in Settings
    /// while a call is already up.
    /// </summary>
    public void ReloadProcessing()
    {
        if (!IsConnected)
        {
            return;
        }

        StartProcessor();
        _processor?.SetStreamDelay(_speakers.LatencyMilliseconds + CaptureDelayMs);
    }

    /// <summary>
    /// Deafen: stop hearing anyone. Distinct from muting, and it does not touch
    /// the LiveKit subscription — the audio still arrives, it just is not played,
    /// so undeafening is instant rather than a renegotiation.
    /// </summary>
    public void SetDeafened(bool deafened)
    {
        if (deafened == IsDeafened)
        {
            return;
        }

        IsDeafened = deafened;

        if (deafened)
        {
            _speakers.Stop();
        }
        else if (IsConnected)
        {
            // With the chosen device, not the system default. Undeafening used
            // to reopen whatever Windows felt like, so somebody who had picked
            // their headset found themselves back on the monitor speakers.
            _speakers.Start(AudioDevices.Resolve(SettingsStore.Current.OutputDeviceId, input: false));

            foreach (var (uuid, volume) in SettingsStore.Current.UserVolumes)
            {
                _speakers.SetGain(uuid, volume);
            }
        }

        // The room is told, the same way it is told about the microphone: a
        // person whose speakers are off cannot hear anyone talking to them, and
        // that is worth seeing before somebody starts talking.
        _ = ReportStateAsync();
        Raise();
    }

    public async Task LeaveAsync()
    {
        await _joining.WaitAsync().ConfigureAwait(true);

        try
        {
            await LeaveCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _joining.Release();
        }
    }

    /// <summary>
    /// Leaves without taking the gate, for callers that already hold it.
    /// Taking it twice on the same thread would deadlock.
    /// </summary>
    private async Task LeaveCoreAsync()
    {
        var media = _session;
        _session = null;
        _roomId = "";
        Speakers = [];
        CanSpeak = false;
        CanPublishVideo = false;
        CanShareScreen = false;
        IsMuted = true;
        IsCameraOn = false;
        IsScreenSharing = false;

        StopSpeakingWatch();
        _microphone.Stop();
        _microphone.FrameReady -= OnFrameReady;
        _speakers.Stop();
        StopProcessor();
        _mutedBeforeClip = null;
        Soundboard.Stop();

        _camera.FrameReady -= OnCameraFrame;
        await _camera.StopAsync().ConfigureAwait(true);
        _screen.FrameReady -= OnScreenFrame;
        _screen.Stop();

        if (media is not null)
        {
            media.FrameReceived -= OnFrameReceived;
            media.SpeakersChanged -= OnSpeakersChanged;
            media.Closed -= OnMediaClosed;
            media.VideoFrameReceived -= OnVideoFrameReceived;
            media.VideoTrackEnded -= OnVideoTrackEnded;
            await media.DisposeAsync().ConfigureAwait(true);
        }

        Raise();
    }

    /// <summary>
    /// Turns the microphone on and off.
    ///
    /// The track is published on first unmute and then kept, because publishing
    /// is a round trip and doing it on every toggle would clip the first word.
    /// Muting stops the frames instead.
    /// </summary>
    public async Task SetMutedAsync(bool muted)
    {
        if (_session is null || (!CanSpeak && !muted))
        {
            return;
        }

        if (!muted)
        {
            await _session.StartPublishingAsync().ConfigureAwait(true);

            if (!_microphone.IsRunning)
            {
                _microphone.FrameReady += OnFrameReady;
                try
                {
                    _microphone.Start(AudioDevices.Resolve(SettingsStore.Current.InputDeviceId, input: true));
                }
                catch (Exception)
                {
                    // No microphone, or one the user has blocked. Stay muted
                    // rather than pretending to be live.
                    _microphone.FrameReady -= OnFrameReady;
                    return;
                }
            }
        }

        IsMuted = muted;
        _session.SetMuted(muted);

        // Tell everyone else, so the room tree can show the microphone icon.
        await ReportStateAsync().ConfigureAwait(true);
        Raise();
    }

    public Task ToggleMuteAsync() => SetMutedAsync(!IsMuted);

    /// <summary>
    /// How loud one other person is, for this listener only.
    ///
    /// Kept in settings by client UUID rather than by name, so turning somebody
    /// down survives them renaming themselves — and survives a restart, which is
    /// the point of bothering to store it at all.
    /// </summary>
    public double GetVolume(string clientUuid) =>
        SettingsStore.Current.UserVolumes.TryGetValue(clientUuid, out var v) ? v : 1.0;

    public void SetVolume(string clientUuid, double volume)
    {
        SettingsStore.Current.UserVolumes[clientUuid] = volume;
        SettingsStore.Save();
        _speakers.SetGain(clientUuid, volume);
    }

    // --- camera and screen ---

    private readonly CameraCapture _camera = new();
    private readonly ScreenCapture _screen = new();

    /// <summary>Raised for each frame of somebody else's camera or screen.</summary>
    public event EventHandler<RemoteVideoFrame>? VideoFrameReceived;

    /// <summary>Raised when a remote video track stops, so its tile can go back to the avatar.</summary>
    public event EventHandler<RemoteVideoFrame>? VideoTrackEnded;

    public bool IsCameraOn { get; private set; }

    public bool IsScreenSharing { get; private set; }

    public bool CanPublishVideo { get; private set; }

    public bool CanShareScreen { get; private set; }

    public async Task SetCameraAsync(bool on)
    {
        if (_session is null || (on && !CanPublishVideo) || on == IsCameraOn)
        {
            return;
        }

        if (!on)
        {
            await _camera.StopAsync().ConfigureAwait(true);
            _camera.FrameReady -= OnCameraFrame;
            await _session.StopVideoAsync(VideoKind.Camera).ConfigureAwait(true);
            IsCameraOn = false;
            RaiseLocalEnded(VideoKind.Camera);
            await ReportStateAsync().ConfigureAwait(true);
            return;
        }

        // Opened before publishing, because only the camera itself knows what
        // size it will hand over, and the track is published at that size.
        await _camera.StartAsync().ConfigureAwait(true);
        await _session.StartVideoAsync(VideoKind.Camera, _camera.Width, _camera.Height).ConfigureAwait(true);
        _camera.FrameReady += OnCameraFrame;

        IsCameraOn = true;
        await ReportStateAsync().ConfigureAwait(true);
    }

    public async Task SetScreenShareAsync(bool on, ShareTarget? target = null)
    {
        if (_session is null || (on && !CanShareScreen) || on == IsScreenSharing)
        {
            return;
        }

        if (!on)
        {
            _screen.FrameReady -= OnScreenFrame;
            _screen.Stop();
            await _session.StopVideoAsync(VideoKind.Screen).ConfigureAwait(true);
            IsScreenSharing = false;
            RaiseLocalEnded(VideoKind.Screen);
            await ReportStateAsync().ConfigureAwait(true);
            return;
        }

        // Read at the moment of sharing rather than held, so changing the
        // quality in Settings takes effect on the next share without a restart.
        _screen.MaxHeight = SettingsStore.Current.ScreenShareMaxHeight;
        _screen.DrawCursor = SettingsStore.Current.ScreenShareCursor;

        _screen.Start(
            target ?? throw new InvalidOperationException("nothing was chosen to share"),
            Math.Clamp(SettingsStore.Current.ScreenShareFps, 1, 60));
        await _session.StartVideoAsync(VideoKind.Screen, _screen.Width, _screen.Height).ConfigureAwait(true);
        _screen.FrameReady += OnScreenFrame;

        IsScreenSharing = true;
        await ReportStateAsync().ConfigureAwait(true);
    }

    /// <summary>Whether this machine has a camera, so the toggle can be hidden if not.</summary>
    public static Task<bool> HasCameraAsync() => CameraCapture.IsAvailableAsync();

    private void OnCameraFrame(object? sender, VideoFrameBuffer frame)
    {
        _session?.SendVideoFrame(VideoKind.Camera, frame.Bgra, frame.Width, frame.Height);
        RaiseLocal(VideoKind.Camera, frame);
    }

    private void OnScreenFrame(object? sender, VideoFrameBuffer frame)
    {
        _session?.SendVideoFrame(VideoKind.Screen, frame.Bgra, frame.Width, frame.Height);
        RaiseLocal(VideoKind.Screen, frame);
    }

    /// <summary>
    /// Shows the user their own camera or screen in their own cell.
    ///
    /// Without this, turning screen sharing on looks like nothing happening:
    /// LiveKit does not loop a published track back to its publisher, so the one
    /// person who cannot see the share is the person sharing. That reads as a
    /// broken button.
    /// </summary>
    private void RaiseLocal(VideoKind kind, VideoFrameBuffer frame)
    {
        var identity = ServerSession.Instance.MyUuid;
        if (string.IsNullOrEmpty(identity))
        {
            return;
        }

        _ui.TryEnqueue(() => VideoFrameReceived?.Invoke(
            this,
            new RemoteVideoFrame(identity, kind, frame.Width, frame.Height, frame.Bgra)));
    }

    private void RaiseLocalEnded(VideoKind kind)
    {
        var identity = ServerSession.Instance.MyUuid;
        if (!string.IsNullOrEmpty(identity))
        {
            VideoTrackEnded?.Invoke(this, new RemoteVideoFrame(identity, kind, 0, 0, []));
        }
    }

    /// <summary>
    /// Tells the room what this user has switched on.
    ///
    /// The whole state goes every time rather than the one thing that changed:
    /// there is a single message for it, and sending all of it is how the icons
    /// above somebody's name can never disagree with each other. Deafened is in
    /// here too — a person who has turned their speakers off cannot hear anyone
    /// talking to them, and the room deserves to know before somebody tries.
    /// </summary>
    private Task ReportStateAsync()
    {
        Raise();
        return ServerSession.Instance.SetMediaStateAsync(
            !IsMuted, IsCameraOn, IsScreenSharing, IsDeafened);
    }

    /// <summary>
    /// The outgoing audio pipeline, in the order it has to happen.
    ///
    /// The voice changer runs first, on the voice alone, then the soundboard is
    /// mixed on top. The other order would put the airhorn through the pitch
    /// shifter as well, so switching to Chipmunk would also change what the
    /// clips sound like — the clips are meant to be fixed, recognisable sounds.
    /// </summary>
    private void OnFrameReady(object? sender, short[] pcm)
    {
        // The echo canceller comes first, on the rawest signal there is.
        //
        // It has to see the microphone as the microphone heard it: any gain,
        // pitch shifting or mixed-in clip applied beforehand is a signal that
        // was never in the room, and it would be trying to match its reference
        // against something the loudspeaker never produced.
        _processor?.ProcessCapture(pcm);

        // Your own level next, before the effects: the voice changer and the
        // soundboard should both work on a signal that is already at the level
        // you chose.
        var gain = SettingsStore.Current.MicGain;
        if (Math.Abs(gain - 1.0) > 0.001)
        {
            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = AudioFormat.ToPcm(pcm[i] / 32768f * (float)gain);
            }
        }

        Effect.Process(pcm);

        // The clip is taken out separately so what goes to this user's own
        // speakers is the clip alone. Sending the mixed frame back would put
        // their own voice through their own speakers — a monitor loop, and one
        // the canceller would then have to chase.
        var monitor = new short[pcm.Length];
        var playing = Soundboard.MixInto(pcm, monitor);

        _session?.SendCapturedFrame(pcm);

        // Local speaking state, straight off the frame that is being sent. It is
        // what makes the ring appear on the first syllable instead of a second
        // into the sentence.
        NoteSpeech(ServerSession.Instance.MyUuid, pcm);

        // The user hears their own soundboard, but never their own voice. Voice
        // would be a monitor loop with the round trip's delay, which is
        // disorienting to talk over; the clip is a thing you triggered and
        // expect to hear.
        if (playing && !IsDeafened)
        {
            _speakers.Submit("soundboard", monitor);
        }

        if (playing != _wasPlayingClip)
        {
            _wasPlayingClip = playing;

            // A clip that has just finished gives the microphone back to
            // whatever state it was in before the button was pressed.
            if (!playing)
            {
                _ui.TryEnqueue(() => _ = RestoreMuteAfterClipAsync());
            }

            Raise();
        }
    }

    private bool _wasPlayingClip;

    /// <summary>The voice changer applied to the outgoing microphone.</summary>
    public VoiceEffect Effect { get; } = new();

    /// <summary>The built-in clips, mixed into the outgoing microphone.</summary>
    public Soundboard Soundboard { get; } = new();

    /// <summary>
    /// Triggers a clip. Works while muted on purpose — pressing a soundboard
    /// button is a deliberate act, and silently doing nothing because the
    /// microphone happens to be off reads as a broken button.
    /// </summary>
    public async Task PlayEffectAsync(SoundboardEntry entry)
    {
        if (_session is null)
        {
            return;
        }

        var pcm = SoundboardLibrary.Instance.Pcm(entry);
        if (pcm.Length == 0)
        {
            return;
        }

        if (IsMuted && CanSpeak)
        {
            // Remembered before it is changed, so the microphone can be put back
            // afterwards. Leaving it open was a real trap: somebody sitting
            // muted pressed a sound, and from then on the whole room could hear
            // them without a single thing on screen having changed.
            _mutedBeforeClip = true;
            await SetMutedAsync(false).ConfigureAwait(true);
        }

        Soundboard.Play(pcm);
        Raise();
    }

    /// <summary>
    /// What the microphone was before a clip opened it, or null when the clip
    /// did not change anything.
    /// </summary>
    private bool? _mutedBeforeClip;

    /// <summary>
    /// Puts the microphone back the way the clip found it.
    ///
    /// Only when the clip is the reason it opened: somebody who unmuted by hand
    /// while their airhorn was playing meant it, and having the app mute them a
    /// second later would be worse than the bug this fixes.
    /// </summary>
    private async Task RestoreMuteAfterClipAsync()
    {
        var restore = _mutedBeforeClip;
        _mutedBeforeClip = null;

        if (restore == true && !IsMuted)
        {
            await SetMutedAsync(true).ConfigureAwait(true);
        }
    }

    /// <summary>Cuts a clip short, for the Stop the bar shows while one is playing.</summary>
    public void StopEffect()
    {
        Soundboard.Stop();
        _ = RestoreMuteAfterClipAsync();
        Raise();
    }

    /// <summary>
    /// Plays a notification through the call's own output, if one is open.
    ///
    /// This matters more than it looks: the echo canceller only knows about
    /// audio that went through this mixer, so a notification played on a device
    /// of its own is a sound the microphone picks up and nothing removes — and
    /// then everybody else in the room hears your join chime as well as their
    /// own. Returns false when there is no call, and the caller falls back to
    /// its own output.
    /// </summary>
    public bool PlayNotification(short[] pcm)
    {
        if (!_speakers.IsRunning)
        {
            return false;
        }

        _speakers.PlayClip(pcm);
        return true;
    }

    /// <summary>True while a soundboard clip is playing, so the bar can offer Stop.</summary>
    public bool IsPlayingEffect => Soundboard.IsPlaying;

    public void SetVoiceEffect(VoiceEffectKind kind)
    {
        Effect.SetKind(kind);
        Raise();
    }

    private void OnFrameReceived(object? sender, RemoteAudioFrame frame)
    {
        NoteSpeech(frame.Identity, frame.Pcm);
        _speakers.Submit(frame.Identity, frame.Pcm);
    }

    // --- who is talking ---
    //
    // LiveKit's active-speaker list is a server-side judgement, smoothed and
    // sent a few times a second, and it showed: somebody talking for five
    // seconds got a ring for the last two of them. Every frame of everybody's
    // audio already passes through this class, so the answer is here, a hundred
    // times a second, with no round trip at all. LiveKit's list is still taken
    // as a floor, because it knows about participants whose audio this client
    // has not subscribed to.

    /// <summary>
    /// Below this the frame is a quiet room rather than a voice. Measured on the
    /// 16-bit scale, so about -46 dBFS: quiet enough to catch someone speaking
    /// softly, loud enough that a fan or a keyboard does not light the ring up.
    /// </summary>
    private const double SpeechThreshold = 160;

    /// <summary>
    /// How long the ring stays on after the last loud frame.
    ///
    /// Speech is full of gaps — every stop consonant is a moment of silence —
    /// and a ring that follows them exactly flickers. A quarter of a second
    /// bridges the gaps inside a sentence without outlasting the sentence.
    /// </summary>
    private static readonly TimeSpan SpeechHold = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, DateTime> _lastSpoke = [];
    private readonly object _speechGate = new();
    private IReadOnlyList<string> _reported = [];
    private DispatcherQueueTimer? _speechTimer;

    /// <summary>
    /// Records that one participant's frame carried speech. Called from the
    /// capture thread and from every receiving thread, so the map is locked.
    /// </summary>
    private void NoteSpeech(string identity, short[] pcm)
    {
        if (string.IsNullOrEmpty(identity) || pcm.Length == 0)
        {
            return;
        }

        // Root mean square, not peak: one sample of a click should not count as
        // talking, and speech carries its energy across the whole frame.
        double sum = 0;
        foreach (var sample in pcm)
        {
            sum += (double)sample * sample;
        }

        if (Math.Sqrt(sum / pcm.Length) < SpeechThreshold)
        {
            return;
        }

        lock (_speechGate)
        {
            _lastSpoke[identity] = DateTime.UtcNow;
        }
    }

    private void StartSpeakingWatch()
    {
        _speechTimer ??= _ui.CreateTimer();
        _speechTimer.Interval = TimeSpan.FromMilliseconds(60);
        _speechTimer.IsRepeating = true;
        _speechTimer.Tick -= OnSpeechTick;
        _speechTimer.Tick += OnSpeechTick;
        _speechTimer.Start();
    }

    private void StopSpeakingWatch()
    {
        _speechTimer?.Stop();

        lock (_speechGate)
        {
            _lastSpoke.Clear();
        }

        _reported = [];
        Speakers = [];
    }

    /// <summary>
    /// Recomputes the speaking set and only raises when it really changed —
    /// sixteen times a second into a room redraw would be a redraw for nothing.
    /// </summary>
    private void OnSpeechTick(DispatcherQueueTimer sender, object args)
    {
        var now = DateTime.UtcNow;
        List<string> speaking;

        lock (_speechGate)
        {
            foreach (var stale in _lastSpoke.Where(e => now - e.Value > SpeechHold).Select(e => e.Key).ToList())
            {
                _lastSpoke.Remove(stale);
            }

            speaking = [.. _lastSpoke.Keys];
        }

        // A muted microphone is never talking, whatever the last frame said.
        if (IsMuted)
        {
            speaking.Remove(ServerSession.Instance.MyUuid);
        }

        if (speaking.Count == _reported.Count && !speaking.Except(_reported).Any())
        {
            return;
        }

        _reported = speaking;
        Speakers = speaking;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Deliberately ignored now.
    ///
    /// It was the only source of "who is talking" and it is the reason the ring
    /// lagged: it is computed on the server, smoothed, and pushed a few times a
    /// second. Every frame already passes through this class, so the local
    /// answer is both instant and more accurate. The subscription is kept so
    /// that turning it back on is one line if it is ever needed again.
    /// </summary>
    private void OnSpeakersChanged(object? sender, IReadOnlyList<string> speakers)
    {
    }

    /// <summary>
    /// Video arrives at up to 30 frames a second per person, so it is handed to
    /// the UI thread as-is and the cell decides what to do with it. Marshalling
    /// here rather than in the cell keeps the drain loop off the dispatcher.
    /// </summary>
    private void OnVideoFrameReceived(object? sender, RemoteVideoFrame frame) =>
        _ui.TryEnqueue(() => VideoFrameReceived?.Invoke(this, frame));

    private void OnVideoTrackEnded(object? sender, RemoteVideoFrame frame) =>
        _ui.TryEnqueue(() => VideoTrackEnded?.Invoke(this, frame));

    private void OnMediaClosed(object? sender, EventArgs e) =>
        _ui.TryEnqueue(() => _ = LeaveAsync());

    private void Raise() => _ui.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
}
