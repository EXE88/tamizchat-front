using NAudio.CoreAudioApi;
using NAudio.Wave;
using TamizChat.Core.Media;

namespace TamizChat.Audio;

/// <summary>
/// Everybody else's audio, mixed into one stream and played.
///
/// Each participant's frames land in their own jitter buffer and are summed on
/// the way out, so one person's network hiccup only gaps that person. The mix
/// is converted here to whatever the render device asked for, rather than asking
/// WASAPI to convert for us — the same reason as on the capture side: this is
/// where per-person volume will live.
/// </summary>
public sealed class SpeakerPlayback : IDisposable
{
    /// <summary>
    /// How much audio may pile up per speaker before the oldest is dropped.
    ///
    /// Half a second is long enough to ride out a normal network wobble and
    /// short enough that recovering never leaves someone talking noticeably
    /// behind everyone else. Dropping is the right response, not growing: a
    /// buffer that only grows turns a hiccup into permanent delay.
    /// </summary>
    private const int MaxBufferedSamples = MediaSession.SampleRate / 2;

    private readonly Dictionary<string, Queue<short>> _buffers = [];
    private readonly object _gate = new();

    private WasapiOut? _output;
    private MixProvider? _provider;

    public bool IsRunning => _output is not null;

    public void Start(MMDevice? device = null)
    {
        if (_output is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        var target = device ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

        var format = target.AudioClient.MixFormat;
        _provider = new MixProvider(this, format);

        // Event sync with a short latency: this is a conversation, and latency
        // people can hear makes them talk over each other.
        var output = new WasapiOut(target, AudioClientShareMode.Shared, true, 60);
        output.Init(_provider);
        output.Play();
        _output = output;
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch (Exception)
        {
            // Same as capture: a device that vanished has already stopped.
        }

        output.Dispose();
        _provider = null;

        lock (_gate)
        {
            _buffers.Clear();
        }
    }

    /// <summary>Queues one participant's 10 ms frame for playback.</summary>
    public void Submit(string identity, short[] pcm)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(identity, out var queue))
            {
                queue = new Queue<short>();
                _buffers[identity] = queue;
            }

            foreach (var sample in pcm)
            {
                queue.Enqueue(sample);
            }

            while (queue.Count > MaxBufferedSamples)
            {
                queue.Dequeue();
            }
        }
    }

    /// <summary>
    /// Plays a complete sound to the end, mixed alongside the voices.
    ///
    /// Deliberately **not** `Submit`. That path is a jitter buffer with a half
    /// second cap: a two-second notification pushed through it has its own
    /// beginning dropped sample by sample as the rest arrives, so all anyone
    /// hears is the last half second. That really happened. A clip is not a
    /// stream — it is finite, it is already all here, and it must play whole.
    /// </summary>
    public void PlayClip(short[] pcm)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _clips.Add(new OneShot(pcm));
        }
    }

    /// <summary>Stops every one-shot immediately, leaving voices alone.</summary>
    public void StopClips()
    {
        lock (_gate)
        {
            _clips.Clear();
        }
    }

    public bool IsPlayingClip
    {
        get
        {
            lock (_gate)
            {
                return _clips.Count > 0;
            }
        }
    }

    private readonly List<OneShot> _clips = [];

    private sealed class OneShot(short[] pcm)
    {
        public short[] Pcm { get; } = pcm;

        public int Position { get; set; }
    }

    /// <summary>Forgets a participant, so their buffer does not linger after they leave.</summary>
    public void Remove(string identity)
    {
        lock (_gate)
        {
            _buffers.Remove(identity);
        }
    }

    public void Dispose() => Stop();

    /// <summary>Pulls the next mono samples, summed across everyone currently buffered.</summary>
    private float[] ReadMix(int samples)
    {
        var mix = new float[samples];

        lock (_gate)
        {
            foreach (var queue in _buffers.Values)
            {
                for (var i = 0; i < samples && queue.Count > 0; i++)
                {
                    mix[i] += queue.Dequeue() / 32768f;
                }
            }

            // One-shots advance by however much was actually taken, and are
            // removed the moment they run out.
            for (var c = _clips.Count - 1; c >= 0; c--)
            {
                var clip = _clips[c];
                var taken = Math.Min(samples, clip.Pcm.Length - clip.Position);

                for (var i = 0; i < taken; i++)
                {
                    mix[i] += clip.Pcm[clip.Position + i] / 32768f;
                }

                clip.Position += taken;

                if (clip.Position >= clip.Pcm.Length)
                {
                    _clips.RemoveAt(c);
                }
            }
        }

        // Summing several people can exceed full scale. Clamping here is what
        // keeps a loud moment from wrapping into a crack; per-speaker gain in
        // F10 is the better long-term answer.
        for (var i = 0; i < mix.Length; i++)
        {
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
        }

        return mix;
    }

    /// <summary>
    /// Presents the mono 48 kHz mix in the device's own format: resampled if the
    /// device disagrees about the rate, and copied across every channel.
    /// </summary>
    private sealed class MixProvider(SpeakerPlayback owner, WaveFormat format) : IWaveProvider
    {
        private double _position;
        private float _carry;

        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            var channels = WaveFormat.Channels;
            var framesWanted = count / (WaveFormat.BitsPerSample / 8) / channels;

            // Ask the mix for as many 48 kHz samples as this many device frames
            // works out to.
            var needed = (int)Math.Ceiling(framesWanted * (double)MediaSession.SampleRate / WaveFormat.SampleRate) + 2;
            var mono = owner.ReadMix(needed);
            var converted = AudioFormat.Resample(mono, MediaSession.SampleRate, WaveFormat.SampleRate, ref _position, ref _carry);

            var target = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, float>(buffer.AsSpan(offset, count));

            for (var frame = 0; frame < framesWanted; frame++)
            {
                var value = frame < converted.Length ? converted[frame] : 0f;
                for (var c = 0; c < channels; c++)
                {
                    target[(frame * channels) + c] = value;
                }
            }

            // Always return a full buffer. Returning less tells NAudio the stream
            // ended and playback stops for good — silence is the correct output
            // when nobody is talking, not the end of the call.
            return count;
        }
    }
}
