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

        // The stock accent keys are overridden with the palette's own accent, so
        // the primary button follows the theme instead of coming out Windows
        // blue. Taking the colour from TcAccentBrush rather than from the stock
        // keys is what makes this work under every palette: those keys are not
        // always redefined at application level, and when they are not, there
        // was nothing to copy and the button stayed blue.
        var accent = Application.Current.Resources.TryGetValue("TcAccentBrush", out var themed)
            ? themed as SolidColorBrush
            : null;

        var onAccent = Application.Current.Resources.TryGetValue("TcOnAccentBrush", out var onThemed)
            ? onThemed as SolidColorBrush
            : null;

        foreach (var (key, source) in new[]
        {
            ("AccentFillColorDefaultBrush", accent),
            ("AccentFillColorSecondaryBrush", accent),
            ("AccentFillColorTertiaryBrush", accent),
            ("TextOnAccentFillColorPrimaryBrush", onAccent),
        })
        {
            if (source is not null)
            {
                // A new brush rather than the shared instance: the dialog's
                // dictionary would otherwise hold a reference that ThemeManager
                // keeps mutating after the dialog is gone.
                dialog.Resources[key] = new SolidColorBrush(source.Color);
            }
        }

        return dialog;
    }
}
