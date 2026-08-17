using NAudio.CoreAudioApi;
using NAudio.Wave;
using TamizChat.Core.Media;

namespace TamizChat.Audio;

/// <summary>
/// The microphone, delivered as the exact frames LiveKit wants: 10 ms of 48 kHz
/// mono PCM, over and over.
///
/// The device is captured in its own mix format — shared-mode WASAPI does not
/// let us ask for a different one — and converted here. <see cref="Process"/> is
/// where the voice changer and the soundboard mix will go in F10; it is a hook
/// now so that the pipeline never has to be rearranged to add them.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly List<float> _pending = [];
    private WasapiCapture? _capture;
    private double _position;
    private float _carry;
    private int _channels;
    private int _deviceRate;

    /// <summary>Raised for each complete 10 ms frame, on the capture thread.</summary>
    public event EventHandler<short[]>? FrameReady;

    /// <summary>
    /// The last frame's peak, 0..1, for a level meter. Read from the UI thread
    /// without locking: a torn read of a float costs one stale meter update.
    /// </summary>
    public float Level { get; private set; }

    /// <summary>
    /// The seam the effects chain plugs into. It runs on mono 48 kHz floats,
    /// in place, before the frame is quantised and sent.
    /// </summary>
    public Func<float[], float[]>? Process { get; set; }

    public bool IsRunning => _capture is not null;

    /// <summary>Starts the default communications microphone.</summary>
    public void Start(MMDevice? device = null)
    {
        if (_capture is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();

        // The Communications role, not Console: Windows lets people pick a
        // different default for calls than for music, and this is a call.
        var target = device ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

        // Opening a microphone is what makes Windows decide a call is happening
        // and turn everything else down. This asks it not to.
        AudioDucking.OptOut(target);

        var capture = new WasapiCapture(target);
        _channels = capture.WaveFormat.Channels;
        _deviceRate = capture.WaveFormat.SampleRate;
        _position = 0;
        _carry = 0;
        _pending.Clear();

        capture.DataAvailable += OnDataAvailable;
        capture.StartRecording();
        _capture = capture;
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // A device unplugged mid-call stops itself; nothing to report.
        }

        capture.Dispose();
        Level = 0;
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        // WASAPI shared mode hands over 32-bit float.
        var samples = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));

        var mono = AudioFormat.ToMono(samples, _channels);
        var resampled = AudioFormat.Resample(mono, _deviceRate, MediaSession.SampleRate, ref _position, ref _carry);

        _pending.AddRange(resampled);

        // Whatever the device's buffer size is, LiveKit is fed in 480-sample
        // frames, so the remainder is kept for the next callback.
        while (_pending.Count >= MediaSession.SamplesPer10Ms)
        {
            var frame = _pending.GetRange(0, MediaSession.SamplesPer10Ms).ToArray();
            _pending.RemoveRange(0, MediaSession.SamplesPer10Ms);

            if (Process is { } effects)
            {
                frame = effects(frame);
            }

            var peak = 0f;
            foreach (var sample in frame)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            Level = peak;

            var pcm = new short[MediaSession.SamplesPer10Ms];
            AudioFormat.ToPcm16(frame, pcm);
            FrameReady?.Invoke(this, pcm);
        }
    }
}
