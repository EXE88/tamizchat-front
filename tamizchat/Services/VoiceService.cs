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
    private readonly SpeakerPlayback _speakers = new();

    private MediaSession? _session;
    private string _roomId = "";

    public static VoiceService Instance { get; } = new();

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
        var session = ServerSession.Instance;
        if (string.IsNullOrEmpty(session.MyRoomId))
        {
            return;
        }

        if (_roomId == session.MyRoomId && IsConnected)
        {
            return;
        }

        await LeaveAsync().ConfigureAwait(true);

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

        _speakers.Start();
        Raise();

        if (CanSpeak)
        {
            await SetMutedAsync(false).ConfigureAwait(true);
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
            _speakers.Start();
        }

        Raise();
    }

    public async Task LeaveAsync()
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

        _microphone.Stop();
        _microphone.FrameReady -= OnFrameReady;
        _speakers.Stop();

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
                    _microphone.Start();
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
        await ServerSession.Instance.SetMediaStateAsync(!muted).ConfigureAwait(true);
        Raise();
    }

    public Task ToggleMuteAsync() => SetMutedAsync(!IsMuted);

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

        _screen.Start(target ?? throw new InvalidOperationException("nothing was chosen to share"));
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

    private Task ReportStateAsync()
    {
        Raise();
        return ServerSession.Instance.SetMediaStateAsync(!IsMuted, IsCameraOn, IsScreenSharing);
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
        Effect.Process(pcm);
        var playing = Soundboard.MixInto(pcm);

        _session?.SendCapturedFrame(pcm);

        // The user hears their own soundboard, but never their own voice. Voice
        // would be a monitor loop with the round trip's delay, which is
        // disorienting to talk over; the clip is a thing you triggered and
        // expect to hear.
        if (playing && !IsDeafened)
        {
            _speakers.Submit("soundboard", pcm);
        }

        if (playing != _wasPlayingClip)
        {
            _wasPlayingClip = playing;
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
            await SetMutedAsync(false).ConfigureAwait(true);
        }

        Soundboard.Play(pcm);
        Raise();
    }

    /// <summary>Cuts a clip short, for the Stop the bar shows while one is playing.</summary>
    public void StopEffect()
    {
        Soundboard.Stop();
        Raise();
    }

    /// <summary>True while a soundboard clip is playing, so the bar can offer Stop.</summary>
    public bool IsPlayingEffect => Soundboard.IsPlaying;

    public void SetVoiceEffect(VoiceEffectKind kind)
    {
        Effect.SetKind(kind);
        Raise();
    }

    private void OnFrameReceived(object? sender, RemoteAudioFrame frame) =>
        _speakers.Submit(frame.Identity, frame.Pcm);

    private void OnSpeakersChanged(object? sender, IReadOnlyList<string> speakers) =>
        _ui.TryEnqueue(() =>
        {
            Speakers = speakers;
            Changed?.Invoke(this, EventArgs.Empty);
        });

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
