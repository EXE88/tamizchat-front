using LiveKit.Rtc;
using TamizChat.Core.Protocol;

// LiveKit has a Room and so do we, and they mean genuinely different things:
// theirs is a live media session, ours is a room definition on the server.
using LkRoom = LiveKit.Rtc.Room;

namespace TamizChat.Core.Media;

/// <summary>
/// One voice connection to LiveKit, for the room the user is in.
///
/// This deliberately does no device work. Audio is <em>pushed in</em> through
/// <see cref="SendCapturedFrame"/> and comes <em>out</em> through
/// <see cref="FrameReceived"/>, which is what lets the voice changer and the
/// soundboard sit in our own pipeline instead of behind a virtual audio driver.
/// It also keeps this class free of any Windows dependency, so the console
/// simulator can be a talking participant.
/// </summary>
public sealed class MediaSession : IAsyncDisposable
{
    /// <summary>
    /// LiveKit works in 10 ms frames, which at 48 kHz mono is 480 samples. Every
    /// buffer in the audio path is built around this number.
    /// </summary>
    public const int SampleRate = 48000;

    public const int Channels = 1;

    public const int SamplesPer10Ms = SampleRate / 100;

    private readonly LkRoom _room = new();
    private readonly object _gate = new();

    private AudioSource? _source;
    private LocalAudioTrack? _track;
    private CancellationTokenSource? _streams;
    private bool _connected;

    /// <summary>Raised for every 10 ms of audio arriving from somebody else.</summary>
    public event EventHandler<RemoteAudioFrame>? FrameReceived;

    /// <summary>
    /// Raised as LiveKit's idea of who is talking changes. This comes from
    /// LiveKit, not the TamizChat server, which deliberately does not track it.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? SpeakersChanged;

    /// <summary>Raised when the media connection drops, separately from the chat one.</summary>
    public event EventHandler? Closed;

    public bool IsConnected => _connected && _room.IsConnected;

    /// <summary>The LiveKit room, which is the TamizChat room id.</summary>
    public string RoomId { get; private set; } = "";

    /// <summary>Whether the microphone track is published and unmuted.</summary>
    public bool IsPublishing { get; private set; }

    public MediaSession()
    {
        _room.Connected += (_, _) => _connected = true;
        _room.Disconnected += (_, _) => Drop();
        _room.ActiveSpeakersChanged += (_, e) =>
            SpeakersChanged?.Invoke(this, e.Speakers.Select(p => p.Identity).ToList());

        _room.TrackSubscribed += OnTrackSubscribed;
    }

    /// <summary>Enters the LiveKit room with credentials issued by our own server.</summary>
    public async Task ConnectAsync(MediaToken credentials, CancellationToken cancellationToken = default)
    {
        RoomId = credentials.Room;
        _streams = new CancellationTokenSource();

        await _room.ConnectAsync(
            credentials.Url,
            credentials.Token,
            new RoomOptions { AutoSubscribe = true },
            cancellationToken).ConfigureAwait(false);

        _connected = true;
    }

    /// <summary>
    /// Publishes the microphone track. Called once the user actually wants to
    /// speak, so somebody who only listens never appears as a publisher.
    /// </summary>
    public async Task StartPublishingAsync(CancellationToken cancellationToken = default)
    {
        if (_source is not null || !IsConnected)
        {
            return;
        }

        var source = new AudioSource(SampleRate, Channels);
        var track = LocalAudioTrack.Create("microphone", source);

        await _room.LocalParticipant!.PublishTrackAsync(
            track,
            new TrackPublishOptions { Source = LiveKit.Proto.TrackSource.SourceMicrophone },
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _source = source;
            _track = track;
        }

        IsPublishing = true;
    }

    /// <summary>
    /// Hands one 10 ms frame of 48 kHz mono PCM to LiveKit.
    ///
    /// Fire and forget on purpose: this is called from the capture callback, and
    /// blocking it to await the network would stutter the microphone. LiveKit
    /// queues internally and a dropped frame is 10 ms of audio, not a failure
    /// worth propagating.
    /// </summary>
    public void SendCapturedFrame(short[] pcm)
    {
        AudioSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null || !IsPublishing)
        {
            return;
        }

