using System.Net;

namespace TamizChat.Services;

/// <summary>
/// Request body that says how far it has got.
///
/// `StreamContent` copies the stream in one call and reports nothing, so an
/// upload of a 30 MB track was a frozen dialog with no way to tell a slow
/// connection from a stuck one. This copies in chunks and reports after each,
/// which is enough for a progress bar and costs one extra callback per 80 KB.
/// </summary>
internal sealed class ProgressStreamContent : HttpContent
{
    private const int ChunkSize = 80 * 1024;

    private readonly Stream _source;
    private readonly long _length;
    private readonly IProgress<double>? _percent;
    private double _lastReported;

    public ProgressStreamContent(Stream source, long length, IProgress<double>? percent)
    {
        _source = source;
        _length = length;
        _percent = percent;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[ChunkSize];
        long sent = 0;

        while (true)
        {
            var read = await _source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            sent += read;

            // A zero-length file would divide by zero, and reporting anything
            // for one is meaningless anyway. Whole percentages only: each report
            // crosses to the interface thread, and nobody can see finer than
            // that on a progress bar.
            if (_length > 0)
            {
                var percent = Math.Min(100, sent * 100.0 / _length);
                if (percent - _lastReported >= 1)
                {
                    _lastReported = percent;
                    _percent?.Report(percent);
                }
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        // Known length means a Content-Length header rather than chunked
        // encoding, which is what lets the server enforce its size limit before
        // reading the body.
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}
