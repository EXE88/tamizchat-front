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
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _scaling;

    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _pixels;
    private nint _sourceDc;
    private nint _sourceBitmap;

    /// <summary>Raised for each captured frame, off the UI thread.</summary>
    public event EventHandler<VideoFrameBuffer>? FrameReady;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsRunning => _running is not null;

    /// <summary>
    /// Whether the mouse pointer is drawn into the picture.
    ///
    /// GDI does not include it: BitBlt copies the desktop's pixels and the
    /// cursor is drawn by the compositor on top of them, so a share without this
    /// shows a screen where nothing is ever being pointed at. Anyone
    /// demonstrating something is pointing at it, so this is on by default.
    /// </summary>
    public bool DrawCursor { get; set; } = true;

    /// <summary>
    /// The tallest the frame is published at; 0 sends it at the source's own
    /// size. A 4K screen sent whole is four times the pixels of 1080p for
    /// detail nobody can see in a room tile.
    /// </summary>
    public int MaxHeight { get; set; }

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
            _sourceWidth = bounds.Right - bounds.Left;
            _sourceHeight = bounds.Bottom - bounds.Top;
            _originX = bounds.Left;
            _originY = bounds.Top;
        }
        else
        {
            // A monitor is captured from the desktop DC at its own offset, so a
            // second screen to the right of the first is not captured as the
            // first one all over again.
            _sourceWidth = target.Width;
            _sourceHeight = target.Height;
            _originX = target.X;
            _originY = target.Y;
        }

        Width = _sourceWidth;
        Height = _sourceHeight;

        // Scaled down to the chosen ceiling, keeping the shape. A big screen
        // sent whole costs bandwidth for detail that is thrown away again by
        // the size of the tile it lands in.
        if (MaxHeight > 0 && Height > MaxHeight)
        {
            Width = (int)Math.Round(Width * (double)MaxHeight / Height);
            Height = MaxHeight;
        }

        // Odd dimensions break the chroma subsampling every video codec does.
        Width -= Width % 2;
        Height -= Height % 2;
        _sourceWidth -= _sourceWidth % 2;
        _sourceHeight -= _sourceHeight % 2;

        if (Width <= 0 || Height <= 0 || _sourceWidth <= 0 || _sourceHeight <= 0)
        {
            throw new InvalidOperationException("that window has nothing to capture");
        }

        _scaling = Width != _sourceWidth || Height != _sourceHeight;

        _screenDc = GetDC(nint.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateSurface(_memoryDc, Width, Height, out _pixels);
        SelectObject(_memoryDc, _bitmap);

        if (_scaling)
        {
            // The picture is grabbed at its real size first and shrunk
            // afterwards. Asking GDI to stretch straight out of the screen DC
            // works for a monitor but not for a window: PrintWindow draws the
            // window at its own size and takes no scale, so there has to be a
            // surface that size for it to draw into either way.
            _sourceDc = CreateCompatibleDC(_screenDc);
            _sourceBitmap = CreateSurface(_sourceDc, _sourceWidth, _sourceHeight, out _);
            SelectObject(_sourceDc, _sourceBitmap);
        }

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

            if (_sourceBitmap != nint.Zero)
            {
                DeleteObject(_sourceBitmap);
                _sourceBitmap = nint.Zero;
            }

            if (_sourceDc != nint.Zero)
            {
                DeleteDC(_sourceDc);
                _sourceDc = nint.Zero;
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

                // Whichever surface is the full-size one: the output directly
                // when nothing is being scaled, otherwise the intermediate.
                var canvas = _scaling ? _sourceDc : _memoryDc;

                if (_target?.IsWindow == true)
                {
                    // PrintWindow asks the window to draw itself, so it works
                    // even when the window is behind another one. The flag is
                    // what makes it work for hardware-composed content — without
                    // it, modern apps come back blank.
                    PrintWindow(_target.Window, canvas, PwRenderFullContent);
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
                    BitBlt(canvas, 0, 0, _sourceWidth, _sourceHeight, _screenDc, _originX, _originY, SrcCopy);
                }

                if (DrawCursor)
                {
                    // Drawn at full size, before any shrinking, so the pointer
                    // scales with everything else instead of staying a fixed
                    // number of pixels and looking enormous on a scaled share.
                    CompositeCursor(canvas);
                }

                if (_scaling)
                {
                    // Halftone averages the pixels it drops. The default mode
                    // throws them away, which turns text into a sieve — and text
                    // is most of what anybody shares a screen to show.
                    SetStretchBltMode(_memoryDc, StretchHalftone);
                    SetBrushOrgEx(_memoryDc, 0, 0, nint.Zero);
                    StretchBlt(
                        _memoryDc, 0, 0, Width, Height,
                        _sourceDc, 0, 0, _sourceWidth, _sourceHeight, SrcCopy);
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

    /// <summary>
    /// Creates a top-down 32-bit surface and hands back a pointer to its pixels.
    /// </summary>
    private static nint CreateSurface(nint dc, int width, int height, out nint pixels)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,

            // Negative height asks for a top-down bitmap. Left positive, GDI
            // returns the rows bottom-up and the picture arrives upside down.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        return CreateDIBSection(dc, ref header, 0, out pixels, nint.Zero, 0);
    }

    /// <summary>
    /// Paints the mouse pointer into the captured surface.
    ///
    /// GDI does not capture it. The cursor is not part of the desktop's pixels —
    /// it is composited on top by the system — so a share taken with BitBlt
    /// shows a screen with nothing pointing at anything, and the person watching
    /// has no idea what is being talked about. That is worth one icon draw a
    /// frame.
    ///
    /// The hotspot is subtracted because the position Windows reports is the
    /// point the cursor addresses, not the top-left corner of its picture: an
    /// I-beam or a crosshair would otherwise sit noticeably off.
    /// </summary>
    private void CompositeCursor(nint dc)
    {
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };

        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == nint.Zero)
        {
            return;
        }

        if (!GetIconInfo(info.Cursor, out var icon))
        {
            return;
        }

        try
        {
            var x = info.X - _originX - icon.HotspotX;
            var y = info.Y - _originY - icon.HotspotY;

            // Off this surface entirely — another monitor, or outside the shared
            // window — so there is nothing to draw.
            if (x > _sourceWidth || y > _sourceHeight || x < -128 || y < -128)
            {
                return;
            }

            DrawIconEx(dc, x, y, info.Cursor, 0, 0, 0, nint.Zero, DiNormal);
        }
        finally
        {
            // GetIconInfo creates both bitmaps and hands ownership over. Sixteen
            // frames a second without this is a GDI leak that ends in the whole
            // process running out of handles.
            if (icon.MaskBitmap != nint.Zero)
            {
                DeleteObject(icon.MaskBitmap);
            }

            if (icon.ColorBitmap != nint.Zero)
            {
                DeleteObject(icon.ColorBitmap);
            }
        }
    }

    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;
    private const int StretchHalftone = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint Size;
        public uint Flags;
        public nint Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint point);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        nint dest, int x, int y, int w, int h,
        nint src, int srcX, int srcY, int srcW, int srcH, int rop);

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
