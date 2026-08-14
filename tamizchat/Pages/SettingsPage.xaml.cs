using Microsoft.UI.Xaml.Controls;
using TamizChat.Localization;
using TamizChat.Services;
using TamizChat.Theming;

namespace TamizChat.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        ThemeBox.SelectedIndex = (int)ThemeManager.Instance.Family;
        ModeBox.SelectedIndex = (int)ThemeManager.Instance.Mode;
        BackdropBox.SelectedIndex = (int)ThemeManager.Instance.Backdrop;
        LanguageBox.SelectedIndex = Loc.Language == "fa" ? 1 : 0;
        _loading = false;

        Translate();
        ShowStatus();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Loc.SetLanguage(LanguageBox.SelectedIndex == 1 ? "fa" : "en");
        SettingsStore.Current.Language = Loc.Language;
        SettingsStore.Save();
        Translate();
        ShowStatus();
    }

    /// <summary>
    /// Re-reads every string on this page.
    ///
    /// The page is not rebuilt on a language change: the combo boxes hold the
    /// user's current selection, and recreating them would either lose it or
    /// fire the change handlers again as they repopulate.
    /// </summary>
    private void Translate()
    {
        Title.Text = Loc.Get("Settings.Title");
        AppearanceLabel.Text = Loc.Get("Settings.Appearance");
        ThemeLabel.Text = Loc.Get("Settings.Theme");
        ModeLabel.Text = Loc.Get("Settings.Mode");
        BackgroundLabel.Text = Loc.Get("Settings.Background");
        BackdropHelp.Text = Loc.Get("Settings.BackdropHelp");
        LanguageLabel.Text = Loc.Get("Settings.Language");
        LanguageHelp.Text = Loc.Get("Settings.LanguageHelp");
        PreviewLabel.Text = Loc.Get("Settings.Preview");
        AccentLabel.Text = Loc.Get("Settings.Accent");
        StandardButton.Content = Loc.Get("Settings.StandardButton");
        SecondaryText.Text = Loc.Get("Settings.SecondaryText");

        SetItems(ThemeBox, "Theme.SaltAndPepper", "Theme.VioletAndLavender", "Theme.CarbonAndLime");
        SetItems(ModeBox, "Mode.FollowWindows", "Mode.Light", "Mode.Dark");
        SetItems(BackdropBox, "Backdrop.Matte", "Backdrop.MatteHigh", "Backdrop.Glass", "Backdrop.GlassHigh");
        SetItems(LanguageBox, "Language.English", "Language.Persian");
    }

    /// <summary>Relabels a combo box in place, keeping its selection.</summary>
    private static void SetItems(ComboBox box, params string[] keys)
    {
        var wasLoading = box.SelectedIndex;
        for (var i = 0; i < keys.Length && i < box.Items.Count; i++)
        {
            ((ComboBoxItem)box.Items[i]).Content = Loc.Get(keys[i]);
        }

        box.SelectedIndex = wasLoading;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetFamily((ThemeFamily)ThemeBox.SelectedIndex);
        ShowStatus();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetMode((AppThemeMode)ModeBox.SelectedIndex);
        ShowStatus();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetBackdrop((AppBackdrop)BackdropBox.SelectedIndex);
        ShowStatus();
    }

    private void ShowStatus()
    {
        var manager = ThemeManager.Instance;
        Status.Text =
            $"{manager.Family} / {(manager.IsDark ? "dark" : "light")} / {manager.Backdrop}\n" +
            SettingsStore.FilePath;
    }
}
