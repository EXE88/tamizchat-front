using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TamizChat.Controls;
using TamizChat.Core.Protocol;
using TamizChat.Services;
using Windows.System;

namespace TamizChat.Pages;

/// <summary>
/// The room's chat.
///
/// Messages live only in the room's memory on the server, so there is nothing to
/// cache locally: the page reads a page of history on open and follows the live
/// stream from there.
/// </summary>
public sealed partial class ChatPage : Page
{
    private readonly HashSet<string> _shown = [];
    private readonly Dictionary<string, DateTime> _typing = [];
    private DispatcherTimer? _typingTimer;
    private DateTime _lastTypingSent = DateTime.MinValue;
    private long _oldestSeq;
    private bool _hasMore;
    private bool _loading;

    public ChatPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var session = ServerSession.Instance;
        session.MessageReceived += OnMessageReceived;
        session.TypingChanged += OnTypingChanged;
        session.Changed += OnSessionChanged;

        // Typing indicators have no "stopped" guarantee, so they are aged out.
        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _typingTimer.Tick += (_, _) => PruneTyping();
        _typingTimer.Start();

        UpdateHeader();
        await LoadNewestAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var session = ServerSession.Instance;
        session.MessageReceived -= OnMessageReceived;
        session.TypingChanged -= OnTypingChanged;
        session.Changed -= OnSessionChanged;
        _typingTimer?.Stop();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => UpdateHeader();

