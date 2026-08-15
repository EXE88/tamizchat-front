using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TamizChat.Audio;

/// <summary>
/// Turns a music file into the one thing a bot can play: Ogg/Opus.
///
/// A bot publishes into LiveKit as a participant, and WebRTC carries Opus. An
/// Ogg/Opus file already holds exactly those packets, so the server hands them
/// over untouched — nothing on the server decodes or re-encodes anything, and
/// there is no rate to guess at. The price is that the conversion has to happen
/// somewhere, and here is the right somewhere: Windows already decodes mp3, m4a
/// and wma, and this app already carries an Opus encoder for the microphone.
/// </summary>
public static class OpusFile
{
    /// <summary>What can be picked and converted.</summary>
    public static readonly string[] SupportedExtensions =
        [".mp3", ".ogg", ".opus", ".wav", ".m4a", ".aac", ".wma", ".flac"];

    private const int SampleRate = 48000;
    private const int Channels = 2;

    /// <summary>
    /// Music is not speech, so the encoder is told so and given a bitrate that
    /// sounds like a stream rather than a phone call.
    /// </summary>
    private const int Bitrate = 128000;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> as
    /// Ogg/Opus and returns the path written.
    ///
    /// A file that is already Opus is copied rather than re-encoded: going
    /// through the decoder and back would cost quality for nothing.
    /// </summary>
    public static async Task<string> ConvertAsync(string source, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsOpus(source))
        {
            await using var input = File.OpenRead(source);
            await using var copy = File.Create(destination);
            await input.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
            return destination;
        }

        await Task.Run(() => Encode(source, destination, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return destination;
    }

    /// <summary>True when the file is already Opus inside Ogg.</summary>
    public static bool IsOpus(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".opus", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var probe = File.OpenRead(path);
            var header = new byte[64];
            var read = probe.Read(header, 0, header.Length);

            // The codec is named in the first packet: "OpusHead" for Opus. A
            // Vorbis file wears the same .ogg extension and has to be converted
            // like anything else.
            return System.Text.Encoding.ASCII.GetString(header, 0, read)
                .Contains("OpusHead", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Encode(string source, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var reader = Open(source);

        // 48 kHz stereo, because that is what Opus wants and what music
        // deserves — the voice path is mono, but a bot is playing records.
        var samples = new WdlResamplingSampleProvider(Stereo(reader.ToSampleProvider()), SampleRate);

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = Bitrate;

        using var output = File.Create(destination);
        var writer = new OpusOggWriteStream(encoder, output);

        // 20 ms at a time, which is the frame size the rest of the system uses.
        var frame = new float[SampleRate / 50 * Channels];
        var pcm = new short[frame.Length];

        var total = TotalSamples(reader);
        long written = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = samples.Read(frame, 0, frame.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                pcm[i] = (short)Math.Clamp(frame[i] * short.MaxValue, short.MinValue, short.MaxValue);
            }

            // A short final read is padded with silence: Opus frames are a fixed
            // size, and a partial one would be refused.
            for (var i = read; i < pcm.Length; i++)
            {
                pcm[i] = 0;
            }

            writer.WriteSamples(pcm, 0, pcm.Length);
            written += read;

            if (total > 0)
            {
                progress?.Report(Math.Min(100, written * 100.0 / total));
            }
        }

        writer.Finish();
        progress?.Report(100);
    }

    /// <summary>
    /// Opens whatever Windows can decode.
    ///
    /// Not `AudioFileReader`, convenient as it is: that type lives in the NAudio
    /// meta-package, which drags in WinForms. `WaveFileReader` is in NAudio.Core
    /// and `MediaFoundationReader` in NAudio.Wasapi, both of which this project
    /// already references.
    /// </summary>
    private static WaveStream Open(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return new WaveFileReader(path);
        }

        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            // Vorbis: Media Foundation does not read it, but this app already
            // decodes it for its own sound effects.
            return new RawSourceWaveStream(
                new MemoryStream(ToBytes(AudioClip.Load(path))),
                new WaveFormat(SampleRate, 16, 1));
        }

        return new MediaFoundationReader(path);
    }

    private static ISampleProvider Stereo(ISampleProvider provider) => provider.WaveFormat.Channels switch
    {
        Channels => provider,
        1 => new MonoToStereoSampleProvider(provider),

        // Anything else — 5.1, for instance — is folded down rather than
        // refused: a bot playing a film soundtrack in stereo is fine.
        _ => new StereoToMonoSampleProvider(provider).ToStereo(),
    };

    private static ISampleProvider ToStereo(this ISampleProvider provider) =>
        new MonoToStereoSampleProvider(provider);

    /// <summary>How many samples the whole file holds, for the progress figure.</summary>
    private static long TotalSamples(WaveStream reader)
    {
        try
        {
            return (long)(reader.TotalTime.TotalSeconds * SampleRate * Channels);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static byte[] ToBytes(short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
