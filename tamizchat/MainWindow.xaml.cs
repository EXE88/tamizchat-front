using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TamizChat.Controls;
using TamizChat.Localization;
using TamizChat.Navigation;
using TamizChat.Pages;
using TamizChat.Audio;
using TamizChat.Services;
using TamizChat.Overlays;
using TamizChat.Video;
using TamizChat.Theming;

namespace TamizChat;

/// <summary>
/// The shell: the title bar, the content frame and the floating bottom bar.
/// It owns which item set the bar shows and which transition each move uses.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// The item whose page is on screen. Tracked by key rather than derived from
    /// the page type, because several items can lead to the same page and the
    /// key is the only thing that stays unique.
    /// </summary>
    private string _selectedKey = "home";

    public MainWindow()
    {
        InitializeComponent();

        // The content is drawn all the way up through the title bar; this strip
        // is what the window can still be dragged by.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SizeAndCentre(appWindow, handle, 1180, 780);

        ThemeManager.Instance.Initialize(this);
        Loc.Initialize(SettingsStore.Current.Language);
        ApplyFlowDirection();

        // A language change re-lays the whole shell, because flow direction is
        // set on the root and the bar's labels are built in code.
        Loc.LanguageChanged += (_, _) =>
        {
            ApplyFlowDirection();
            NavBar.Retranslate();
            SyncShell();
        };

        ServerStore.Load();

        SoundboardLibrary.Instance.EnsureDefaults();
        EventSounds.Instance.Enabled = SettingsStore.Current.EventSoundsEnabled;
        EventSounds.Instance.Volume = SettingsStore.Current.EventSoundsVolume;

        NavigationService.Instance.Initialize(ContentFrame);
        NavigationService.Instance.Navigated += (_, _) =>
        {
            SyncShell();
            SyncInlineChat();
        };

        ServerSession.Instance.Changed += (_, _) =>
        {
            SyncInlineChat();
            OverlayService.Instance.Apply();
        };
        NavBar.ItemInvoked += OnNavItemInvoked;
        NavBar.StateChanged += OnNavStateChanged;

        // While a soundboard clip plays, Effects becomes Stop.
        VoiceService.Instance.Changed += (_, _) => NavBar.SetOverride(
            "effects",
            VoiceService.Instance.IsPlayingEffect ? "" : null,
            VoiceService.Instance.IsPlayingEffect ? Loc.Get("Effect.Stop") : null);

        NavigationService.Instance.Navigate(typeof(HomePage), NavTransition.None);

        // Lets a script open the app straight onto a page, which is how states
        // other than Home get screenshotted without driving the UI.
        switch (Environment.GetEnvironmentVariable("TAMIZCHAT_START_PAGE"))
        {
            case "server":
                EnterServer();
                break;

            case "servers":
                OnNavItemInvoked(null, ShellItems.PreServer.First(i => i.Key == "servers"));
                break;

            case "settings":
                OnNavItemInvoked(null, ShellItems.PreServer.First(i => i.Key == "settings"));
                break;
        }

        // Connects to the first saved server and goes straight in, so the room
        // grid can be exercised from a script.
        if (Environment.GetEnvironmentVariable("TAMIZCHAT_AUTOJOIN") is { } room)
        {
            _ = AutoJoinAsync(room);
        }

        Closed += (_, _) => OverlayService.Instance.Close();

        if (Environment.GetEnvironmentVariable("TAMIZCHAT_SELFTEST") == "1")
        {
            RunSelfTest();
        }
    }

    /// <summary>
    /// Mirrors the whole shell for Persian.
    ///
    /// Set on the root content rather than per page: FlowDirection inherits, so
    /// one assignment turns the navigation bar, every page and every dialog
    /// around together. Setting it per page leaves the bar facing the wrong way.
    /// </summary>
    private void ApplyFlowDirection()
    {
        if (Content is FrameworkElement root)
        {
            root.FlowDirection = Loc.FlowDirection;
        }

        // The title bar stays left to right even in Persian. Windows keeps the
        // minimise/maximise/close buttons on the right whatever the app's flow
        // direction is, and mirroring our own strip sends the back button and
        // the title straight underneath them.
        //
        // This has to be the whole row, not just AppTitleBar. The back button
        // deliberately sits *outside* AppTitleBar — anything inside the drag
        // region never receives clicks — so setting it on AppTitleBar alone left
        // the back arrow mirrored into the close button. That bug shipped once
        // already; the fix is the parent, which both of them inherit from.
        TitleRow.FlowDirection = FlowDirection.LeftToRight;
    }

    private async Task AutoJoinAsync(string room)
    {
        try
        {
            await AutoJoinCoreAsync(room);
        }
        catch (Exception ex)
        {
            // Fire-and-forget would otherwise swallow this silently.
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "autojoin-error.txt"),
                $"{ex.GetType().Name}: {ex}");
        }
    }

    private async Task AutoJoinCoreAsync(string room)
    {
        var server = ServerStore.Servers.FirstOrDefault();
        if (server is null)
        {
            return;
        }

        await ServerSession.Instance.ConnectAsync(server);

        // "none" means connect but stay out of every room, which is how the full
        // grid gets captured.
        if (room is not ("" or "none"))
        {
            var target = ServerSession.Instance.Rooms
                .FirstOrDefault(r => string.Equals(r.Name, room, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                await ServerSession.Instance.JoinRoomAsync(target.Id);
            }
        }

        EnterServer();

        var inServerPage = Environment.GetEnvironmentVariable("TAMIZCHAT_START_PAGE");
        if (inServerPage is "chat" or "paint")
        {
            OnNavItemInvoked(null, ShellItems.InServer.First(i => i.Key == inServerPage));
        }
    }

    /// <summary>Drills into a server. The only move that uses a drill going in.</summary>
    public void EnterServer()
    {
        _selectedKey = "room";
        NavigationService.Instance.Navigate(typeof(ServerPage), NavTransition.DrillIn);
    }

    /// <summary>
    /// Decides how a requested move should be animated.
    ///
    /// Both item sets slide within themselves, by the target's position relative
    /// to the current one. A drill is reserved for crossing between the two:
    /// going into a server and coming back out. That way the drill always means
    /// "a level changed" and never just "a different page".
    /// </summary>
    private void OnNavItemInvoked(object? sender, NavBarItem item)
    {
        if (item.Key == "disconnect")
        {
            Disconnect();
            return;
        }

        if (item.Kind != NavItemKind.Navigate || item.Page is null || item.Key == _selectedKey)
        {
            return;
        }

        var set = ShellItems.IsInServer(NavigationService.Instance.CurrentPageType)
            ? ShellItems.InServer
            : ShellItems.PreServer;

        var from = IndexOfKey(set, _selectedKey);
        var to = IndexOfKey(set, item.Key);
        var transition = from >= 0 && to >= 0
            ? NavigationService.SlideTowards(from, to)
            : NavTransition.None;

        _selectedKey = item.Key;
        NavigationService.Instance.Navigate(item.Page, transition);
    }

    /// <summary>
    /// Toggles and menus do not navigate — they act. The bar owns the microphone,
    /// speaker, camera and screen; nothing else in the app offers those controls.
    /// </summary>
    private void OnNavStateChanged(object? sender, NavBarStateEventArgs e)
    {
        LastStateChange = e.Item.Kind == NavItemKind.Menu
            ? $"{e.Item.Key}={e.Option}"
            : $"{e.Item.Key}={(e.IsOn ? "on" : "off")}";

        switch (e.Item.Key)
        {
            case "mic":
                _ = SafelyAsync(() => VoiceService.Instance.SetMutedAsync(!e.IsOn));
                break;

            case "speaker":
                VoiceService.Instance.SetDeafened(!e.IsOn);
                break;

            case "camera":
                _ = SafelyAsync(() => VoiceService.Instance.SetCameraAsync(e.IsOn));
                break;

            case "screen":
                _ = SafelyAsync(() => ShareScreenAsync(e.IsOn, e.Item));
                break;

            case "inline":
                SettingsStore.Current.InlineChatEnabled = e.IsOn;
                SettingsStore.Save();
                SyncInlineChat();

                // Focus follows the toggle: turning it on means you want to type
                // now, and reaching for the mouse afterwards defeats the point.
                if (e.IsOn)
                {
                    InlineChatBox.Focus(FocusState.Programmatic);
                }

                break;

            // Menus report the chosen entry by its position in the item's list,
            // found by index rather than by the label — the label is translated,
            // so matching on it would break the moment the language changes.
            case "effects":
                // While a clip is playing the item is a Stop button, so pressing
                // it again cuts the clip rather than opening the menu — that is
                // the whole point of being able to stop one you set off by
                // accident.
                if (VoiceService.Instance.IsPlayingEffect)
                {
                    VoiceService.Instance.StopEffect();
                    break;
                }

                if (IndexOfOption(e) is { } index
                    && index < SoundboardLibrary.Instance.Entries.Count)
                {
                    _ = SafelyAsync(() =>
                        VoiceService.Instance.PlayEffectAsync(SoundboardLibrary.Instance.Entries[index]));
                }

                break;

            case "voice":
                if (IndexOfOption(e) is { } preset)
                {
                    VoiceService.Instance.SetVoiceEffect((VoiceEffectKind)preset);
                }

                break;
        }
    }

    /// <summary>Where the chosen entry sits in its item's menu, or null if it is not there.</summary>
    private static int? IndexOfOption(NavBarStateEventArgs e)
    {
        var index = e.Item.MenuOptions.ToList().IndexOf(e.Option ?? "");
        return index < 0 ? null : index;
    }

    /// <summary>
    /// Shows the quick-chat strip only where it can do anything: inside a
    /// server, switched on, and connected.
    /// </summary>
    private void SyncInlineChat()
    {
        var usable = SettingsStore.Current.InlineChatEnabled
            && ServerSession.Instance.IsConnected
            && ShellItems.IsInServer(NavigationService.Instance.CurrentPageType);

        InlineChat.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInlineChatKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SendInlineAsync();
        }
    }

    private void OnInlineChatSend(object sender, RoutedEventArgs e) => _ = SendInlineAsync();

    /// <summary>
    /// Sends what is in the strip and clears it.
    ///
    /// Nothing is shown back: this is a send-only surface by design, and the
    /// messages overlay is what the other half of the conversation arrives on.
    /// </summary>
    private async Task SendInlineAsync()
    {
        var text = InlineChatBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        InlineChatBox.Text = "";

        try
        {
            await ServerSession.Instance.SendMessageAsync(text);
        }
        catch (Exception)
        {
            // Put it back rather than losing what they typed.
            InlineChatBox.Text = text;
        }
    }

    /// <summary>
    /// Asks what to share, then shares it.
    ///
    /// The toggle is put back if the user cancels — otherwise the bar would show
    /// screen sharing as on while nothing is being sent, which is the worst of
    /// the three possible states.
    /// </summary>
    private async Task ShareScreenAsync(bool on, NavBarItem item)
    {
        if (!on)
        {
            await VoiceService.Instance.SetScreenShareAsync(false);
            return;
        }

        var target = await SharePicker.ShowAsync(Content.XamlRoot);
        if (target is null)
        {
            NavBar.SetToggle(item.Key, false);
            return;
        }

        try
        {
            await VoiceService.Instance.SetScreenShareAsync(true, target);
        }
        catch (Exception)
        {
            NavBar.SetToggle(item.Key, false);
        }
    }

    /// <summary>
    /// Runs a media toggle without letting a failure reach the dispatcher.
    ///
    /// A camera that refuses to open must leave the app standing; the toggle
    /// simply goes back to reflecting what is actually happening.
    /// </summary>
    private static async Task SafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // The service already reports its real state through Changed.
        }
    }

    /// <summary>Recorded so the self-test can prove the toggles and menus fire.</summary>
    internal string LastStateChange { get; private set; } = "";

    private async void Disconnect()
    {
        // Close the socket as well as leaving the pages, or the server would keep
        // showing this user as present in a room nobody is looking at.
        await ServerSession.Instance.DisconnectAsync();

        // Unwind every page that belongs to the server, so leaving from chat or
        // paint lands back outside rather than on the room.
        while (NavigationService.Instance.CanGoBack
               && ShellItems.IsInServer(NavigationService.Instance.CurrentPageType))
        {
            NavigationService.Instance.GoBack();
        }

        _selectedKey = KeyForPage(ShellItems.PreServer, NavigationService.Instance.CurrentPageType) ?? "home";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        NavigationService.Instance.GoBack();

        var set = ShellItems.IsInServer(NavigationService.Instance.CurrentPageType)
            ? ShellItems.InServer
            : ShellItems.PreServer;
        _selectedKey = KeyForPage(set, NavigationService.Instance.CurrentPageType) ?? _selectedKey;
    }

    /// <summary>Brings the bar and the back button in line with the current page.</summary>
    private void SyncShell()
    {
        var current = NavigationService.Instance.CurrentPageType;
        var inServer = ShellItems.IsInServer(current);
        var items = inServer ? ShellItems.InServer : ShellItems.PreServer;

        // Keep the selection honest if we arrived by any route other than the bar.
        if (KeyForPage(items, current) is { } key && IndexOfKey(items, _selectedKey) < 0)
        {
            _selectedKey = key;
        }

        NavBar.SetItems(items, _selectedKey);
        BackButton.Visibility = NavigationService.Instance.CanGoBack
            ? Visibility.Visible
            : Visibility.Collapsed;
        TitleText.Text = inServer ? "TamizChat — Development server" : "TamizChat";
    }

    private static string? KeyForPage(IReadOnlyList<NavBarItem> items, Type? page) =>
        items.FirstOrDefault(i => i.Page == page)?.Key;

    private static int IndexOfKey(IReadOnlyList<NavBarItem> items, string key)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Sizes the window in layout units and puts it in the middle of the screen.
    ///
    /// AppWindow.Resize works in physical pixels, so passing a layout size
    /// straight through gives a window that is far too small on a scaled display
    /// — at 250% a "1100x760" window only has 440x304 of usable layout space,
    /// and the page gets clipped.
    /// </summary>
    private static void SizeAndCentre(AppWindow appWindow, IntPtr handle, int width, int height)
    {
        var scale = GetDpiForWindow(handle) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var size = new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
        appWindow.Resize(size);

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        appWindow.Move(new Windows.Graphics.PointInt32(
            area.WorkArea.X + ((area.WorkArea.Width - size.Width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - size.Height) / 2)));
    }

    /// <summary>
    /// Walks the whole navigation graph and records what the shell ended up
    /// showing, so the phase can be checked without a person clicking through it.
    /// </summary>
    private void RunSelfTest()
    {
        var log = new StringBuilder();

        void Step(string what, Action action)
        {
            try
            {
                action();
                var current = NavigationService.Instance.CurrentPageType?.Name ?? "none";
                log.AppendLine(
                    $"OK    {what,-28} page={current,-12} selected={_selectedKey,-11} " +
                    $"back={(NavigationService.Instance.CanGoBack ? "yes" : "no")}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"FAIL  {what}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        NavBarItem Item(IReadOnlyList<NavBarItem> set, string key) => set.First(i => i.Key == key);

        Step("start", () => { });
        Step("home -> servers", () => OnNavItemInvoked(null, Item(ShellItems.PreServer, "servers")));
        Step("servers -> settings", () => OnNavItemInvoked(null, Item(ShellItems.PreServer, "settings")));
        Step("join server (drill)", EnterServer);
        Step("room -> chat (slide)", () => OnNavItemInvoked(null, Item(ShellItems.InServer, "chat")));
        Step("chat -> paint (slide)", () => OnNavItemInvoked(null, Item(ShellItems.InServer, "paint")));
        Step("paint -> room (slide)", () => OnNavItemInvoked(null, Item(ShellItems.InServer, "room")));

        // The bug this replaced: two items sharing a page made the selection stick
        // to whichever one came first in the list.
        Step("chat then paint distinct", () =>
        {
            OnNavItemInvoked(null, Item(ShellItems.InServer, "chat"));
            if (_selectedKey != "chat") throw new InvalidOperationException("chat did not take the selection");
            OnNavItemInvoked(null, Item(ShellItems.InServer, "paint"));
            if (_selectedKey != "paint") throw new InvalidOperationException("paint did not take the selection");
        });

        Step("mic toggle", () =>
        {
            OnNavStateChanged(null, new NavBarStateEventArgs(Item(ShellItems.InServer, "mic"), false, null));
            if (LastStateChange != "mic=off") throw new InvalidOperationException(LastStateChange);
        });
        Step("voice changer menu", () =>
        {
            OnNavStateChanged(null, new NavBarStateEventArgs(Item(ShellItems.InServer, "voice"), true, "Robot"));
            if (LastStateChange != "voice=Robot") throw new InvalidOperationException(LastStateChange);
        });

        Step("disconnect (drill out)", () => OnNavItemInvoked(null, Item(ShellItems.InServer, "disconnect")));

        log.AppendLine($"pre-server items = {ShellItems.PreServer.Count} " +
                       $"(home at index {IndexOfKey(ShellItems.PreServer, "home")})");
        log.AppendLine($"in-server items  = {ShellItems.InServer.Count}, overflow at {NavBar.MaxPrimaryItems}");
        log.AppendLine($"servers saved    = {ServerStore.Servers.Count} -> {ServerStore.FilePath}");

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), log.ToString());

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Close();
        timer.Start();
    }
}
