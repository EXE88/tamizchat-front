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
                await SetScreenShareAsync(true).ConfigureAwait(true);
                break;
            case "camera":
                await SetCameraAsync(true).ConfigureAwait(true);
                break;
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

    public async Task SetScreenShareAsync(bool on)
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
            await ReportStateAsync().ConfigureAwait(true);
            return;
        }

        _screen.Start();
        await _session.StartVideoAsync(VideoKind.Screen, _screen.Width, _screen.Height).ConfigureAwait(true);
        _screen.FrameReady += OnScreenFrame;

        IsScreenSharing = true;
        await ReportStateAsync().ConfigureAwait(true);
    }

    /// <summary>Whether this machine has a camera, so the toggle can be hidden if not.</summary>
    public static Task<bool> HasCameraAsync() => CameraCapture.IsAvailableAsync();

    private void OnCameraFrame(object? sender, VideoFrameBuffer frame) =>
        _session?.SendVideoFrame(VideoKind.Camera, frame.Bgra, frame.Width, frame.Height);

    private void OnScreenFrame(object? sender, VideoFrameBuffer frame) =>
        _session?.SendVideoFrame(VideoKind.Screen, frame.Bgra, frame.Width, frame.Height);

    private Task ReportStateAsync()
    {
        Raise();
        return ServerSession.Instance.SetMediaStateAsync(!IsMuted, IsCameraOn, IsScreenSharing);
    }

    private void OnFrameReady(object? sender, short[] pcm) => _session?.SendCapturedFrame(pcm);

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
