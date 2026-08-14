using TamizChat.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TamizChat.Core.Protocol;
using TamizChat.Services;
using Windows.Foundation;
using Windows.UI;

namespace TamizChat.Pages;

/// <summary>
/// The shared board.
///
/// Strokes are streamed rather than sent when finished: begin, append, append,
/// end. A drawing has to appear as it is drawn, or the room feels laggy even
/// when it is not.
///
/// Coordinates on the wire are normalized 0..1, never pixels, so a drawing lands
/// in the same place on a window of any size.
/// </summary>
public sealed partial class PaintPage : Page
{
    private static readonly string[] Palette =
        ["#FFFFFF", "#E5484D", "#F5A524", "#C6FF34", "#3E9BFF", "#D2C3F6", "#171717"];

    /// <summary>Points closer than this to the last one are not worth a frame.</summary>
    private const double MinPointGap = 0.004;

    private readonly Dictionary<string, Polyline> _lines = [];

    /// <summary>
    /// The board as the server sees it, in normalized coordinates. This is the
    /// authority; the polylines are only its pixel projection.
    ///
    /// Keeping it means a resize re-projects what is already known instead of
    /// re-fetching. Re-fetching raced with itself — several size changes fire
    /// during a single page load, and an older reply landing last would clear the
    /// canvas and draw a stale, sometimes empty, board.
    /// </summary>
    private readonly Dictionary<string, Stroke> _strokes = [];

    private readonly List<PaintPoint> _pending = [];
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private Canvas _canvas = null!;
    private string _color = "#C6FF34";
    private string? _activeStrokeId;
    private bool _drawing;
    private PaintPoint? _last;

    public PaintPage()
    {
        InitializeComponent();
        Translate();

        BuildCanvas();
        BuildSwatches();

        _flush.Tick += async (_, _) => await FlushAsync();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void BuildCanvas()
    {
        // A transparent background still receives pointer input; null would not.
        // A transparent background still receives pointer input; null would not.
        _canvas = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _canvas.SizeChanged += (_, _) => Reproject();
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += OnPointerReleased;
        BoardHost.Children.Add(_canvas);
    }

    private void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Parse(hex)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["TcBorderBrush"],
                Tag = hex,
            };

            swatch.Click += (s, _) =>
            {
                _color = (string)((Button)s).Tag;
                EraserToggle.IsChecked = false;
                ShowStatus();
            };

            Swatches.Children.Add(swatch);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var session = ServerSession.Instance;
        session.StrokeStarted += OnStrokeStarted;
        session.StrokeAppended += OnStrokeAppended;
        session.StrokeEnded += OnStrokeEnded;
        session.StrokeUndone += OnStrokeUndone;
        session.BoardCleared += OnBoardCleared;

        _flush.Start();
        ShowStatus();

        // Newcomers ask for the board; the server never pushes it, because a busy
        // board is large and most people never open this page.
        try
        {
            var state = await session.GetBoardAsync();
            foreach (var stroke in state.Strokes)
            {
                Draw(stroke);
            }

            ShowStatus(state.Strokes.Count, state.MaxStrokes);
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"Could not load the board: {ex.Message}";
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var session = ServerSession.Instance;
        session.StrokeStarted -= OnStrokeStarted;
        session.StrokeAppended -= OnStrokeAppended;
        session.StrokeEnded -= OnStrokeEnded;
        session.StrokeUndone -= OnStrokeUndone;
        session.BoardCleared -= OnBoardCleared;
        _flush.Stop();
    }

    // --- drawing by hand ---

