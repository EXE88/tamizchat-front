using System.Runtime.InteropServices;

namespace TamizChat.Video;

/// <summary>
/// The primary monitor, grabbed repeatedly as BGRA frames.
///
/// This uses GDI's BitBlt into a DIB section, which hands back a pointer to the
/// pixels directly, rather than Windows.Graphics.Capture. That newer API is the
/// better one — it is hardware accelerated and can capture a single window — but
/// it returns Direct3D surfaces, so using it means a D3D11 device, staging
/// textures and a copy back to the CPU before LiveKit can see a byte. BitBlt
/// gets screen sharing working now; moving to Graphics.Capture is a contained
/// change behind this class, because the output shape is the same either way.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private const int SrcCopy = 0x00CC0020;
    private const int PwRenderFullContent = 0x00000002;

    private readonly object _gate = new();
    private CancellationTokenSource? _running;

    private ShareTarget? _target;
    private int _originX;
    private int _originY;

    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _pixels;

    /// <summary>Raised for each captured frame, off the UI thread.</summary>
    public event EventHandler<VideoFrameBuffer>? FrameReady;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsRunning => _running is not null;

    /// <summary>
    /// Starts capturing at roughly the requested rate.
    ///
    /// Fifteen a second by default: screen content is mostly still, and the cost
    /// of a full-screen copy is high enough that chasing 30 buys a lot of CPU for
    /// very little that anyone would notice.
    /// </summary>
    public void Start(ShareTarget target, int fps = 15)
    {
        if (_running is not null)
        {
            return;
        }

        _target = target;

        if (target.IsWindow)
        {
            GetWindowRect(target.Window, out var bounds);
            Width = bounds.Right - bounds.Left;
            Height = bounds.Bottom - bounds.Top;
            _originX = 0;
            _originY = 0;
        }
        else
        {
            // A monitor is captured from the desktop DC at its own offset, so a
            // second screen to the right of the first is not captured as the
            // first one all over again.
            Width = target.Width;
            Height = target.Height;
            _originX = target.X;
            _originY = target.Y;
        }

        // Odd dimensions break the chroma subsampling every video codec does.
        Width -= Width % 2;
        Height -= Height % 2;

        if (Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException("that window has nothing to capture");
        }

        _screenDc = GetDC(nint.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);

        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = Width,

            // Negative height asks for a top-down bitmap. Left positive, GDI
            // returns the rows bottom-up and the picture arrives upside down.
            Height = -Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        _bitmap = CreateDIBSection(_memoryDc, ref header, 0, out _pixels, nint.Zero, 0);
        SelectObject(_memoryDc, _bitmap);

        var cts = new CancellationTokenSource();
        _running = cts;

        _ = Task.Run(() => LoopAsync(Math.Max(1, fps), cts.Token), cts.Token);
    }

    public void Stop()
    {
        var cts = _running;
        _running = null;
        cts?.Cancel();
        cts?.Dispose();

        lock (_gate)
        {
            if (_bitmap != nint.Zero)
            {
                DeleteObject(_bitmap);
                _bitmap = nint.Zero;
            }

            if (_memoryDc != nint.Zero)
            {
                DeleteDC(_memoryDc);
                _memoryDc = nint.Zero;
            }

            if (_screenDc != nint.Zero)
            {
                ReleaseDC(nint.Zero, _screenDc);
                _screenDc = nint.Zero;
            }

            _pixels = nint.Zero;
            _target = null;
        }
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(int fps, CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        var buffer = new byte[Width * Height * 4];

        while (!token.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;

            lock (_gate)
            {
                if (_pixels == nint.Zero)
                {
                    return;
                }

                if (_target?.IsWindow == true)
                {
                    // PrintWindow asks the window to draw itself, so it works
                    // even when the window is behind another one. The flag is
                    // what makes it work for hardware-composed content — without
                    // it, modern apps come back blank.
                    PrintWindow(_target.Window, _memoryDc, PwRenderFullContent);
                }
                else
                {
                    // Without CAPTUREBLT. That flag pulls in layered windows but
                    // forces a full desktop composition on every call, so it is
                    // the expensive option and this path is already the slow one.
                    //
                    // Measured honestly: dropping it moved a subscriber from
                    // about 4.3 to 5.2 frames a second, so it was *not* the main
                    // cost. See MEMORY for the open question.
                    BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _originX, _originY, SrcCopy);
                }
                Marshal.Copy(_pixels, buffer, 0, buffer.Length);
            }

            FrameReady?.Invoke(this, new VideoFrameBuffer(buffer, Width, Height));

            var elapsed = DateTime.UtcNow - started;
            if (elapsed < interval)
            {
                await Task.Delay(interval - elapsed, token).ConfigureAwait(false);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out ShareTargets.Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int srcX, int srcY, int rop);
}

/// <summary>One frame of BGRA pixels, with the size they are laid out at.</summary>
public sealed class VideoFrameBuffer(byte[] bgra, int width, int height)
{
    public byte[] Bgra { get; } = bgra;

    public int Width { get; } = width;

    public int Height { get; } = height;
}
