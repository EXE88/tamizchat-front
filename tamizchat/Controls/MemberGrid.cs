using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TamizChat.Core.Media;
using TamizChat.Core.Protocol;
using Windows.Graphics.Imaging;

namespace TamizChat.Controls;

/// <summary>
/// The people inside a room, each in their own cell.
///
/// Cells subdivide as people arrive, the same way rooms do on the server page.
/// A cell is not just a place to put an avatar — it is the frame a camera feed or
/// a shared screen will fill later, which is why the avatar sits centred in it
/// rather than the cells being sized to the avatars.
/// </summary>
public sealed class MemberGrid : Grid
{
    private const double Gap = 8;

    private readonly Dictionary<string, MemberCell> _cells = [];
    private IReadOnlyList<User> _members = [];
    private double _viewportWidth;
    private double _viewportHeight;

    public void SetMembers(IReadOnlyList<User> members)
    {
        _members = members;

        foreach (var gone in _cells.Keys.Except(members.Select(m => m.ClientUuid)).ToList())
        {
            Children.Remove(_cells[gone]);
            _cells.Remove(gone);
        }

        foreach (var member in members)
        {
            if (_cells.TryGetValue(member.ClientUuid, out var existing))
            {
                existing.Update(member);
                continue;
            }

            var cell = new MemberCell(member);
            _cells[member.ClientUuid] = cell;
            Children.Add(cell);
        }

        Arrange();
    }

    /// <summary>
    /// Rings whoever is talking.
    ///
    /// The identities come from LiveKit, which uses the client UUID, so they
    /// match the cell keys without any translation.
    /// </summary>
    public void SetSpeaking(IReadOnlyList<string> identities)
    {
        foreach (var (uuid, cell) in _cells)
        {
            cell.SetSpeaking(identities.Contains(uuid));
        }
    }

    /// <summary>
    /// Routes a video frame to whoever sent it. Frames for people in other rooms
    /// simply find no cell here, which is the right outcome.
    /// </summary>
    public void SetVideoFrame(RemoteVideoFrame frame)
    {
        if (_cells.TryGetValue(frame.Identity, out var cell))
        {
            cell.SetVideoFrame(frame);
        }
    }

    public void ClearVideo(string identity, VideoKind kind)
    {
        if (_cells.TryGetValue(identity, out var cell))
        {
            cell.ClearVideo(kind);
        }
    }

    /// <summary>
    /// The cells are a fraction of the visible area, so the grid has to be told
    /// how big that is — it cannot read it from itself once it is taller than the
    /// viewport and scrolling.
    /// </summary>
    public void SetViewport(double width, double height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        Arrange();
    }

    private void Arrange()
    {
        if (_members.Count == 0 || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            Width = Math.Max(0, _viewportWidth);
            Height = 0;
            return;
        }

        var slots = TileLayout.Arrange(_members.Count, _viewportWidth, _viewportHeight, Gap);

        for (var i = 0; i < _members.Count && i < slots.Length; i++)
        {
            if (!_cells.TryGetValue(_members[i].ClientUuid, out var cell))
            {
                continue;
            }

            var slot = slots[i];
            cell.HorizontalAlignment = HorizontalAlignment.Left;
            cell.VerticalAlignment = VerticalAlignment.Top;
            cell.Margin = new Thickness(slot.X, slot.Y, 0, 0);
            cell.Width = slot.Width;
            cell.Height = slot.Height;
            cell.Resize(slot.Width, slot.Height);
        }

        Width = _viewportWidth;
        Height = TileLayout.ContentHeight(_members.Count, _viewportHeight, Gap);
    }
}

/// <summary>One person's cell: their avatar and name, centred.</summary>
internal sealed class MemberCell : Grid
{
    private readonly AvatarView _avatar;
    private readonly TextBlock _name;
    private readonly StackPanel _stack;
    private readonly Image _video;
    private readonly SoftwareBitmapSource _videoSource = new();
    private User _member;
    private VideoKind? _videoKind;
    private bool _videoBusy;

    public MemberCell(User member)
    {
        _member = member;

        Background = (Brush)Application.Current.Resources["TcSurfaceHoverBrush"];
        CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"];

        _avatar = new AvatarView(member.Username)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _name = new TextBlock
        {
            Text = member.Username,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        _stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _stack.Children.Add(_avatar);
        _stack.Children.Add(_name);

        // Sits behind the avatar stack and takes over when video arrives. Uniform
        // keeps the aspect ratio: a stretched face and a stretched screen are
        // both immediately wrong to look at.
        _video = new Image
        {
            Source = _videoSource,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };

        Children.Add(_video);
        Children.Add(_stack);
        Update(member);
    }

    public void Update(User member)
    {
        _member = member;
        _name.Text = member.Username;
        _avatar.SetMuted(member.Muted);
    }

    public void SetSpeaking(bool speaking) => _avatar.IsSpeaking = speaking;

    /// <summary>
    /// Shows a frame of this person's camera or screen in place of their avatar.
    ///
    /// Frames are dropped while a previous one is still being handed to XAML.
    /// The alternative — queueing them — builds a backlog the moment the UI
    /// thread is busy, and stale video is worse than fewer frames.
    /// </summary>
    public void SetVideoFrame(RemoteVideoFrame frame)
    {
        if (_videoBusy || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        // Screen share wins over the camera: someone sharing their screen is
        // showing it for a reason.
        if (_videoKind == VideoKind.Screen && frame.Kind == VideoKind.Camera)
        {
            return;
        }

        _videoKind = frame.Kind;
        _videoBusy = true;

        _ = ShowAsync(frame);
    }

    /// <summary>Drops back to the avatar when a track stops.</summary>
    public void ClearVideo(VideoKind kind)
    {
        if (_videoKind != kind)
        {
            return;
        }

        _videoKind = null;
        _video.Visibility = Visibility.Collapsed;
        _stack.Visibility = Visibility.Visible;
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

                // XAML will only display premultiplied alpha; a straight-alpha
                // bitmap throws rather than looking wrong.
                BitmapAlphaMode.Premultiplied);

            await _videoSource.SetBitmapAsync(bitmap);

            _video.Visibility = Visibility.Visible;
            _stack.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // A frame arriving as the cell is torn down is not worth reporting.
        }
        finally
        {
            _videoBusy = false;
        }
    }

    /// <summary>
    /// Scales the avatar to the cell. A quarter-sized cell on a small tile would
    /// otherwise be mostly avatar with the name clipped off the bottom.
    /// </summary>
    public void Resize(double width, double height)
    {
        var shortest = Math.Min(width, height);
        var size = Math.Clamp(shortest * 0.38, 22, 96);

        _avatar.Resize(size);
        _name.Visibility = shortest > 92 ? Visibility.Visible : Visibility.Collapsed;
        _name.FontSize = Math.Clamp(shortest * 0.10, 10, 14);
        _name.MaxWidth = Math.Max(24, width - 12);
    }
}