        try
        {
            source.CaptureFrame(new AudioFrame(pcm, SampleRate, Channels, pcm.Length));
        }
        catch (Exception)
        {
            // A frame lost while the track is being torn down is not an error.
        }
    }

    /// <summary>
    /// Stops sending audio while staying in the room, which is what mute means:
    /// the person keeps hearing everyone else.
    ///
    /// Two things, and both are needed. Not feeding the source is what actually
    /// silences the microphone. Muting the track is what makes it *true* for
    /// everybody else: LiveKit and the server both track a publication's muted
    /// flag, and a track that keeps claiming to be live while sending nothing is
    /// how somebody ends up drawn as talking when they are not — or, worse,
    /// drawn as muted while they are being heard.
    /// </summary>
    public void SetMuted(bool muted)
    {
        IsPublishing = _source is not null && !muted;

        LocalAudioTrack? track;
        lock (_gate)
        {
            track = _track;
        }

        if (track is null)
        {
            return;
        }

        try
        {
            if (muted)
            {
                track.Mute();
            }
            else
            {
                track.Unmute();
            }
        }
        catch (Exception)
        {
            // The track is going away. The frames have already stopped, which is
            // the half that matters to anyone listening.
        }
    }

    // --- video: camera and screen share ---

    /// <summary>Raised for every frame of somebody else's camera or screen.</summary>
    public event EventHandler<RemoteVideoFrame>? VideoFrameReceived;

    /// <summary>Raised when a remote camera or screen track goes away.</summary>
    public event EventHandler<RemoteVideoFrame>? VideoTrackEnded;

    private readonly Dictionary<VideoKind, VideoPublication> _video = [];

    public bool IsSending(VideoKind kind) => _video.ContainsKey(kind);

    /// <summary>
    /// Publishes a camera or screen track. The size is fixed at publish time
    /// because it is the source's size, not the window's — a capture that
    /// changes shape has to republish.
    /// </summary>
    public async Task StartVideoAsync(VideoKind kind, int width, int height, CancellationToken cancellationToken = default)
    {
        if (_video.ContainsKey(kind) || !IsConnected)
        {
            return;
        }

        // Checked here rather than trusted from the caller: libwebrtc asserts on
        // a zero-sized frame buffer and an assert there is a hard abort, not an
        // exception — it kills the process with no stack a caller can catch. A
        // camera that has not reported its size yet really did do this.
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"a {kind} track cannot be published at {width}x{height}");
        }

        var source = new VideoSource(width, height);
        var track = LocalVideoTrack.Create(kind == VideoKind.Camera ? "camera" : "screen", source);

        var publication = await _room.LocalParticipant!.PublishTrackAsync(
            track,
            new TrackPublishOptions
            {
                Source = kind == VideoKind.Camera
                    ? LiveKit.Proto.TrackSource.SourceCamera
                    : LiveKit.Proto.TrackSource.SourceScreenshare,

                // Screen content is mostly still and full of text, so letting
                // LiveKit drop resolution on it makes it unreadable. A camera
                // degrades gracefully; a spreadsheet does not.
                Simulcast = kind == VideoKind.Camera,
            },
            cancellationToken).ConfigureAwait(false);

        _video[kind] = new VideoPublication(source, publication.Sid ?? "", width, height);
    }

    public async Task StopVideoAsync(VideoKind kind)
    {
        if (!_video.Remove(kind, out var publication))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(publication.Sid))
            {
                await _room.LocalParticipant!.UnpublishTrackAsync(publication.Sid).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Unpublishing a track the room has already torn down is fine.
        }

        publication.Source.Dispose();
    }

    /// <summary>
    /// Hands one frame of BGRA pixels to LiveKit. Fire and forget, for the same
    /// reason as audio: this runs on a capture callback.
    /// </summary>
    public void SendVideoFrame(VideoKind kind, byte[] bgra, int width, int height)
    {
        if (!_video.TryGetValue(kind, out var publication))
        {
            return;
        }

        // What must match is the buffer against its own claimed size — a short
        // buffer is an out-of-bounds read in native code. The *published* size is
        // deliberately not enforced: it is only the initial hint, libwebrtc
        // handles a source changing size, and a camera whose driver delivers
        // something other than the format it was asked for is normal. Dropping
        // on that mismatch silently published nothing at all while the local
        // preview looked perfect, which is a horrible way to fail.
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return;
        }

        try
        {
            publication.Source.CaptureFrame(
                new VideoFrame(width, height, LiveKit.Proto.VideoBufferType.Bgra, bgra),
                0,
                LiveKit.Proto.VideoRotation._0);
        }
        catch (Exception)
        {
            // Same as audio: a frame lost during teardown is not an error.
        }
    }

    private sealed record VideoPublication(VideoSource Source, string Sid, int Width, int Height);

    public async ValueTask DisposeAsync()
    {
        Drop();

        if (_streams is not null)
        {
            await _streams.CancelAsync().ConfigureAwait(false);
            _streams.Dispose();
            _streams = null;
        }

        try
        {
            await _room.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Leaving a connection that is already gone is not worth reporting.
        }

        lock (_gate)
        {
            _source?.Dispose();
            _source = null;
            _track = null;
        }

        _room.Dispose();
    }

    /// <summary>
    /// Starts draining a newly subscribed track.
    ///
    /// Subscribing is not receiving: unless the stream is read, no audio ever
    /// arrives. Each track gets its own reader, and the identity is captured here
    /// so the mixer downstream knows who is speaking.
    /// </summary>
    private void OnTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        var identity = e.Participant.Identity;

        if (e.Track is RemoteVideoTrack video)
        {
            // Screen share and camera arrive as separate tracks and are told
            // apart by the publication's source, not by the track itself.
            var kind = e.Publication.Source == LiveKit.Proto.TrackSource.SourceScreenshare
                ? VideoKind.Screen
                : VideoKind.Camera;

            DrainVideo(video, identity, kind);
            return;
        }

        if (e.Track is not RemoteAudioTrack audio)
        {
            return;
        }

        var token = _streams?.Token ?? CancellationToken.None;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using var stream = AudioStream.FromTrack(audio, SampleRate, Channels);
                    await foreach (var frame in stream.WithCancellation(token).ConfigureAwait(false))
                    {
                        FrameReceived?.Invoke(this, new RemoteAudioFrame(identity, frame.Frame.DataArray));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Leaving the room, which is the normal way this ends.
                }
                catch (Exception)
                {
                    // One participant's track failing must not take the call down.
                }
            },
            token);
    }

    /// <summary>
    /// Reads one remote video track until it ends, converting each frame to BGRA
    /// so the UI gets something it can put straight into a bitmap.
    /// </summary>
    private void DrainVideo(RemoteVideoTrack track, string identity, VideoKind kind)
    {
        var token = _streams?.Token ?? CancellationToken.None;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using var stream = VideoStream.FromTrack(track, LiveKit.Proto.VideoBufferType.Bgra);
                    await foreach (var frame in stream.WithCancellation(token).ConfigureAwait(false))
                    {
                        var buffer = frame.Frame;
                        VideoFrameReceived?.Invoke(
                            this,
                            new RemoteVideoFrame(identity, kind, buffer.Width, buffer.Height, buffer.DataBytes));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Leaving the room.
                }
                catch (Exception)
                {
                    // One broken video track must not take the call down.
                }
                finally
                {
                    VideoTrackEnded?.Invoke(this, new RemoteVideoFrame(identity, kind, 0, 0, []));
                }
            },
            token);
    }

    private void Drop()
    {
        if (!_connected)
        {
            return;
        }

        _connected = false;
        IsPublishing = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The two kinds of video a participant can publish.</summary>
public enum VideoKind
{
    Camera,
    Screen,
}

/// <summary>One frame of somebody's camera or screen, as BGRA pixels.</summary>
public sealed class RemoteVideoFrame(string identity, VideoKind kind, int width, int height, byte[] bgra)
{
    public string Identity { get; } = identity;

    public VideoKind Kind { get; } = kind;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Bgra { get; } = bgra;
}

/// <summary>10 ms of 48 kHz mono PCM from one participant.</summary>
public sealed class RemoteAudioFrame(string identity, short[] pcm)
{
    /// <summary>The speaker's client UUID — LiveKit identities are our UUIDs.</summary>
    public string Identity { get; } = identity;

    public short[] Pcm { get; } = pcm;
}
