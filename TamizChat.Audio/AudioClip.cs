using NAudio.Wave;
using NVorbis;

namespace TamizChat.Audio;

/// <summary>
/// Reads a sound file off disk and converts it to the one format everything
/// downstream speaks: 48 kHz, mono, signed 16-bit.
///
/// Doing the conversion once at load time rather than during playback is what
/// keeps the mixing path trivial — by the time a clip is played it is already
/// exactly the shape of the frames the capture path produces.
///
/// Four codecs are supported. `.ogg` is a container, so what is inside it is
/// detected from the bytes: Opus goes through Concentus, Vorbis through NVorbis.
/// WAV and MP3 go through NAudio, so a user adding their own file is not forced
/// to convert it first.
/// </summary>
public static class AudioClip
{
    /// <summary>Everything the file picker should offer.</summary>
    public static readonly string[] SupportedExtensions = [".ogg", ".wav", ".mp3"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a file as 48 kHz mono PCM.
    ///
    /// Throws if the file is missing, unreadable or in a format we do not
    /// handle. The caller decides what to tell the user — a clip that will not
    /// load is a normal thing for a person to do by accident, not a crash.
    /// </summary>
    public static short[] Load(string path)
    {
        var extension = Path.GetExtension(path);

        return extension.ToLowerInvariant() switch
        {
            ".ogg" => LoadOgg(path),
            ".wav" or ".mp3" => LoadWithNAudio(path),
            _ => throw new NotSupportedException($"{extension} is not a sound format TamizChat can read"),
        };
    }

    /// <summary>
    /// An `.ogg` file is a container, and what is inside it matters.
    ///
    /// Vorbis and Opus both ship in Ogg and neither decoder reads the other's
    /// stream. The bundled clips turned out to be Opus even though everyone
    /// involved assumed Vorbis, so the format is detected from the bytes rather
    /// than believed from the extension — the same rule the backend already
    /// applies to uploaded files.
    /// </summary>
    private static short[] LoadOgg(string path)
    {
        using (var probe = File.OpenRead(path))
        {
            // The codec is named in the first packet, right after the Ogg page
            // header: "OpusHead" for Opus, "\x01vorbis" for Vorbis.
            var header = new byte[64];
            var read = probe.Read(header, 0, header.Length);
            var text = System.Text.Encoding.ASCII.GetString(header, 0, read);

            if (text.Contains("OpusHead", StringComparison.Ordinal))
            {
                return LoadOpus(path);
            }
        }

        return LoadVorbis(path);
    }

    private static short[] LoadOpus(string path)
    {
        using var file = File.OpenRead(path);

        // Opus is always 48 kHz internally, which is exactly the rate everything
        // here works at — so this is the one format that needs no resampling.
        var decoder = new Concentus.Structs.OpusDecoder(AudioFormat.SampleRate, 2);
        var reader = new Concentus.Oggfile.OpusOggReadStream(decoder, file);

        var samples = new List<short>();
        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is not null)
            {
                samples.AddRange(packet);
            }
        }

        // The decoder was asked for stereo, so the result is interleaved.
        var interleaved = new float[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            interleaved[i] = samples[i] / 32768f;
        }

        return Finish(interleaved, 2, AudioFormat.SampleRate);
    }

    private static short[] LoadVorbis(string path)
    {
        using var reader = new VorbisReader(path);

        var channels = reader.Channels;
        var rate = reader.SampleRate;

        // Vorbis decodes to float, which is already what the resampler wants.
        var interleaved = new float[reader.TotalSamples * channels];
        var total = 0;

        var block = new float[channels * 4096];
        int read;
        while ((read = reader.ReadSamples(block, 0, block.Length)) > 0)
        {
            var room = Math.Min(read, interleaved.Length - total);
            if (room <= 0)
            {
                break;
            }

            Array.Copy(block, 0, interleaved, total, room);
            total += room;
        }

        return Finish(interleaved.AsSpan(0, total), channels, rate);
    }

    private static short[] LoadWithNAudio(string path)
    {
        // Not `AudioFileReader`, convenient as it is: that type lives in the
        // NAudio meta-package, which drags in WinForms. `WaveFileReader` is in
        // NAudio.Core and `MediaFoundationReader` in NAudio.Wasapi, both of
        // which this project already references — so MP3 costs no new
        // dependency at all.
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);

        // Whatever the file was, present it as float samples so every format
        // meets Vorbis in the same place.
        var floats = reader.ToSampleProvider();

        var channels = reader.WaveFormat.Channels;
        var rate = reader.WaveFormat.SampleRate;

        var samples = new List<float>();
        var block = new float[channels * 4096];

        int read;
        while ((read = floats.Read(block, 0, block.Length)) > 0)
        {
            samples.AddRange(block.AsSpan(0, read).ToArray());
        }

        return Finish(samples.ToArray(), channels, rate);
    }

    /// <summary>Downmix to mono, resample to 48 kHz, convert to PCM16.</summary>
    private static short[] Finish(ReadOnlySpan<float> interleaved, int channels, int rate)
    {
        var mono = AudioFormat.ToMono(interleaved, channels);

        if (rate != AudioFormat.SampleRate)
        {
            // The resampler is stateful across calls because the capture path
            // feeds it a frame at a time; here the whole clip is one call, so
            // the state is created and thrown away.
            var position = 0.0;
            var carry = 0f;
            mono = AudioFormat.Resample(mono, rate, AudioFormat.SampleRate, ref position, ref carry);
        }

        var pcm = new short[mono.Length];
        AudioFormat.ToPcm16(mono, pcm);
        return pcm;
    }
}
