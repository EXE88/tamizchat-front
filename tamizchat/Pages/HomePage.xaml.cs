using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TamizChat.Core;
using TamizChat.Services;

namespace TamizChat.Pages;

/// <summary>
/// The server list the user actually joins from. Each card is probed over HTTP
/// so it can show whether the server is up and how busy it is before anyone
/// commits to connecting.
/// </summary>
public sealed partial class HomePage : Page
{
    private CancellationTokenSource? _probes;

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            if (Environment.GetEnvironmentVariable("TAMIZCHAT_TREEDUMP") == "1")
            {
                DispatcherQueue.TryEnqueue(DumpTree);
            }
        };
        Unloaded += (_, _) => _probes?.Cancel();
    }

    /// <summary>
    /// Writes the laid-out visual tree to a file. Used to find out why an element
    /// is not on screen, instead of guessing from a screenshot.
    /// </summary>
    private void DumpTree()
    {
        var lines = new List<string>();

        void Walk(DependencyObject node, int depth)
        {
            var name = node.GetType().Name;

            if (node is FrameworkElement fe)
            {
                // Position matters as much as size: an element can be the right
                // size and still be off the side of the window.
                var x = double.NaN;
                try
                {
                    x = fe.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                }
                catch (Exception)
                {
                    // Not in the tree yet.
                }

                name = $"{name} name={fe.Name} x={x:F0} w={fe.ActualWidth:F0} h={fe.ActualHeight:F0} " +
                       $"vis={fe.Visibility} halign={fe.HorizontalAlignment}";
            }

            lines.Add(new string(' ', depth * 2) + name);

            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
            }
        }

        Walk(this, 0);
        File.WriteAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "treedump.txt"), lines);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        _probes?.Cancel();
        _probes = new CancellationTokenSource();

        ServerList.Items.Clear();
        var servers = ServerStore.Servers;
        EmptyHint.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var server in servers)
        {
            var status = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("TcTextSecondaryBrush"),
                Text = "Checking…",
            };

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Brush("TcTextSecondaryBrush"),
                Opacity = 0.4,
            };

            ServerList.Items.Add(BuildCard(server, status, dot));
            _ = ProbeAsync(server, status, dot, _probes.Token);
        }
    }

    private Border BuildCard(ServerEntry server, TextBlock status, Ellipse dot)
    {
        var initial = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = Brush("TcAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(server.Name) ? "?" : server.Name.Trim()[..1].ToUpperInvariant(),
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("TcOnAccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(status);

        var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = server.Name,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("TcTextPrimaryBrush"),
        });
        details.Children.Add(new TextBlock
        {
            Text = server.Address,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("TcTextSecondaryBrush"),
        });
        details.Children.Add(statusRow);

        var join = new Button
        {
            Content = "Join",
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(22, 8, 22, 8),
            Style = (Style)Application.Current.Resources["TcAccentButtonStyle"],
        };
        join.Click += async (_, _) => await JoinAsync(server, join, status);

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(initial, 0);
        Grid.SetColumn(details, 1);
        Grid.SetColumn(join, 2);
        grid.Children.Add(initial);
        grid.Children.Add(details);
        grid.Children.Add(join);

        return new Border
        {
            Padding = new Thickness(18),
            Background = Brush("TcSurfaceBrush"),
            BorderBrush = Brush("TcBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"],
            Child = grid,
        };
    }

    /// <summary>
    /// Connects, then drills into the server. The connection is made before
    /// navigating so a failure is reported here rather than on an empty page the
    /// user has already been taken to.
    /// </summary>
    private async Task JoinAsync(ServerEntry server, Button join, TextBlock status)
    {
        join.IsEnabled = false;
        var original = status.Text;
        status.Text = "Connecting…";

        try
        {
            await ServerSession.Instance.ConnectAsync(server);
            (App.MainWindow as MainWindow)?.EnterServer();
            status.Text = original;
        }
        catch (Exception ex)
        {
            status.Text = $"Could not connect: {ex.Message}";
        }
        finally
        {
            join.IsEnabled = true;
        }
    }

    private async Task ProbeAsync(ServerEntry server, TextBlock status, Ellipse dot, CancellationToken token)
    {
        var info = await ServerProbe.TryGetAsync(server.HttpUrl, token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (info is null)
        {
            status.Text = "Offline";
            dot.Fill = Brush("TcDangerBrush");
            dot.Opacity = 1;
            return;
        }

        var parts = new List<string>
        {
            info.Name,
            $"{info.OnlineUsers}/{info.MaxUsers} online",
        };

        if (info.PasswordRequired)
        {
            parts.Add("password required");
        }

        if (info.MediaEnabled)
        {
            parts.Add("voice");
        }

        status.Text = string.Join("  ·  ", parts);
        dot.Fill = new SolidColorBrush(Colors.LimeGreen);
        dot.Opacity = 1;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
