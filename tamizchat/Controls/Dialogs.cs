using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TamizChat.Controls;

/// <summary>Shared setup for the app's dialogs.</summary>
public static class Dialogs
{
    /// <summary>
    /// Makes a dialog follow the chosen theme, including its primary button.
    ///
    /// `PrimaryButtonStyle` alone is not enough and neither is overriding the
    /// accent brushes at application level: a ContentDialog is hosted in its own
    /// popup root, and with `RequestedTheme` set it re-resolves `ThemeResource`
    /// lookups against the built-in theme dictionaries — so the Save button came
    /// out Windows blue under every palette. Putting the brushes in the dialog's
    /// **own** resource dictionary wins, because that is the first place its
    /// template looks.
    /// </summary>
    public static ContentDialog Themed(this ContentDialog dialog, XamlRoot? root)
    {
        dialog.XamlRoot = root;

        if (root?.Content is FrameworkElement content)
        {
            dialog.RequestedTheme = content.RequestedTheme;
        }

        foreach (var key in new[]
        {
            "AccentFillColorDefaultBrush",
            "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush",
            "TextOnAccentFillColorPrimaryBrush",
        })
        {
            if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is SolidColorBrush solid)
            {
                // A new brush rather than the shared instance: the dialog's
                // dictionary would otherwise hold a reference that ThemeManager
                // keeps mutating after the dialog is gone.
                dialog.Resources[key] = new SolidColorBrush(solid.Color);
            }
        }

        return dialog;
    }
}
