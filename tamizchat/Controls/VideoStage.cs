using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TamizChat.Core.Media;
using TamizChat.Localization;
using TamizChat.Services;
using Windows.Graphics.Imaging;

namespace TamizChat.Controls;

/// <summary>
/// One person's camera or screen, filling the window.
///
/// A shared screen inside a quarter of a room tile is a thumbnail of a
/// spreadsheet: the picture is technically there and nobody can read a word of
/// it. This is the way to actually look at it — click the tile and it takes over
/// the page, Escape or the close button puts it back.
///
/// It is a panel over the page rather than a separate window on purpose. A
/// second window would need its own title bar, its own theme plumbing and its
/// own place in Alt-Tab, and it would lose the bottom bar — which is where mute
/// lives, and muting while watching somebody's screen is exactly when people
/// reach for it.
/// </summary>
public sealed class VideoStage : Grid
{
    private readonly Image _image;
    private readonly SoftwareBitmapSource _source = new();
    private readonly TextBlock _caption;
    private readonly Button _close;

    private string _identity = "";
    private VideoKind _kind = VideoKind.Screen;
    private bool _busy;

    public VideoStage()
    {
        Visibility = Visibility.Collapsed;

        // Opaque, not translucent: this is a picture to look at, and anything
        // showing through it competes with the thing being shown.
        Background = (Brush)Application.Current.Resources["TcBoardBrush"];
        CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"];

        _image = new Image
        {
            Source = _source,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 44, 0, 0),
        };

        _caption = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        };

        _close = new Button
        {
            Content = Loc.Get("Video.Close"),
            Margin = new Thickness(0, 8, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _close.Click += (_, _) => Hide();

        Children.Add(_image);
        Children.Add(_caption);
        Children.Add(_close);

        // Escape only reaches here when something inside has focus, which is why
        // the close button is focused on opening. Double-clicking the picture is
        // the other way out, and the one people try first.
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };

        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };
    }

    /// <summary>Whose picture is on the stage, or empty when it is closed.</summary>
    public string Identity => Visibility == Visibility.Visible ? _identity : "";

    public void Show(string identity, VideoKind kind, string title)
    {
        _identity = identity;
        _kind = kind;
        _caption.Text = kind == VideoKind.Screen
            ? Loc.Get("Video.SharingScreen", title)
            : title;

        Visibility = Visibility.Visible;
        _close.Focus(FocusState.Programmatic);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _identity = "";
    }

    /// <summary>
    /// Takes a frame if it is the one being watched.
    ///
    /// Frames are dropped while the previous one is still being handed to XAML,
    /// the same rule the room cells use: queueing them builds a backlog the
    /// moment the interface thread is busy, and stale video is worse than fewer
    /// frames.
    /// </summary>
    public void SetVideoFrame(RemoteVideoFrame frame)
    {
        if (Visibility != Visibility.Visible
            || frame.Identity != _identity
            || frame.Kind != _kind
            || _busy
            || frame.Width <= 0
            || frame.Height <= 0)
        {
            return;
        }

        _busy = true;
        _ = ShowAsync(frame);
    }

    /// <summary>Closes when the track being watched stops; there is nothing left to show.</summary>
    public void ClearVideo(string identity, VideoKind kind)
    {
        if (Visibility == Visibility.Visible && identity == _identity && kind == _kind)
        {
            Hide();
        }
    }

    private async Task ShowAsync(RemoteVideoFrame frame)
    {
        try
        {
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(frame.Bgra);

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                writer.DetachBuffer(),
                BitmapPixelFormat.Bgra8,
                frame.Width,
                frame.Height,

                // XAML only displays premultiplied alpha; straight alpha throws.
                BitmapAlphaMode.Premultiplied);

            await _source.SetBitmapAsync(bitmap);
        }
        catch (Exception)
        {
            // A frame arriving as the stage closes is not worth reporting.
        }
        finally
        {
            _busy = false;
        }
    }
}
