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
    private readonly Dictionary<string, float> _gains = [];
    private readonly object _gate = new();

    private WasapiOut? _output;
    private MixProvider? _provider;

    /// <summary>
    /// Everything this device is about to play, handed over in 10 ms frames of
    /// 48 kHz mono as it is played.
    ///
    /// This is the echo canceller's reference signal, and it is taken here
    /// rather than anywhere upstream for one reason: what matters is what the
    /// loudspeaker actually emits, and this is the last place the mix exists as
    /// a single mono stream before it is spread across the device's channels.
    /// A reference that is not exactly what was played cancels a sound nobody
    /// heard and leaves the one they did.
    ///
    /// Raised on the playback thread, outside the mixer's lock — the capture
    /// thread takes that lock too (the soundboard) and the processor's own lock
    /// on the other side of it, so tapping under the lock would be the two
    /// threads taking the same pair in opposite orders.
    /// </summary>
    public Action<short[]>? RenderTap { get; set; }

    /// <summary>
    /// How much latency the output was opened with, which is roughly how long it
    /// is between a frame being tapped and it leaving the speaker. The echo
    /// canceller wants that number.
    /// </summary>
    public int LatencyMilliseconds { get; private set; } = LatencyMs;

    /// <summary>
    /// The endpoint actually opened, which is not always the one that was asked
    /// for: a device that is unplugged at the moment a call starts resolves to
    /// nothing and the system default is used instead. That used to happen
    /// silently, so somebody whose headset was asleep had no way to tell why
    /// their choice appeared to be ignored.
    /// </summary>
    public string DeviceName { get; private set; } = "";

    private const int LatencyMs = 60;

    public bool IsRunning => _output is not null;

    public void Start(MMDevice? device = null)
    {
        if (_output is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();

        // The **Console** default, not the Communications one.
        //
        // This is a reversal, and the reason is that the old choice was
        // unfalsifiable from the user's side. Windows keeps two defaults: the
        // one the volume flyout and Settings → Sound change, and a separate
        // "default communication device" that is only reachable from the legacy
        // Sound control panel and that OEM audio software sometimes pins
        // somewhere else. Following the second one meant the app could play out
        // of the laptop speakers while every volume control the user could see
        // pointed at their headset — and nothing on screen said so.
        //
        // For anyone who has never touched the communications default the two
        // are the same device, so this costs nothing and removes a whole class
        // of "the sound comes out of the wrong thing".
        var target = device ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        DeviceName = target.FriendlyName;

        // The system is asked not to quieten everyone else's audio while this
        // stream is open; see AudioDucking for what that is about.
        AudioDucking.OptOut(target);

        var format = target.AudioClient.MixFormat;
        _provider = new MixProvider(this, format);

        // Event sync with a short latency: this is a conversation, and latency
        // people can hear makes them talk over each other.
        var output = new WasapiOut(target, AudioClientShareMode.Shared, true, LatencyMs);
        output.Init(_provider);
        output.Play();
        _output = output;
        LatencyMilliseconds = LatencyMs;
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
        _tapPending.Clear();

        lock (_gate)
        {
            _buffers.Clear();
            _clips.Clear();
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

    /// <summary>
    /// How loud one person is, for this listener only. 1 is unchanged, 0 is
    /// silent, and above 1 amplifies — the mix is clamped afterwards, so pushing
    /// somebody quiet up is safe.
    /// </summary>
    public void SetGain(string identity, double gain)
    {
        lock (_gate)
        {
            _gains[identity] = (float)Math.Clamp(gain, 0, 4);
        }
    }

    public double GetGain(string identity)
    {
        lock (_gate)
        {
            return _gains.TryGetValue(identity, out var gain) ? gain : 1.0;
        }
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
            foreach (var (identity, queue) in _buffers)
            {
                // Per-person gain belongs here, where the streams are still
                // separate — this is the whole reason the mix is done in the app
                // rather than handed to WASAPI to sum.
                var gain = _gains.TryGetValue(identity, out var g) ? g : 1f;

                for (var i = 0; i < samples && queue.Count > 0; i++)
                {
                    mix[i] += queue.Dequeue() / 32768f * gain;
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
        /// <summary>
        /// Device-rate samples produced by the resampler but not yet asked for.
        ///
        /// Resampling a block never lands exactly on the number of frames the
        /// device wants, and the overflow used to be thrown away. That is a
        /// slow, silent decimation of the mix — and it also meant the echo
        /// canceller's reference contained samples that were never played, which
        /// is precisely the way to make it cancel the wrong thing.
        /// </summary>
        private readonly Queue<float> _ready = new();

        private double _position;
        private float _carry;

        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            var channels = WaveFormat.Channels;
            var framesWanted = count / (WaveFormat.BitsPerSample / 8) / channels;

            while (_ready.Count < framesWanted)
            {
                // Ask the mix for as many 48 kHz samples as the shortfall works
                // out to, with a little slack so this loop settles in one pass.
                var shortfall = framesWanted - _ready.Count;
                var needed = (int)Math.Ceiling(shortfall * (double)MediaSession.SampleRate / WaveFormat.SampleRate) + 2;

                var mono = owner.ReadMix(needed);
                owner.Tap(mono);

                var converted = AudioFormat.Resample(
                    mono, MediaSession.SampleRate, WaveFormat.SampleRate, ref _position, ref _carry);

                foreach (var sample in converted)
                {
                    _ready.Enqueue(sample);
                }
            }

            var target = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, float>(buffer.AsSpan(offset, count));

            for (var frame = 0; frame < framesWanted; frame++)
            {
                var value = _ready.Count > 0 ? _ready.Dequeue() : 0f;
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

    /// <summary>Whatever is left of the mix after the last whole 10 ms frame.</summary>
    private readonly List<float> _tapPending = [];

    /// <summary>
    /// Hands the mix to <see cref="RenderTap"/> in exact 10 ms frames.
    ///
    /// Only ever called from the playback thread, so its buffer needs no lock —
    /// and deliberately not called from inside the mixer's lock; see RenderTap.
    /// </summary>
    private void Tap(float[] mono)
    {
        if (RenderTap is not { } tap)
        {
            // Nothing is listening. Do not let the leftovers grow, or turning
            // the canceller on mid-call would start it with stale audio.
            _tapPending.Clear();
            return;
        }

        _tapPending.AddRange(mono);

        while (_tapPending.Count >= MediaSession.SamplesPer10Ms)
        {
            var frame = new short[MediaSession.SamplesPer10Ms];
            AudioFormat.ToPcm16(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_tapPending)[..MediaSession.SamplesPer10Ms],
                frame);

            _tapPending.RemoveRange(0, MediaSession.SamplesPer10Ms);
            tap(frame);
        }
    }
}
