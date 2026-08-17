using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        FillDevices(InputBox, AudioDevices.Inputs(), SettingsStore.Current.InputDeviceId);
        FillDevices(OutputBox, AudioDevices.Outputs(), SettingsStore.Current.OutputDeviceId);
        MicGainSlider.Value = SettingsStore.Current.MicGain * 100;

        EchoSwitch.IsOn = SettingsStore.Current.EchoCancellation;
        NoiseSwitch.IsOn = SettingsStore.Current.NoiseSuppression;

        ShareQualityBox.SelectedIndex = QualityIndex(SettingsStore.Current.ScreenShareMaxHeight);
        ShareFpsSlider.Value = Math.Clamp(SettingsStore.Current.ScreenShareFps, 5, 30);
        ShareCursorSwitch.IsOn = SettingsStore.Current.ScreenShareCursor;

        // **After every control has been given its value.** Setting a control
        // raises its change handler, and those handlers write the whole group
        // back to settings — so with this line any higher up, populating the
        // first control saved the *unpopulated* state of all the others over the
        // real values. Merely opening this page wiped the overlay configuration.
        _loading = false;

        Translate();
        RenderClips();
        RenderKeys();
        RenderAvatar();
        ShowStatus();
        StartMeter();

        // The preview follows the session, so an uploaded picture appears the
        // moment the server announces it rather than at the next visit.
        ServerSession.Instance.Changed += OnSessionChanged;

        // The timer holds a reference to this page; without stopping it the page
        // is kept alive after navigating away, and so is the tick. The same goes
        // for the session's event.
        Unloaded += (_, _) =>
        {
            _meter.Stop();
            ServerSession.Instance.Changed -= OnSessionChanged;
        };
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
        AudioLabel.Text = Loc.Get("Settings.Audio");
        InputLabel.Text = Loc.Get("Settings.Microphone");
        OutputLabel.Text = Loc.Get("Settings.Speakers");
        MicGainLabel.Text = Loc.Get("Settings.MicLevel");
        MicMeterLabel.Text = Loc.Get("Settings.InputMeter");
        KeysLabel.Text = Loc.Get("Settings.Shortcuts");
        KeysHelp.Text = Loc.Get("Settings.ShortcutsHelp");
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

        ProcessingLabel.Text = Loc.Get("Settings.Processing");
        EchoSwitch.Header = Loc.Get("Settings.EchoCancellation");
        EchoHelp.Text = Loc.Get("Settings.EchoCancellationHelp");
        NoiseSwitch.Header = Loc.Get("Settings.NoiseSuppression");

        ScreenShareLabel.Text = Loc.Get("Settings.ScreenShare");
        ScreenShareHelp.Text = Loc.Get("Settings.ScreenShareHelp");
        ShareQualityLabel.Text = Loc.Get("Settings.ScreenShareQuality");
        ShareFpsLabel.Text = Loc.Get("Settings.ScreenShareFps");
        ShareCursorSwitch.Header = Loc.Get("Settings.ScreenShareCursor");

        ProfileLabel.Text = Loc.Get("Settings.Profile");
        ProfilePictureHelp.Text = Loc.Get("Settings.ProfilePictureHelp");
        ChooseAvatarButton.Content = Loc.Get("Settings.ChooseImage");
        RemoveAvatarButton.Content = Loc.Get("Settings.RemoveImage");

        SetItems(ShareQualityBox, "Quality.Original", "Quality.1080", "Quality.720", "Quality.480");
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

    /// <summary>
    /// Fills a device list, keeping the saved choice selected.
    ///
    /// A device that is not currently plugged in simply is not in the list, so
    /// the selection falls back to the system default rather than showing an
    /// entry that cannot be opened.
    /// </summary>
    private static void FillDevices(ComboBox box, IReadOnlyList<AudioDeviceInfo> devices, string selectedId)
    {
        box.Items.Clear();

        foreach (var device in devices)
        {
            box.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        }

        var index = devices.ToList().FindIndex(d => d.Id == selectedId);
        box.SelectedIndex = index < 0 ? 0 : index;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        SettingsStore.Current.InputDeviceId = (InputBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        SettingsStore.Current.OutputDeviceId = (OutputBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        SettingsStore.Save();

        // Takes effect immediately rather than at the next call: changing your
        // microphone is something you do *because* the current one is wrong.
        VoiceService.Instance.ReopenDevices();
    }

    private void OnMicGainChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Current.MicGain = MicGainSlider.Value / 100;
        SettingsStore.Save();
    }

    // --- microphone processing ---

    private void OnProcessingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        SettingsStore.Current.EchoCancellation = EchoSwitch.IsOn;
        SettingsStore.Current.NoiseSuppression = NoiseSwitch.IsOn;
        SettingsStore.Save();

        // Applied to the call already in progress, not at the next one: somebody
        // turning echo cancellation on is doing it *because* the room is
        // currently echoing.
        VoiceService.Instance.ReloadProcessing();
    }

    // --- screen sharing ---

    /// <summary>
    /// The four picture sizes, as the ceiling each one puts on the height.
    /// Zero is "send it at its own size".
    /// </summary>
    private static readonly int[] Qualities = [0, 1080, 720, 480];

    private static int QualityIndex(int maxHeight)
    {
        var index = Array.IndexOf(Qualities, maxHeight);
        return index < 0 ? 1 : index;
    }

    private void OnShareChanged(object sender, SelectionChangedEventArgs e) => SaveShare();

    private void OnShareToggled(object sender, RoutedEventArgs e) => SaveShare();

    private void OnShareSliderChanged(object sender, RangeBaseValueChangedEventArgs e) => SaveShare();

    /// <summary>
    /// Writes the screen sharing settings. They are read when a share starts, so
    /// a change reaches the next share rather than the one already running —
    /// republishing a live track at a different size would drop the picture for
    /// everyone watching, which is worse than the change waiting.
    /// </summary>
    private void SaveShare()
    {
        if (_loading) return;

        SettingsStore.Current.ScreenShareMaxHeight = Qualities[Math.Clamp(ShareQualityBox.SelectedIndex, 0, 3)];
        SettingsStore.Current.ScreenShareFps = (int)ShareFpsSlider.Value;
        SettingsStore.Current.ScreenShareCursor = ShareCursorSwitch.IsOn;
        SettingsStore.Save();
    }

    // --- profile picture ---

    /// <summary>
    /// Draws the picture as it will actually appear: the same control the room
    /// grid uses, at the same shape, rather than a rectangular preview that
    /// hides how the circle will crop it.
    /// </summary>
    private void RenderAvatar()
    {
        var me = ServerSession.Instance.Users
            .FirstOrDefault(u => u.ClientUuid == ServerSession.Instance.MyUuid);

        var view = new TamizChat.Controls.AvatarView(
            me?.Username ?? SettingsStore.Current.Username, size: 64);

        if (me is not null)
        {
            // SetUser, not SetPicture: the picture is never ready the first time
            // it is asked for, so the control has to watch for its own arrival.
            view.SetUser(me);
        }

        AvatarPreview.Content = view;

        // A picture belongs to a server, not to this machine: it is stored there
        // and shown to the people there. With no connection there is nothing to
        // upload it to and nothing to show.
        var connected = ServerSession.Instance.IsConnected;
        ChooseAvatarButton.IsEnabled = connected;
        RemoveAvatarButton.IsEnabled = connected && !string.IsNullOrEmpty(me?.Avatar);

        if (!connected)
        {
            ShowAvatarError(Loc.Get("Server.NotConnected"));
        }
    }

    private async void OnChooseAvatarClick(object sender, RoutedEventArgs e)
    {
        AvatarError.Visibility = Visibility.Collapsed;

        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif" })
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
            await ServerSession.Instance.UploadAvatarAsync(file.Path);

            // Nothing is drawn here. The server announces the new picture as a
            // user.updated, the session records it, and every avatar on screen —
            // this preview included — is redrawn from that. Painting it locally
            // as well would be a second source of truth that could disagree.
        }
        catch (Exception ex)
        {
            ShowAvatarError(Loc.Get("Settings.ImageFailed", ex.Message));
        }
    }

    private async void OnRemoveAvatarClick(object sender, RoutedEventArgs e)
    {
        AvatarError.Visibility = Visibility.Collapsed;

        try
        {
            await ServerSession.Instance.ClearAvatarAsync();
        }
        catch (Exception ex)
        {
            ShowAvatarError(Loc.Get("Settings.ImageFailed", ex.Message));
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RenderAvatar();

    private void ShowAvatarError(string message)
    {
        AvatarError.Text = message;
        AvatarError.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Drives the input meter while this page is open.
    ///
    /// A timer rather than an event from the capture path: the level changes a
    /// hundred times a second and a meter only needs to look alive.
    /// </summary>
    private void StartMeter()
    {
        _meter.Interval = TimeSpan.FromMilliseconds(60);
        _meter.Tick += (_, _) =>
        {
            MicMeter.Value = Math.Min(100, VoiceService.Instance.MicLevel * 100);
            ShowDevicesInUse();
        };

        _meter.Start();
        ShowDevicesInUse();
    }

    private readonly DispatcherTimer _meter = new();

    /// <summary>
    /// Names the endpoints that are really open.
    ///
    /// The lists above are what was *asked* for. A device that is unplugged,
    /// asleep or disabled when a call starts resolves to nothing and the system
    /// default is used instead — which is indistinguishable, from the outside,
    /// from the setting being ignored. Saying which one is open turns that from
    /// a mystery into a fact.
    /// </summary>
    private void ShowDevicesInUse()
    {
        var voice = VoiceService.Instance;

        // What is open if anything is, and otherwise what *would* open. Both
        // answers are useful and the second one is the one somebody reads before
        // joining, when nothing is open yet.
        var mic = voice.MicrophoneDeviceName;
        if (string.IsNullOrEmpty(mic))
        {
            mic = AudioDevices.NameInUse(SettingsStore.Current.InputDeviceId, input: true);
        }

        var speakers = voice.SpeakerDeviceName;
        if (string.IsNullOrEmpty(speakers))
        {
            speakers = AudioDevices.NameInUse(SettingsStore.Current.OutputDeviceId, input: false);
        }

        var text = Loc.Get("Settings.DevicesInUse",
            string.IsNullOrEmpty(mic) ? "—" : mic,
            string.IsNullOrEmpty(speakers) ? "—" : speakers);

        // Only touched when it changed: this runs on a 60 ms timer, and
        // reassigning the text every tick makes the whole line flicker.
        if (DevicesInUse.Text != text)
        {
            DevicesInUse.Text = text;
        }
    }

    /// <summary>
    /// One row per action: what it does, and the key bound to it.
    ///
    /// Binding is "press the button, then press the key" rather than typing a
    /// key name — the codes are virtual-key numbers and nobody knows them.
    /// </summary>
    private void RenderKeys()
    {
        KeyList.Items.Clear();

        foreach (HotKeyAction action in Enum.GetValues<HotKeyAction>())
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = Loc.Get($"Key.{action}"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            };

            var bound = SettingsStore.Current.HotKeys.TryGetValue(action.ToString(), out var code) ? code : 0;

            var button = new Button
            {
                Content = bound == 0 ? Loc.Get("Key.None") : KeyName(bound),
                MinWidth = 120,
                Margin = new Thickness(8, 0, 8, 0),
            };

            button.Click += (_, _) => Capture(action, button);

            var clear = new Button { Content = Loc.Get("Key.Clear") };
            clear.Click += (_, _) =>
            {
                SettingsStore.Current.HotKeys.Remove(action.ToString());
                SettingsStore.Save();
                RenderKeys();
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(button, 1);
            Grid.SetColumn(clear, 2);
            row.Children.Add(label);
            row.Children.Add(button);
            row.Children.Add(clear);

            KeyList.Items.Add(row);
        }
    }

    /// <summary>
    /// Waits for the next key and binds it.
    ///
    /// Handled on the button itself so the keystroke does not also reach the
    /// page — and Escape cancels, which is the one key nobody wants to bind.
    /// </summary>
    private void Capture(HotKeyAction action, Button button)
    {
        button.Content = Loc.Get("Key.Press");
        button.Focus(FocusState.Programmatic);

        void OnKey(object sender, KeyRoutedEventArgs e)
        {
            e.Handled = true;
            button.KeyDown -= OnKey;

            if (e.Key != Windows.System.VirtualKey.Escape)
            {
                SettingsStore.Current.HotKeys[action.ToString()] = (int)e.Key;
                SettingsStore.Save();
            }

            RenderKeys();
        }

        button.KeyDown += OnKey;
    }

    private static string KeyName(int code) => ((Windows.System.VirtualKey)code).ToString();

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
