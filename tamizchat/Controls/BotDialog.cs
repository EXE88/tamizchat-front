using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TamizChat.Core.Protocol;
using TamizChat.Localization;

namespace TamizChat.Controls;

/// <summary>
/// Creating or editing a music bot: a name, a colour, and how it walks its queue.
///
/// There is deliberately **no folder box**. A bot's music folder is a path on the
/// server's machine, and the server refuses to take one from a client — a bot
/// made here is given storage of its own and filled by uploading tracks into a
/// playlist. Asking for a path here would only produce a rejection.
/// </summary>
public sealed class BotDialog
{
    private readonly Bot? _existing;

    private readonly TextBox _name;
    private readonly ComboBox _colour;
    private readonly CheckBox _loop;
    private readonly CheckBox _shuffle;
    private readonly CheckBox _enabled;

    public BotDialog(Bot? existing)
    {
        _existing = existing;

        _name = new TextBox { Header = Loc.Get("Admin.BotName"), Text = existing?.Name ?? "" };

        _colour = new ComboBox
        {
            Header = Loc.Get("Admin.BotColour"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // The same palette the role designer offers, so a bot sits visually
        // alongside the roles rather than introducing a second set of colours.
        foreach (var (name, hex, _) in Colours.Palette)
        {
            _colour.Items.Add(new ComboBoxItem { Content = name, Tag = hex });
        }

        var index = Array.FindIndex(Colours.Palette, entry =>
            string.Equals(entry.Hex, existing?.Color, StringComparison.OrdinalIgnoreCase));
        _colour.SelectedIndex = index < 0 ? 0 : index;

        _loop = new CheckBox
        {
            Content = Loc.Get("Admin.BotLoop"),
            IsChecked = existing?.Loop ?? true,
        };

        _shuffle = new CheckBox
        {
            Content = Loc.Get("Admin.BotShuffle"),
            IsChecked = existing?.Shuffle ?? false,
        };

        _enabled = new CheckBox
        {
            Content = Loc.Get("Admin.BotEnabled"),
            IsChecked = existing?.Enabled ?? true,
        };
    }

    public XamlRoot? XamlRoot { get; init; }

    public async Task<BotSpec?> ShowAsync()
    {
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(_name);
        panel.Children.Add(_colour);
        panel.Children.Add(_loop);
        panel.Children.Add(_shuffle);
        panel.Children.Add(_enabled);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Admin.BotStorageNote"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            Title = Loc.Get(_existing is null ? "Admin.NewBot" : "Admin.EditBot"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Admin.Save"),
            CloseButtonText = Loc.Get("Admin.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["TcAccentButtonStyle"],
        };

        dialog.Themed(XamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new BotSpec
        {
            BotId = _existing?.Id,
            Name = _name.Text.Trim(),
            Color = (_colour.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            Loop = _loop.IsChecked == true,
            Shuffle = _shuffle.IsChecked == true,
            Enabled = _enabled.IsChecked == true,
        };
    }
}
