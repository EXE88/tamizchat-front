using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Localization;
using TamizChat.Video;

namespace TamizChat.Controls;

/// <summary>
/// Asks what to share before anything is broadcast.
///
/// Turning the toggle on and silently publishing a monitor is the wrong
/// behaviour twice over: the user has not said which screen, and screen sharing
/// is the one control in the app where doing something unasked can leak
/// whatever happens to be on the other monitor.
/// </summary>
public static class SharePicker
{
    public static async Task<ShareTarget?> ShowAsync(XamlRoot root)
    {
        var targets = ShareTargets.List();

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 380,
        };

        foreach (var target in targets)
        {
            var title = new TextBlock
            {
                Text = target.Title,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            };

            var subtitle = new TextBlock
            {
                Text = target.Subtitle,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            };

            var stack = new StackPanel { Padding = new Thickness(2, 6, 2, 6) };
            stack.Children.Add(title);
            stack.Children.Add(subtitle);

            list.Items.Add(new ListViewItem { Content = stack, Tag = target });
        }

        if (targets.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("Share.Title"),
            Content = list,
            PrimaryButtonText = Loc.Get("Share.Share"),
            CloseButtonText = Loc.Get("Share.Cancel"),
            DefaultButton = ContentDialogButton.Primary,

            // The stock primary button paints itself with the Windows system
            // accent and ignores the chosen theme entirely.
            PrimaryButtonStyle = (Style)Application.Current.Resources["TcAccentButtonStyle"],

            // Without this the dialog is drawn with the system theme rather than
            // the app's, which on a dark palette means a white box.
            RequestedTheme = ((FrameworkElement)root.Content).RequestedTheme,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return (list.SelectedItem as ListViewItem)?.Tag as ShareTarget;
    }
}
