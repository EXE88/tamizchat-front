using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace TamizChat.Video;

/// <summary>
/// A webcam, delivered as BGRA frames.
///
/// It asks the camera itself for BGRA rather than converting: every webcam
/// driver on Windows can produce it, and doing the conversion here would mean
/// unpacking NV12 or MJPEG by hand for no benefit.
/// </summary>
public sealed class CameraCapture : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;

    /// <summary>Raised for each frame, on a capture thread.</summary>
    public event EventHandler<VideoFrameBuffer>? FrameReady;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsRunning => _reader is not null;

    /// <summary>Whether this machine has a camera at all.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Count > 0;
    }

    /// <summary>
    /// Opens the first camera and starts reading.
    ///
    /// Throws if there is no camera or the user has denied access — the caller
    /// turns the toggle back off rather than pretending to broadcast.
    /// </summary>
    public async Task StartAsync()
    {
        if (_reader is not null)
        {
            return;
        }

        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("no camera is connected");
        }

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = devices[0].Id,

            // Video only: the microphone is opened by the audio path, and asking
            // for it twice can fail on devices that do not allow sharing.
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });

        var source = capture.FrameSources.Values.FirstOrDefault(s =>
            s.Info.MediaStreamType == MediaStreamType.VideoRecord
            || s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? throw new InvalidOperationException("the camera exposes no video stream");

        // The largest format no wider than 1280: a full-resolution webcam frame
        // costs bandwidth out of all proportion to how large anyone displays it.
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 1280)
            .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();

        if (format is not null)
        {
            await source.SetFormatAsync(format);
        }

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.FrameArrived += OnFrameArrived;
        await reader.StartAsync();

        _capture = capture;
        _reader = reader;
    }

    public async Task StopAsync()
    {
        var reader = _reader;
        _reader = null;

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch (Exception)
            {
                // A camera unplugged mid-call has already stopped.
            }

            reader.Dispose();
        }

        _capture?.Dispose();
        _capture = null;
        Width = 0;
        Height = 0;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }

        using (bitmap)
        {
            var sourceWidth = bitmap.PixelWidth;
            var sourceHeight = bitmap.PixelHeight;

            // `AsBuffer` was removed with the old WinRT interop layer, so the
            // pixels come back through a WinRT buffer and a DataReader. That is
            // all managed projection — no COM pointer work needed.
            var native = new Windows.Storage.Streams.Buffer((uint)(sourceWidth * sourceHeight * 4));
            bitmap.CopyToBuffer(native);

            var pixels = new byte[native.Length];
            Windows.Storage.Streams.DataReader.FromBuffer(native).ReadBytes(pixels);

            // Odd dimensions break the chroma subsampling every video codec does,
            // so the frame is cropped by a row or column when needed. Cropping
            // happens after the copy because the bitmap's own stride is what the
            // buffer is laid out at.
            var width = sourceWidth - (sourceWidth % 2);
            var height = sourceHeight - (sourceHeight % 2);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (width != sourceWidth || height != sourceHeight)
            {
                var cropped = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                {
                    Array.Copy(pixels, row * sourceWidth * 4, cropped, row * width * 4, width * 4);
                }

                pixels = cropped;
            }

            Width = width;
            Height = height;
            FrameReady?.Invoke(this, new VideoFrameBuffer(pixels, width, height));
        }
    }
}
