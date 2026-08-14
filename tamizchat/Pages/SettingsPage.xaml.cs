using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TamizChat.Audio;
using TamizChat.Overlays;
using Windows.Storage.Pickers;
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

        EventSoundsSwitch.IsOn = SettingsStore.Current.EventSoundsEnabled;
        EventVolumeSlider.Value = SettingsStore.Current.EventSoundsVolume * 100;

        MembersOverlaySwitch.IsOn = SettingsStore.Current.MembersOverlayEnabled;
        MembersCornerBox.SelectedIndex = (int)SettingsStore.Current.MembersOverlayCorner;
        MembersOpacitySlider.Value = SettingsStore.Current.MembersOverlayOpacity * 100;

        MessagesOverlaySwitch.IsOn = SettingsStore.Current.MessagesOverlayEnabled;
        MessagesCornerBox.SelectedIndex = (int)SettingsStore.Current.MessagesOverlayCorner;
        MessagesOpacitySlider.Value = SettingsStore.Current.MessagesOverlayOpacity * 100;
        MaxMessagesSlider.Value = SettingsStore.Current.MessagesOverlayMax;
        FadeAfterSlider.Value = SettingsStore.Current.MessagesOverlayFadeSeconds;

        // **After every control has been given its value.** Setting a control
        // raises its change handler, and those handlers write the whole group
        // back to settings — so with this line any higher up, populating the
        // first control saved the *unpopulated* state of all the others over the
        // real values. Merely opening this page wiped the overlay configuration.
        _loading = false;

        Translate();
        RenderClips();
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
        SoundboardLabel.Text = Loc.Get("Settings.Soundboard");
        SoundboardHelp.Text = Loc.Get("Settings.SoundboardHelp");
        AddClipButton.Content = Loc.Get("Settings.AddClip");
        EventSoundsLabel.Text = Loc.Get("Settings.EventSounds");
        EventSoundsHelp.Text = Loc.Get("Settings.EventSoundsHelp");
        EventVolumeLabel.Text = Loc.Get("Settings.Volume");
        OverlaysLabel.Text = Loc.Get("Settings.Overlays");
        MembersOverlayLabel.Text = Loc.Get("Settings.MembersOverlay");
        MembersOverlayHelp.Text = Loc.Get("Settings.MembersOverlayHelp");
        MessagesOverlayLabel.Text = Loc.Get("Settings.MessagesOverlay");
        MessagesOverlayHelp.Text = Loc.Get("Settings.MessagesOverlayHelp");
        MembersPositionLabel.Text = Loc.Get("Settings.Position");
        MessagesPositionLabel.Text = Loc.Get("Settings.Position");
        MembersOpacityLabel.Text = Loc.Get("Settings.Opacity");
        MessagesOpacityLabel.Text = Loc.Get("Settings.Opacity");
        MaxMessagesLabel.Text = Loc.Get("Settings.MaxMessages");
        FadeAfterLabel.Text = Loc.Get("Settings.FadeAfter");

        SetItems(MembersCornerBox, "Corner.TopLeft", "Corner.TopRight", "Corner.BottomLeft", "Corner.BottomRight");
        SetItems(MessagesCornerBox, "Corner.TopLeft", "Corner.TopRight", "Corner.BottomLeft", "Corner.BottomRight");

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

    /// <summary>
    /// Draws the clip list: a name and a Remove button per entry.
    ///
    /// Rebuilt wholesale on every change rather than patched. The list is a
    /// handful of rows that only changes when the user presses something, so
    /// the simplest correct thing is also fast enough.
    /// </summary>
    private void RenderClips()
    {
        ClipList.Items.Clear();

        foreach (var entry in SoundboardLibrary.Instance.Entries)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBox
            {
                Text = entry.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            // Renamed as they type rather than behind an edit button: there is
            // one field and nothing to cancel.
            name.LostFocus += (_, _) => SoundboardLibrary.Instance.Rename(entry.Id, name.Text);

            var remove = new Button { Content = Loc.Get("Settings.Remove") };
            remove.Click += (_, _) =>
            {
                SoundboardLibrary.Instance.Remove(entry.Id);
                RenderClips();
            };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(remove, 1);
            row.Children.Add(name);
            row.Children.Add(remove);

            ClipList.Items.Add(row);
        }
    }

    private async void OnAddClipClick(object sender, RoutedEventArgs e)
    {
        ClipError.Visibility = Visibility.Collapsed;

        var picker = new FileOpenPicker();
        foreach (var extension in AudioClip.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        // An unpackaged app has no implicit window for a picker to sit on, and
        // it throws without one.
        var window = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            SoundboardLibrary.Instance.Add(file.Path, Path.GetFileNameWithoutExtension(file.Name));
            RenderClips();
        }
        catch (Exception ex)
        {
            // Decoding happens before anything is saved, so a file that cannot
            // be read is reported here rather than failing silently later.
            ClipError.Text = Loc.Get("Settings.AddClipFailed", ex.Message);
            ClipError.Visibility = Visibility.Visible;
        }
    }

    private void OnEventSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Current.EventSoundsEnabled = EventSoundsSwitch.IsOn;
        EventSounds.Instance.Enabled = EventSoundsSwitch.IsOn;
        SettingsStore.Save();
    }

    private void OnEventVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Current.EventSoundsVolume = EventVolumeSlider.Value / 100;
        EventSounds.Instance.Volume = SettingsStore.Current.EventSoundsVolume;
        SettingsStore.Save();
    }

    private void OnOverlaySliderChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveOverlays();

    private void OnOverlayChanged(object sender, RoutedEventArgs e) => SaveOverlays();

    private void OnOverlayChanged(object sender, SelectionChangedEventArgs e) => SaveOverlays();

    /// <summary>
    /// Writes every overlay setting and applies them at once.
    ///
    /// One handler for all of them rather than one each: they are read together
    /// by <see cref="OverlayService.Apply"/> anyway, and a slider being dragged
    /// should move the overlay as it goes.
    /// </summary>
    private void SaveOverlays()
    {
        if (_loading) return;

        var settings = SettingsStore.Current;

        settings.MembersOverlayEnabled = MembersOverlaySwitch.IsOn;
        settings.MembersOverlayCorner = (OverlayCorner)Math.Max(0, MembersCornerBox.SelectedIndex);
        settings.MembersOverlayOpacity = MembersOpacitySlider.Value / 100;

        settings.MessagesOverlayEnabled = MessagesOverlaySwitch.IsOn;
        settings.MessagesOverlayCorner = (OverlayCorner)Math.Max(0, MessagesCornerBox.SelectedIndex);
        settings.MessagesOverlayOpacity = MessagesOpacitySlider.Value / 100;
        settings.MessagesOverlayMax = (int)MaxMessagesSlider.Value;
        settings.MessagesOverlayFadeSeconds = FadeAfterSlider.Value;

        SettingsStore.Save();
        OverlayService.Instance.Apply();
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
