namespace TamizChat.Audio;

/// <summary>The voice changer presets offered on the bottom bar.</summary>
public enum VoiceEffectKind
{
    None,
    Deep,
    Chipmunk,
    Robot,
    Radio,
}

/// <summary>
/// The voice changer: runs on our own capture path, in this process, before a
/// frame is handed to LiveKit.
///
/// This is the whole reason the project refuses to ship a virtual audio driver.
/// TamizChat captures the microphone and publishes it, so there is nothing to
/// route anywhere — the effect just sits in the middle of a pipeline we already
/// own.
///
/// Everything here works on one 10 ms frame at a time and keeps its own state
/// between calls, because that is the unit the capture path delivers. Nothing
/// allocates per frame: at a hundred frames a second, allocating would give the
/// garbage collector a steady job for no reason.
/// </summary>
public sealed class VoiceEffect
{
    private readonly object _gate = new();

    // Pitch shifting reads from a delay line at a different rate than it is
    // written. Long enough to hold a few frames so the read pointer can lag
    // behind the write pointer without catching it.
    private const int RingLength = AudioFormat.SampleRate / 4; // 250 ms

    private readonly float[] _ring = new float[RingLength];
    private int _writeIndex;
    private double _readIndex;

    // Ring modulation for the robot, and the band-pass state for the radio.
    private double _modulatorPhase;
    private float _highPassPrevious;
    private float _highPassPreviousOut;
    private float _lowPassState;

    public VoiceEffectKind Kind { get; private set; } = VoiceEffectKind.None;

    public void SetKind(VoiceEffectKind kind)
    {
        lock (_gate)
        {
            if (kind == Kind)
            {
                return;
            }

            Kind = kind;
            Reset();
        }
    }

    /// <summary>
    /// Applies the current effect to one frame, in place.
    ///
    /// In place because the caller's buffer is reused by the capture path and
    /// handing back a new array every 10 ms is pure garbage.
    /// </summary>
    public void Process(short[] frame)
    {
        lock (_gate)
        {
            switch (Kind)
            {
                case VoiceEffectKind.None:
                    return;

                // Below and above natural pitch. A semitone is 2^(1/12), so
                // these are about five semitones down and seven up — enough to
                // be unmistakable without turning speech into mush.
                case VoiceEffectKind.Deep:
                    PitchShift(frame, 0.75);
                    return;

                case VoiceEffectKind.Chipmunk:
                    PitchShift(frame, 1.5);
                    return;

                case VoiceEffectKind.Robot:
                    Robot(frame);
                    return;

                case VoiceEffectKind.Radio:
                    Radio(frame);
                    return;
            }
        }
    }

    private void Reset()
    {
        Array.Clear(_ring);
        _writeIndex = 0;
        _readIndex = 0;
        _modulatorPhase = 0;
        _highPassPrevious = 0;
        _highPassPreviousOut = 0;
        _lowPassState = 0;
    }

    /// <summary>
    /// Shifts pitch without changing how long the frame lasts.
    ///
    /// The signal is written into a ring at the normal rate and read out at
    /// <paramref name="ratio"/> times that rate, which shifts the pitch but
    /// drains or fills the buffer. Two read heads half a buffer apart, crossfaded
    /// against each other, hide the wrap: when one reaches the end of its window
    /// it is silent, so the discontinuity is never heard. That is the classic
    /// granular shifter, and it is what makes this cheap enough to run on every
    /// frame without a fourier transform.
    /// </summary>
    private void PitchShift(short[] frame, double ratio)
    {
        // A quarter of the ring: short enough that the doubling is not heard as
        // an echo, long enough to cover several pitch periods of a low voice.
        const int window = RingLength / 4;

        foreach (var sample in frame)
        {
            _ring[_writeIndex] = sample / 32768f;
            _writeIndex = (_writeIndex + 1) % RingLength;
        }

        for (var i = 0; i < frame.Length; i++)
        {
            // How far this read head has drifted from the write head decides its
            // gain, so the two heads always sum to roughly one.
            var offset = _readIndex;
            var second = offset + (window / 2.0);

            var fade = (float)(offset % window / window);
            var first = Read(offset);
            var mirror = Read(second);

            var mixed = (first * (1 - fade)) + (mirror * fade);

            frame[i] = AudioFormat.ToPcm(mixed);
            _readIndex += ratio;

            if (_readIndex >= RingLength)
            {
                _readIndex -= RingLength;
            }
        }
    }

    /// <summary>Linear interpolation into the ring, which is read at fractional positions.</summary>
    private float Read(double position)
    {
        // The read head trails the write head by a fixed distance, so it is
        // always reading samples that have already been written.
        var index = (position + _writeIndex + (RingLength / 2.0)) % RingLength;
        var whole = (int)index;
        var fraction = (float)(index - whole);

        var a = _ring[whole % RingLength];
        var b = _ring[(whole + 1) % RingLength];
        return a + ((b - a) * fraction);
    }

    /// <summary>
    /// Ring modulation: the voice multiplied by a low sine.
    ///
    /// Not a vocoder — a vocoder would be the "proper" robot and needs a filter
    /// bank per band. Ring modulation is two lines and lands in the same place
    /// for a soundboard-grade effect: it replaces the speaker's pitch with a
    /// fixed one and keeps the rhythm of the words.
    /// </summary>
    private void Robot(short[] frame)
    {
        const double modulatorHz = 55;

        for (var i = 0; i < frame.Length; i++)
        {
            var value = frame[i] / 32768f;
            value *= (float)Math.Sin(_modulatorPhase);

            _modulatorPhase += 2 * Math.PI * modulatorHz / AudioFormat.SampleRate;
            if (_modulatorPhase > 2 * Math.PI)
            {
                _modulatorPhase -= 2 * Math.PI;
            }

            frame[i] = AudioFormat.ToPcm(value);
        }
    }

    /// <summary>
    /// A cheap band-pass plus soft clipping: the sound of a two-way radio.
    ///
    /// Telephone band, roughly 300 Hz to 3 kHz. Losing the bass is what makes it
    /// read as "through a speaker" rather than "in the room"; the clipping adds
    /// the overdriven edge.
    /// </summary>
    private void Radio(short[] frame)
    {
        // One-pole coefficients for the two corners, at 48 kHz.
        const float highPassCoefficient = 0.96f;
        const float lowPassCoefficient = 0.35f;

        for (var i = 0; i < frame.Length; i++)
        {
            var value = frame[i] / 32768f;

            // High pass: keep what changes fast, drop the slow-moving bass.
            var highPassed = highPassCoefficient * (_highPassPreviousOut + value - _highPassPrevious);
            _highPassPrevious = value;
            _highPassPreviousOut = highPassed;

            // Low pass: shave the top off, so it is a band rather than a hiss.
            _lowPassState += lowPassCoefficient * (highPassed - _lowPassState);

            // Soft clip. Tanh saturates smoothly instead of squaring off, which
            // is the difference between "overdriven" and "broken".
            frame[i] = AudioFormat.ToPcm((float)Math.Tanh(_lowPassState * 3.0) * 0.8f);
        }
    }
}
