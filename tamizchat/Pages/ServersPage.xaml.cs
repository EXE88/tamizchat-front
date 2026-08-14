using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Services;

namespace TamizChat.Pages;

/// <summary>Managing the saved server list. Joining happens on Home.</summary>
public sealed partial class ServersPage : Page
{
    public ServersPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        ServerList.Items.Clear();
        foreach (var server in ServerStore.Servers)
        {
            ServerList.Items.Add(BuildRow(server));
        }
    }

    private Border BuildRow(ServerEntry server)
    {
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
            Text = server.WebSocketUrl,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("TcTextSecondaryBrush"),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var edit = new Button { Content = "Edit" };
        edit.Click += async (_, _) => await EditAsync(server);

        var remove = new Button { Content = "Remove" };
        remove.Click += async (_, _) => await RemoveAsync(server);

        actions.Children.Add(edit);
        actions.Children.Add(remove);

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(details, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(details);
        grid.Children.Add(actions);

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

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var entry = new ServerEntry { Name = "New server", Address = "localhost:8080" };
        if (await ShowEditorAsync("Add server", entry))
        {
            ServerStore.Add(entry);
            Refresh();
        }
    }

    private async Task EditAsync(ServerEntry server)
    {
        if (await ShowEditorAsync("Edit server", server))
        {
            ServerStore.Save();
            Refresh();
        }
    }

    private async Task RemoveAsync(ServerEntry server)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove server",
            Content = $"Remove \"{server.Name}\" from the list? This does not touch the server itself.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            ServerStore.Remove(server);
            Refresh();
        }
    }

    /// <summary>
    /// Edits the entry in place and reports whether it was accepted, so the
    /// caller decides between adding it and saving an existing one.
    /// </summary>
    private async Task<bool> ShowEditorAsync(string title, ServerEntry entry)
    {
        var name = new TextBox { Header = "Name", Text = entry.Name };
        var address = new TextBox { Header = "Address (host:port)", Text = entry.Address };
        var username = new TextBox { Header = "Username (optional)", Text = entry.Username };
        var tls = new CheckBox { Content = "Use TLS (wss)", IsChecked = entry.UseTls };

        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(name);
        panel.Children.Add(address);
        panel.Children.Add(username);
        panel.Children.Add(tls);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        entry.Name = string.IsNullOrWhiteSpace(name.Text) ? "Unnamed server" : name.Text.Trim();
        entry.Address = address.Text.Trim();
        entry.Username = username.Text.Trim();
        entry.UseTls = tls.IsChecked == true;
        return true;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
