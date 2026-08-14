namespace TamizChat.Audio;

/// <summary>The built-in clips, in the order the bottom bar lists them.</summary>
public enum SoundEffect
{
    Airhorn,
    Applause,
    DrumRoll,
    Rimshot,
    Crickets,
}

/// <summary>
/// The soundboard: clips mixed into the outgoing microphone stream.
///
/// The clips are **synthesised**, not shipped as files. Nothing here needs a
/// licence, an asset folder or an installer step, and the whole soundboard costs
/// a few hundred lines instead of a few megabytes. A user's own files can be
/// added later on top of this — playback does not care where samples come from.
///
/// Only one clip plays at a time. Two airhorns at once is noise, and the second
/// press almost always means "again", not "both".
/// </summary>
public sealed class Soundboard
{
    private readonly object _gate = new();
    private readonly Dictionary<SoundEffect, short[]> _clips = [];

    private short[]? _playing;
    private int _position;

    /// <summary>True while a clip is still being mixed in.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playing is not null;
            }
        }
    }

    public void Play(SoundEffect effect)
    {
        lock (_gate)
        {
            if (!_clips.TryGetValue(effect, out var clip))
            {
                clip = Render(effect);
                _clips[effect] = clip;
            }

            // Restart rather than layer: pressing again means "again".
            _playing = clip;
            _position = 0;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _playing = null;
            _position = 0;
        }
    }

    /// <summary>
    /// Mixes the playing clip into a frame, in place, and reports whether
    /// anything was added.
    ///
    /// The voice is ducked rather than replaced, so somebody can talk over their
    /// own airhorn — and the sum is clamped, because two signals added together
    /// overflow long before either one does on its own.
    /// </summary>
    public bool MixInto(short[] frame)
    {
        lock (_gate)
        {
            if (_playing is null)
            {
                return false;
            }

            for (var i = 0; i < frame.Length; i++)
            {
                if (_position >= _playing.Length)
                {
                    _playing = null;
                    _position = 0;
                    return true;
                }

                var voice = frame[i] / 32768f * 0.6f;
                var clip = _playing[_position] / 32768f;
                _position++;

                frame[i] = AudioFormat.ToPcm(voice + clip);
            }

            return true;
        }
    }

    private static short[] Render(SoundEffect effect) => effect switch
    {
        SoundEffect.Airhorn => Airhorn(),
        SoundEffect.Applause => Applause(),
        SoundEffect.DrumRoll => DrumRoll(),
        SoundEffect.Rimshot => Rimshot(),
        SoundEffect.Crickets => Crickets(),
        _ => [],
    };

    private static short[] Buffer(double seconds) =>
        new short[(int)(AudioFormat.SampleRate * seconds)];

    /// <summary>
    /// Two detuned sawtooths a fifth apart, which is what makes an airhorn sound
    /// like a horn rather than a beep: the harshness is all in the harmonics a
    /// sawtooth has and a sine does not.
    /// </summary>
    private static short[] Airhorn()
    {
        var samples = Buffer(1.4);
        var random = new Random(1);

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;

            // Fast attack, long decay, and a small dip at the end as it runs out
            // of air.
            var envelope = Math.Min(1, t / 0.02) * Math.Exp(-t * 1.1);

            var value = (Saw(t, 233) + Saw(t, 349.6) + (Saw(t, 235.5) * 0.8)) / 3.0;

            // A trace of noise stops it sounding synthetic.
            value += (random.NextDouble() - 0.5) * 0.03;

            samples[i] = AudioFormat.ToPcm((float)(value * envelope * 0.7));
        }

        return samples;
    }

    /// <summary>
    /// Filtered noise with a lot of little peaks: a crowd is hundreds of claps,
    /// and each clap is a burst of noise, so shaped noise is not an approximation
    /// of applause — it is very nearly the real mechanism.
    /// </summary>
    private static short[] Applause()
    {
        var samples = Buffer(2.5);
        var random = new Random(2);
        var previous = 0.0;

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;

            // Swells in, holds, dies away.
            var envelope = Math.Min(1, t / 0.25) * Math.Min(1, (2.5 - t) / 0.6);

            var noise = (random.NextDouble() * 2) - 1;

            // Individual claps on top of the wash. Measured: at a multiplier of
            // 6 this pinned every peak at full scale, so the clip was clipping
            // rather than merely loud — the claps that are meant to stand out
            // were being flattened into the wash with everything else.
            if (random.NextDouble() < 0.0016)
            {
                noise *= 3.2;
            }

            // One-pole low pass, so it is a crowd rather than static.
            previous += 0.45 * (noise - previous);

            samples[i] = AudioFormat.ToPcm((float)(previous * envelope * 0.45));
        }

        return samples;
    }

    /// <summary>Noise bursts accelerating into a cymbal-ish finish.</summary>
    private static short[] DrumRoll()
    {
        var samples = Buffer(2.2);
        var random = new Random(3);
        var previous = 0.0;

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;

            // Hits get closer together as the roll builds.
            var rate = 14 + (t * 12);
            var phase = t * rate % 1.0;
            var hit = Math.Exp(-phase * 26);

            var noise = (random.NextDouble() * 2) - 1;
            previous += 0.6 * (noise - previous);

            var value = previous * hit;

            // The crash at the end.
            if (t > 2.0)
            {
                value += ((random.NextDouble() * 2) - 1) * Math.Exp(-(t - 2.0) * 9) * 0.7;
            }

            samples[i] = AudioFormat.ToPcm((float)(value * 0.55));
        }

        return samples;
    }

    /// <summary>The ba-dum-tss: two drum hits and a cymbal.</summary>
    private static short[] Rimshot()
    {
        var samples = Buffer(1.1);
        var random = new Random(4);

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;
            var value = 0.0;

            // Two toms, a tenth of a second apart, the second one lower.
            value += Drum(t, 0.00, 180);
            value += Drum(t, 0.16, 140);

            // Cymbal: bright noise, decaying slowly.
            if (t > 0.32)
            {
                value += ((random.NextDouble() * 2) - 1) * Math.Exp(-(t - 0.32) * 7) * 0.5;
            }

            samples[i] = AudioFormat.ToPcm((float)(value * 0.6));
        }

        return samples;

        static double Drum(double t, double at, double hz)
        {
            if (t < at)
            {
                return 0;
            }

            var local = t - at;

            // The pitch falls as it decays, which is what a struck drum does.
            var frequency = hz * Math.Exp(-local * 5);
            return Math.Sin(2 * Math.PI * frequency * local) * Math.Exp(-local * 12);
        }
    }

    /// <summary>
    /// The sound of nobody laughing: chirps with long gaps.
    ///
    /// A cricket is a fast trill, so each chirp is a tone pulsed several times
    /// rather than one steady note.
    /// </summary>
    private static short[] Crickets()
    {
        var samples = Buffer(3.0);

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / AudioFormat.SampleRate;

            // One chirp every 0.75 s, each lasting a fraction of that.
            var phase = t % 0.75;
            if (phase > 0.18)
            {
                continue;
            }

            // Trill: the tone is gated on and off quickly inside the chirp.
            var trill = Math.Sin(2 * Math.PI * 38 * phase) > 0 ? 1.0 : 0.0;
            var envelope = Math.Min(1, phase / 0.01) * Math.Min(1, (0.18 - phase) / 0.05);

            var value = Math.Sin(2 * Math.PI * 4400 * t) * trill * envelope;
            samples[i] = AudioFormat.ToPcm((float)(value * 0.22));
        }

        return samples;
    }

    private static double Saw(double t, double hz)
    {
        var phase = t * hz % 1.0;
        return (phase * 2) - 1;
    }
}