    private async void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ServerSession.Instance.IsConnected || ServerSession.Instance.MyRoom is null)
        {
            StatusLine.Text = Loc.Get("Paint.JoinToDraw");
            return;
        }

        _canvas.CapturePointer(e.Pointer);
        _drawing = true;
        _last = Normalize(e.GetCurrentPoint(_canvas).Position);

        var erasing = EraserToggle.IsChecked == true;

        var begin = new PaintBegin
        {
            Tool = erasing ? "eraser" : "pen",
            Color = _color,

            // An eraser the same width as the pen never feels like it is
            // erasing, because it only just covers the line it is chasing.
            Width = WidthSlider.Value * (erasing ? 2.5 : 1.0) / Math.Max(1, _canvas.ActualWidth),
            Points = [_last],
        };

        try
        {
            var stroke = await ServerSession.Instance.BeginStrokeAsync(begin);
            if (stroke is null)
            {
                _drawing = false;
                return;
            }

            _activeStrokeId = stroke.Id;
            Draw(stroke);
        }
        catch (Exception ex)
        {
            _drawing = false;
            StatusLine.Text = ex is TamizChatProtocolException protocolError
                ? protocolError.ServerMessage
                : ex.Message;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || _activeStrokeId is null)
        {
            return;
        }

        var point = Normalize(e.GetCurrentPoint(_canvas).Position);
        if (_last is not null && Distance(_last, point) < MinPointGap)
        {
            return;
        }

        _last = point;
        _pending.Add(point);

        // Drawn locally straight away; the server copy catches up on the next
        // flush. Waiting for the round trip is what makes drawing feel sluggish.
        AppendLocal(_activeStrokeId, [point]);
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || _activeStrokeId is null)
        {
            return;
        }

        _canvas.ReleasePointerCapture(e.Pointer);
        _drawing = false;

        await FlushAsync();

        var id = _activeStrokeId;
        _activeStrokeId = null;
        _last = null;

        try
        {
            await ServerSession.Instance.EndStrokeAsync(id);
        }
        catch (Exception)
        {
            // The stroke is already on everyone's board.
        }
    }

    /// <summary>
    /// Sends the points gathered since the last tick. Batching keeps a fast
    /// scribble from becoming hundreds of frames a second.
    /// </summary>
    private async Task FlushAsync()
    {
        if (_pending.Count == 0 || _activeStrokeId is null)
        {
            return;
        }

        var batch = _pending.ToList();
        _pending.Clear();

        try
        {
            await ServerSession.Instance.AppendStrokeAsync(_activeStrokeId, batch);
        }
        catch (Exception)
        {
            // Dropped points are not worth interrupting the drawing over.
        }
    }

    // --- other people's strokes ---

    private void OnStrokeStarted(object? sender, Stroke stroke) => Draw(stroke);

    private void OnStrokeAppended(object? sender, PaintAppend append) =>
        AppendLocal(append.StrokeId, append.Points);

    private void OnStrokeEnded(object? sender, PaintEnd end)
    {
        // Nothing to do visually: the line is already complete on screen.
    }

    private void OnStrokeUndone(object? sender, PaintUndo undo) => Remove(undo.StrokeId);

    private void OnBoardCleared(object? sender, PaintCleared cleared)
    {
        if (cleared.Scope == "all")
        {
            _canvas.Children.Clear();
            _lines.Clear();
            ShowStatus();
            return;
        }

        // "mine" is scoped to whoever cleared, so only their lines go.
        foreach (var id in _lines
                     .Where(pair => (string?)pair.Value.Tag == cleared.By)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            Remove(id);
        }
    }

    // --- rendering ---

    private void Draw(Stroke stroke)
    {
        if (_lines.ContainsKey(stroke.Id))
        {
            return;
        }

        _strokes[stroke.Id] = stroke;
        AddLine(stroke);
    }

    private void AddLine(Stroke stroke)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Parse(stroke.Color)),
            StrokeThickness = Math.Max(1, stroke.Width * _canvas.ActualWidth),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = stroke.Author,
        };

        // The eraser paints the board's own colour rather than removing points,
        // which keeps every stroke a simple append-only line. The brush must be
        // the opaque board colour, not the translucent surface one, or it tints
        // what is underneath instead of covering it.
        //
        // It is resolved per client, so an eraser stroke drawn under one theme
        // still erases correctly for someone using another.
        if (stroke.Tool == "eraser")
        {
            line.Stroke = (Brush)Application.Current.Resources["TcBoardBrush"];
        }

        foreach (var point in stroke.Points)
        {
            line.Points.Add(ToPixels(point));
        }

        _lines[stroke.Id] = line;
        _canvas.Children.Add(line);
    }

    private void AppendLocal(string strokeId, IReadOnlyList<PaintPoint> points)
    {
        if (!_lines.TryGetValue(strokeId, out var line))
        {
            return;
        }

        // Both copies: the normalized one survives a resize, the pixel one is
        // what is on screen right now.
        if (_strokes.TryGetValue(strokeId, out var stroke))
        {
            stroke.Points.AddRange(points);
        }

        foreach (var point in points)
        {
            line.Points.Add(ToPixels(point));
        }
    }

    private void Remove(string strokeId)
    {
        _strokes.Remove(strokeId);
        if (_lines.Remove(strokeId, out var line))
        {
            _canvas.Children.Remove(line);
        }
    }

    /// <summary>
    /// Rebuilds every line after a resize, from the normalized copy we already
    /// hold. No network call: the board has not changed, only its projection.
    /// </summary>
    private void Reproject()
    {
        if (_canvas.ActualWidth <= 0 || _strokes.Count == 0)
        {
            return;
        }

        _canvas.Children.Clear();
        _lines.Clear();

        foreach (var stroke in _strokes.Values.OrderBy(s => s.Seq))
        {
            AddLine(stroke);
        }
    }

    private Point ToPixels(PaintPoint point) =>
        new(point.X * _canvas.ActualWidth, point.Y * _canvas.ActualHeight);

    private PaintPoint Normalize(Point point) => new()
    {
        X = Math.Clamp(point.X / Math.Max(1, _canvas.ActualWidth), 0, 1),
        Y = Math.Clamp(point.Y / Math.Max(1, _canvas.ActualHeight), 0, 1),
    };

    private static double Distance(PaintPoint a, PaintPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static Color Parse(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    // --- toolbar ---

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServerSession.Instance.UndoStrokeAsync();
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    private async void OnClearMineClick(object sender, RoutedEventArgs e) => await ClearAsync("mine");

    private async void OnClearAllClick(object sender, RoutedEventArgs e) => await ClearAsync("all");

    private async Task ClearAsync(string scope)
    {
        try
        {
            await ServerSession.Instance.ClearBoardAsync(scope);
        }
        catch (TamizChatProtocolException ex)
        {
            // "all" destroys other people's work, so the server refuses it without
            // moderation rights.
            StatusLine.Text = ex.ServerMessage;
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    private void ShowStatus(int? strokes = null, int? max = null)
    {
        var room = ServerSession.Instance.MyRoom;
        var where = room is null ? "Not in a room" : $"In {room.Name}";
        var count = strokes is null ? "" : $" · {strokes}/{max} strokes";
        StatusLine.Text = $"{where}{count}";
    }

    /// <summary>
    /// Reads this page's strings for the current language.
    ///
    /// Called from the constructor only. The language can be changed on the
    /// Settings page, and the frame builds a fresh instance of every page on
    /// navigation, so any page the user reaches afterwards is already correct.
    /// </summary>
    private void Translate()
    {
        TitleText.Text = Loc.Get("Paint.Title");
        UndoButton.Content = Loc.Get("Paint.Undo");
        ClearMineButton.Content = Loc.Get("Paint.ClearMine");
        ClearAllButton.Content = Loc.Get("Paint.ClearAll");
    }

}
