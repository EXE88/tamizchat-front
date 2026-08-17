using System.Runtime.InteropServices;
using LiveKit.Proto;
using LiveKit.Rtc.Internal;

namespace TamizChat.Core.Media;

/// <summary>
/// WebRTC's audio processing module: acoustic echo cancellation, noise
/// suppression and a high-pass filter, applied to our own capture path.
///
/// This is the real AEC3, not something written here. It already ships inside
/// the LiveKit native library the app loads anyway, and the FFI exposes it as a
/// standalone object — which is exactly what a client that does its own device
/// I/O needs. libwebrtc's built-in echo canceller can only work when libwebrtc
/// also owns the loudspeaker, and it does not here: capture is WASAPI in
/// <c>MicrophoneCapture</c> and playback is WASAPI in <c>SpeakerPlayback</c>.
///
/// The contract is two streams that must both be fed, in real time:
///
/// <list type="bullet">
/// <item><b>Render (reverse)</b> — every sample that goes to the speakers,
/// handed over as it is played. This is what the canceller learns to recognise.</item>
/// <item><b>Capture (forward)</b> — every microphone frame, processed in place;
/// whatever of the render signal the microphone picked up is subtracted.</item>
/// </list>
///
/// Feeding only one of them does nothing at all, and feeding the render stream
/// from something other than what is actually being played is worse than not
/// feeding it: the canceller then removes a signal nobody heard.
///
/// Frames must be exactly 10 ms — 480 samples at 48 kHz — which is the frame
/// size the whole audio path is already built around.
/// </summary>
public sealed class AudioProcessor : IDisposable
{
    /// <summary>
    /// Serializes the two calls.
    ///
    /// The capture thread and the playback thread both arrive here a hundred
    /// times a second each, and the native object is not documented as safe to
    /// enter twice at once. Each call is far below the 10 ms budget, so the
    /// contention costs nothing measurable and removes a whole class of
    /// question.
    /// </summary>
    private readonly object _gate = new();

    private readonly FfiHandle _handle;
    private readonly ulong _id;
    private bool _disposed;

    private AudioProcessor(ulong id)
    {
        _id = id;
        _handle = FfiHandle.FromId(id);
    }

    /// <summary>
    /// Creates the processor, or returns null if the native library will not
    /// give us one.
    ///
    /// Null is a normal outcome, not an error: an older native binary simply
    /// does not have the APM requests, and voice without echo cancellation is
    /// the behaviour this app already shipped with. Falling back is better than
    /// refusing to join a room.
    /// </summary>
    public static AudioProcessor? TryCreate(
        bool echoCancellation = true,
        bool noiseSuppression = true,
        bool highPassFilter = true,
        bool gainControl = false)
    {
        try
        {
            var response = FfiClient.Instance.SendRequest(new FfiRequest
            {
                NewApm = new NewApmRequest
                {
                    EchoCancellerEnabled = echoCancellation,
                    NoiseSuppressionEnabled = noiseSuppression,
                    HighPassFilterEnabled = highPassFilter,

                    // Off by default. The app has its own microphone level in
                    // settings, and two gain stages fighting each other is how a
                    // level slider stops appearing to do anything.
                    GainControllerEnabled = gainControl,
                },
            });

            var id = response?.NewApm?.Apm?.Handle?.Id ?? 0;
            return id == 0 ? null : new AudioProcessor(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells the canceller roughly how long it is between a frame being handed
    /// to <see cref="ProcessRender"/> and the microphone hearing it.
    ///
    /// A hint only — AEC3 estimates the real delay itself — but starting from
    /// somewhere near the truth is what stops the first second of a call
    /// echoing while it converges.
    /// </summary>
    public void SetStreamDelay(int milliseconds)
    {
        Send(new FfiRequest
        {
            ApmSetStreamDelay = new ApmSetStreamDelayRequest
            {
                ApmHandle = _id,
                DelayMs = Math.Clamp(milliseconds, 0, 500),
            },
        });
    }

    /// <summary>
    /// Cleans one microphone frame, in place. 480 samples of 48 kHz mono.
    /// </summary>
    public void ProcessCapture(short[] pcm) => Process(pcm, reverse: false);

    /// <summary>
    /// Hands over one frame of what the speakers are playing. 480 samples of
    /// 48 kHz mono, at the rate it is really being played.
    /// </summary>
    public void ProcessRender(short[] pcm) => Process(pcm, reverse: true);

    private void Process(short[] pcm, bool reverse)
    {
        if (pcm.Length != MediaSession.SamplesPer10Ms)
        {
            // The module only accepts 10 ms. A frame of any other length is a
            // bug upstream, and processing it would corrupt the canceller's
            // idea of time rather than fail loudly.
            return;
        }

        // Pinned rather than `fixed`, so this project needs no unsafe blocks:
        // the native side is handed a raw address and writes the cleaned
        // samples back into the very same array.
        var pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);

        try
        {
            var address = (ulong)pin.AddrOfPinnedObject().ToInt64();
            var bytes = (uint)(pcm.Length * sizeof(short));

            Send(reverse
                ? new FfiRequest
                {
                    ApmProcessReverseStream = new ApmProcessReverseStreamRequest
                    {
                        ApmHandle = _id,
                        DataPtr = address,
                        Size = bytes,
                        SampleRate = MediaSession.SampleRate,
                        NumChannels = MediaSession.Channels,
                    },
                }
                : new FfiRequest
                {
                    ApmProcessStream = new ApmProcessStreamRequest
                    {
                        ApmHandle = _id,
                        DataPtr = address,
                        Size = bytes,
                        SampleRate = MediaSession.SampleRate,
                        NumChannels = MediaSession.Channels,
                    },
                });
        }
        finally
        {
            pin.Free();
        }
    }

    private void Send(FfiRequest request)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                FfiClient.Instance.SendRequest(request);
            }
            catch (Exception)
            {
                // One dropped frame of processing is 10 ms of audio going out
                // uncancelled. Taking the call down for it would be far worse.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }
    }
}