    private void UpdateHeader()
    {
        var session = ServerSession.Instance;
        var room = session.MyRoom;

        RoomTitle.Text = room?.Name ?? "Chat";
        RoomSubtitle.Text = room is null
            ? "Join a room to talk."
            : $"{room.MemberCount} in the room · messages are cleared when everyone leaves";

        var canTalk = session.IsConnected && room is not null;
        Composer.IsEnabled = canTalk;
        SendButton.IsEnabled = canTalk;

        if (!canTalk)
        {
            Placeholder.Text = session.IsConnected
                ? "You are not in a room."
                : "Not connected.";
            Placeholder.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadNewestAsync()
    {
        if (ServerSession.Instance.MyRoom is null)
        {
            return;
        }

        _loading = true;
        try
        {
            var history = await ServerSession.Instance.LoadHistoryAsync();
            _hasMore = history.HasMore;

            foreach (var message in history.Messages)
            {
                Append(message, atTop: false);
            }

            Placeholder.Text = history.Messages.Count == 0 ? "No messages yet. Say something." : "";
            Placeholder.Visibility = history.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            Placeholder.Text = $"Could not load messages: {ex.Message}";
            Placeholder.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Pages further back when the user reaches the top of the list.</summary>
    private async void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_loading || !_hasMore || e.IsIntermediate || MessageScroller.VerticalOffset > 40)
        {
            return;
        }

        _loading = true;
        try
        {
            var before = MessageScroller.ExtentHeight;
            var history = await ServerSession.Instance.LoadHistoryAsync(_oldestSeq);
            _hasMore = history.HasMore;

            foreach (var message in history.Messages.AsEnumerable().Reverse())
            {
                Append(message, atTop: true);
            }

            // Keep the reading position where it was rather than jumping to the
            // newly inserted top.
            MessageScroller.UpdateLayout();
            MessageScroller.ChangeView(null, MessageScroller.ExtentHeight - before, null, disableAnimation: true);
        }
        catch (Exception)
        {
            // Leave what is already on screen.
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnMessageReceived(object? sender, ChatMessage message)
    {
        if (message.RoomId != ServerSession.Instance.MyRoomId)
        {
            return;
        }

        var atBottom = MessageScroller.VerticalOffset
                       >= MessageScroller.ScrollableHeight - 60;

        Append(message, atTop: false);
        Placeholder.Visibility = Visibility.Collapsed;

        // Only follow the conversation if the user had not scrolled up to read.
        if (atBottom)
        {
            ScrollToBottom();
        }
    }

    private void Append(ChatMessage message, bool atTop)
    {
        // The sender gets their message from the request reply and again as a
        // broadcast, so identical ids are skipped.
        if (!_shown.Add(message.Id))
        {
            return;
        }

        if (_oldestSeq == 0 || message.Seq < _oldestSeq)
        {
            _oldestSeq = message.Seq;
        }

        var row = BuildRow(message);
        if (atTop)
        {
            MessageList.Children.Insert(0, row);
        }
        else
        {
            MessageList.Children.Add(row);
        }
    }

    /// <summary>
    /// Picks a file and uploads it. Nothing is posted afterwards: the server puts
    /// the file into the room itself, so it arrives like any other message.
    /// </summary>
    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps have no implicit window for a picker to sit on, so it
        // has to be told which one to use.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        AttachButton.IsEnabled = false;
        try
        {
            await ServerSession.Instance.UploadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            Placeholder.Text = $"Could not send the file: {ex.Message}";
            Placeholder.Visibility = Visibility.Visible;
        }
        finally
        {
            AttachButton.IsEnabled = true;
        }
    }

    private static Grid BuildRow(ChatMessage message)
    {
        var avatar = new AvatarView(message.Author.Username, 34)
        {
            VerticalAlignment = VerticalAlignment.Top,
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = message.Author.Username,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        });
        header.Children.Add(new TextBlock
        {
            Text = DateTimeOffset.FromUnixTimeSeconds(message.CreatedAt).LocalDateTime.ToString("HH:mm"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        });

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(header);

        if (message.Attachment is { } attachment)
        {
            body.Children.Add(BuildAttachment(attachment));
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            });
        }

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(avatar);
        row.Children.Add(body);
        return row;
    }

    /// <summary>
    /// A file in the conversation. Images show their thumbnail; everything else
    /// gets a card. Both need a fresh download link, which is short-lived and
    /// scoped to the room, so it is fetched when the row is built.
    /// </summary>
    private static Border BuildAttachment(Attachment attachment)
    {
        var name = new TextBlock
        {
            Text = attachment.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        };

        var detail = new TextBlock
        {
            Text = $"{HumanSize(attachment.Size)} · {attachment.Mime}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        var open = new Button { Content = "Open", Padding = new Thickness(14, 5, 14, 5) };
        open.Click += async (_, _) =>
        {
            var download = await ServerSession.Instance.GetDownloadAsync(attachment.Id);
            if (!string.IsNullOrEmpty(download?.Url))
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(download.Url));
            }
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(detail);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            // Picture or generic document.
            Glyph = attachment.IsImage ? "" : "",
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(open, 2);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(open);

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(row);

        if (attachment is { IsImage: true, HasThumb: true })
        {
            var preview = new Image
            {
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform,
            };

            // Fire and forget: a missing preview is not worth blocking the row.
            _ = LoadThumbAsync(attachment.Id, preview);
            content.Children.Add(preview);
        }

        return new Border
        {
            Padding = new Thickness(12),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["TcSurfaceHoverBrush"],
            BorderBrush = (Brush)Application.Current.Resources["TcBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = content,
        };
    }

    private static async Task LoadThumbAsync(string fileId, Image target)
    {
        try
        {
            var download = await ServerSession.Instance.GetDownloadAsync(fileId);
            if (!string.IsNullOrEmpty(download?.ThumbUrl))
            {
                target.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(download.ThumbUrl));
            }
        }
        catch (Exception)
        {
            // The card still shows the name and an Open button.
        }
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    private void ScrollToBottom()
    {
        MessageScroller.UpdateLayout();
        MessageScroller.ChangeView(null, MessageScroller.ScrollableHeight, null, disableAnimation: true);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter is left alone for a future multi-line composer.
        if (e.Key == VirtualKey.Enter
            && !Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Composer.Text = "";
        try
        {
            await ServerSession.Instance.SendMessageAsync(text);
            await ServerSession.Instance.SendTypingAsync(false);
        }
        catch (Exception ex)
        {
            Placeholder.Text = $"Could not send: {ex.Message}";
            Placeholder.Visibility = Visibility.Visible;

            // Give the text back rather than losing what was typed.
            Composer.Text = text;
        }
    }

    private async void OnComposerChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(Composer.Text))
        {
            return;
        }

        // Throttled: this fires on every keystroke, and the indicator only needs
        // refreshing every few seconds.
        if (DateTime.UtcNow - _lastTypingSent < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastTypingSent = DateTime.UtcNow;
        await ServerSession.Instance.SendTypingAsync(true);
    }

    private void OnTypingChanged(object? sender, ChatTyping typing)
    {
        if (typing.ClientUuid == ServerSession.Instance.MyUuid)
        {
            return;
        }

        if (typing.Typing)
        {
            _typing[typing.Username] = DateTime.UtcNow;
        }
        else
        {
            _typing.Remove(typing.Username);
        }

        ShowTyping();
    }

    private void PruneTyping()
    {
        var stale = _typing
            .Where(pair => DateTime.UtcNow - pair.Value > TimeSpan.FromSeconds(5))
            .Select(pair => pair.Key)
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var name in stale)
        {
            _typing.Remove(name);
        }

        ShowTyping();
    }

    private void ShowTyping()
    {
        var names = _typing.Keys.OrderBy(n => n).ToList();
        TypingLine.Text = names.Count switch
        {
            0 => "",
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            _ => "Several people are typing…",
        };
    }
}
