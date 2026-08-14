namespace TamizChat.Audio;

/// <summary>
/// Conversions between whatever format a sound device wants and the one shape
/// LiveKit takes: 48 kHz, mono, 16-bit.
///
/// These are written out by hand rather than reached for through a resampler
/// library because the whole voice path has to stay ours: this is the same code
/// the voice changer and the soundboard will run through in F10, and a
/// conversion hidden inside somebody else's stream object is not somewhere DSP
/// can be inserted.
/// </summary>
internal static class AudioFormat
{
    /// <summary>
    /// Collapses interleaved float frames to mono by averaging the channels.
    ///
    /// Averaging rather than taking the first channel matters for the headsets
    /// that put the microphone on one side only — picking a channel there is a
    /// coin toss between the real signal and silence.
    /// </summary>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels == 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[(i * channels) + c];
            }

            mono[i] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Linear resampling, carrying the fractional read position across calls in
    /// <paramref name="position"/>.
    ///
    /// The state has to survive between buffers: restarting at zero each time
    /// leaves a discontinuity every buffer boundary, which is audible as a steady
    /// buzz at the buffer rate rather than as anything anyone would call
    /// distortion.
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate, ref double position, ref float carry)
    {
        if (fromRate == toRate)
        {
            if (input.Length > 0)
            {
                carry = input[^1];
            }

            return input.ToArray();
        }

        var step = (double)fromRate / toRate;
        var output = new List<float>((int)(input.Length / step) + 2);

        while (true)
        {
            var index = (int)Math.Floor(position);
            if (index >= input.Length)
            {
                break;
            }

            // Below zero means the interpolation still straddles the previous
            // buffer, which is what `carry` is holding.
            var a = index < 0 ? carry : input[index];
            var b = index + 1 < input.Length ? input[index + 1] : a;

            output.Add((float)(a + ((b - a) * (position - index))));
            position += step;
        }

        position -= input.Length;
        if (input.Length > 0)
        {
            carry = input[^1];
        }

        return [.. output];
    }

    /// <summary>
    /// The rate everything on the wire uses.
    ///
    /// Declared here as well as in the protocol client so the effects and the
    /// soundboard can compute in seconds without depending on Core — this
    /// assembly is the one both the app and the console simulator share.
    /// </summary>
    public const int SampleRate = 48000;

    /// <summary>One float in the range -1..1 to signed 16-bit, clipped not wrapped.</summary>
    public static short ToPcm(float value) => value switch
    {
        >= 1f => short.MaxValue,
        <= -1f => short.MinValue,
        _ => (short)(value * 32767f),
    };

    /// <summary>Float in the range -1..1 to signed 16-bit, clipped rather than wrapped.</summary>
    public static void ToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (var i = 0; i < source.Length && i < destination.Length; i++)
        {
            var scaled = source[i] * 32767f;

            // Wrapping instead of clipping turns a moment of loudness into a
            // full-scale sign flip, which sounds like a gunshot.
            destination[i] = scaled switch
            {
                >= 32767f => short.MaxValue,
                <= -32768f => short.MinValue,
                _ => (short)scaled,
            };
        }
    }

    public static float[] ToFloat(ReadOnlySpan<short> source)
    {
        var result = new float[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = source[i] / 32768f;
        }

        return result;
    }
}
