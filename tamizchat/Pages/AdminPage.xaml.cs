using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Controls;
using TamizChat.Core.Protocol;
using TamizChat.Localization;
using TamizChat.Services;

namespace TamizChat.Pages;

/// <summary>
/// Everything a server's administrator does day to day, in the client.
///
/// The point of this page is that running a server should not require SSH and
/// the command-line panel for ordinary work — granting somebody a role, lifting a
/// ban, renaming a room. The CLI panel stays for the things that genuinely belong
/// on the machine: backups, the listen address, the first administrator.
///
/// Every section is rebuilt from scratch when it is shown. These lists are short
/// and only change when somebody presses something, so the simplest correct thing
/// is also fast enough — and it means no stale row can survive a refresh.
/// </summary>
public sealed partial class AdminPage : Page
{
    // Which tab is showing. TAMIZCHAT_ADMIN_TAB is the same scripted entry point
    // as TAMIZCHAT_START_PAGE: it opens straight onto one, so a tab can be
    // screenshotted without driving the interface.
    private string _tab = Environment.GetEnvironmentVariable("TAMIZCHAT_ADMIN_TAB") ?? "users";

    public AdminPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ServerSession.Instance.Changed += OnSessionChanged;
            Translate();
            Render();
        };

        Unloaded += (_, _) => ServerSession.Instance.Changed -= OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Render();

    private void Translate()
    {
        Title.Text = Loc.Get("Admin.Title");
        Subtitle.Text = Loc.Get("Admin.Subtitle");
    }

    private void Render()
    {
        RenderTabs();

        Body.Children.Clear();

        switch (_tab)
        {
            case "users":
                RenderUsers();
                break;

            case "bans":
                _ = RenderBansAsync();
                break;

            case "roles":
                RenderRoles();
                break;

            case "rooms":
                RenderRooms();
                break;

            case "bots":
                _ = RenderBotsAsync();
                break;
        }
    }

    private void RenderTabs()
    {
        Tabs.Children.Clear();

        var tabs = new List<(string Key, string Label)>
        {
            ("users", Loc.Get("Admin.Users")),
            ("bans", Loc.Get("Admin.Bans")),
            ("roles", Loc.Get("Admin.Roles")),
            ("rooms", Loc.Get("Admin.Rooms")),
        };

        // Bots are their own permission: an operator can hand out the music
        // controls without handing over the server's bot configuration. Hiding
        // the tab is manners, not security — the server refuses either way.
        if (ServerSession.Instance.Can("manage_bots"))
        {
            tabs.Add(("bots", Loc.Get("Admin.Bots")));
        }

        foreach (var (key, label) in tabs)
        {
            var selected = key == _tab;

            var button = new Button
            {
                Content = label,
                Padding = new Thickness(16, 7, 16, 7),
                Background = selected
                    ? (Brush)Application.Current.Resources["TcAccentBrush"]
                    : (Brush)Application.Current.Resources["TcSurfaceBrush"],
                Foreground = selected
                    ? (Brush)Application.Current.Resources["TcOnAccentBrush"]
                    : (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            };

            var captured = key;
            button.Click += (_, _) =>
            {
                _tab = captured;
                Render();
            };

            Tabs.Children.Add(button);
        }
    }

    // --- users ---

    private void RenderUsers()
    {
        var session = ServerSession.Instance;

        foreach (var user in session.Users.OrderBy(u => u.Username, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = Card();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new AvatarView(user.Username, size: 28));
            header.Children.Add(new TextBlock
            {
                Text = user.Username,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            });

            // Their roles as tags, and each tag is also the way to take it off.
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var roleId in user.Roles)
            {
                var role = session.Roles.FirstOrDefault(r => r.Id == roleId);
                if (role is null)
                {
                    continue;
                }

                var tag = new RoleTag(role) { Margin = new Thickness(0, 0, 0, 0) };
                ToolTipService.SetToolTip(tag, Loc.Get("Admin.RevokeRole", role.Name));
                tag.Tapped += (_, _) => Run(() => session.RevokeRoleAsync(user.ClientUuid, role.Id));
                tags.Children.Add(tag);
            }

            header.Children.Add(tags);
            card.Children.Add(header);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };

            // Granting: only roles they do not already have.
            var grantable = session.Roles.Where(r => !user.Roles.Contains(r.Id)).ToList();
            if (grantable.Count > 0)
            {
                var grant = new DropDownButton { Content = Loc.Get("Admin.GrantRole") };
                var menu = new MenuFlyout();

                foreach (var role in grantable)
                {
                    var entry = new MenuFlyoutItem { Text = role.Name };
                    entry.Click += (_, _) => Run(() => session.GrantRoleAsync(user.ClientUuid, role.Id));
                    menu.Items.Add(entry);
                }

                grant.Flyout = menu;
                actions.Children.Add(grant);
            }

            if (user.ClientUuid != session.MyUuid)
            {
                actions.Children.Add(Action(Loc.Get("Admin.Mute"), () => session.MuteAsync(user.ClientUuid, "", 0)));
                actions.Children.Add(Action(Loc.Get("Admin.Unmute"), () => session.UnmuteAsync(user.ClientUuid)));
                actions.Children.Add(Action(Loc.Get("Admin.Kick"), () => session.KickAsync(user.ClientUuid, "")));
                actions.Children.Add(Action(Loc.Get("Admin.Ban"), () => session.BanAsync(user.ClientUuid, "", 0), danger: true));
            }

            card.Children.Add(actions);
            Body.Children.Add(card);
        }

        if (session.Users.Count == 0)
        {
            Body.Children.Add(Empty(Loc.Get("Admin.NoUsers")));
        }
    }

    // --- bans ---

    private async Task RenderBansAsync()
    {
        var session = ServerSession.Instance;

        IReadOnlyList<Sanction> sanctions;

        try
        {
            sanctions = await session.GetSanctionsAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        // The tab may have been changed while that request was in flight.
        if (_tab != "bans")
        {
            return;
        }

        Body.Children.Clear();

        foreach (var sanction in sanctions)
        {
            var card = Card();

            card.Children.Add(new TextBlock
            {
                Text = $"{sanction.Username} · {sanction.Kind}",
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            });

            var detail = sanction.ExpiresAt == 0
                ? Loc.Get("Admin.Permanent")
                : Loc.Get("Admin.Until", DateTimeOffset.FromUnixTimeSeconds(sanction.ExpiresAt).LocalDateTime.ToString("g"));

            if (!string.IsNullOrWhiteSpace(sanction.Reason))
            {
                detail += $" · {sanction.Reason}";
            }

            card.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            var lift = Action(
                Loc.Get(sanction.Kind == "mute" ? "Admin.Unmute" : "Admin.Unban"),
                () => sanction.Kind == "mute"
                    ? session.UnmuteAsync(sanction.ClientUuid)
                    : session.UnbanAsync(sanction.ClientUuid));

            lift.Margin = new Thickness(0, 8, 0, 0);
            lift.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(lift);

            Body.Children.Add(card);
        }

        if (sanctions.Count == 0)
        {
            Body.Children.Add(Empty(Loc.Get("Admin.NoBans")));
        }
    }

    // --- roles ---

    private void RenderRoles()
    {
        var session = ServerSession.Instance;

        var add = Action(Loc.Get("Admin.NewRole"), () => EditRoleAsync(null));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        Body.Children.Add(add);

        foreach (var role in session.Roles.OrderBy(r => r.Priority))
        {
            var card = Card();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new RoleTag(role));
            header.Children.Add(new TextBlock
            {
                Text = Loc.Get("Admin.Priority", role.Priority),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });
            card.Children.Add(header);

            card.Children.Add(new TextBlock
            {
                Text = role.Permissions.Count == 0
                    ? Loc.Get("Admin.NoPermissions")
                    : string.Join(" · ", role.Permissions),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };

            actions.Children.Add(Action(Loc.Get("Admin.Edit"), () => EditRoleAsync(role)));

            // A default role is what everybody gets on arrival; deleting it would
            // leave new users with nothing at all.
            if (!role.IsDefault)
            {
                actions.Children.Add(Action(Loc.Get("Admin.Delete"), () => session.DeleteRoleAsync(role.Id), danger: true));
            }

            card.Children.Add(actions);
            Body.Children.Add(card);
        }
    }

    private async Task EditRoleAsync(Role? existing)
    {
        var dialog = new RoleDialog(existing) { XamlRoot = XamlRoot };
        var spec = await dialog.ShowAsync();

        if (spec is null)
        {
            return;
        }

        await Guarded(() => existing is null
            ? ServerSession.Instance.CreateRoleAsync(spec)
            : ServerSession.Instance.UpdateRoleAsync(spec));

        Render();
    }

    // --- rooms ---

    private void RenderRooms()
    {
        var session = ServerSession.Instance;

        var add = Action(Loc.Get("Admin.NewRoom"), () => EditRoomAsync(null));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        Body.Children.Add(add);

        foreach (var room in session.Rooms)
        {
            var card = Card();

            card.Children.Add(new TextBlock
            {
                Text = room.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
            });

            var bits = new List<string>
            {
                Loc.Get("Admin.Capacity", room.Capacity == 0 ? Loc.Get("Admin.Default") : room.Capacity.ToString()),
                Loc.Get(room.HasPassword ? "Admin.HasPassword" : "Admin.NoPassword"),
            };

            if (!string.IsNullOrEmpty(room.RequiredRoleId))
            {
                var required = session.Roles.FirstOrDefault(r => r.Id == room.RequiredRoleId);
                bits.Add(Loc.Get("Admin.Requires", required?.Name ?? room.RequiredRoleId));
            }

            card.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", bits),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };

            actions.Children.Add(Action(Loc.Get("Admin.Edit"), () => EditRoomAsync(room)));
            actions.Children.Add(Action(Loc.Get("Admin.Delete"), () => session.DeleteRoomAsync(room.Id), danger: true));

            card.Children.Add(actions);
            Body.Children.Add(card);
        }
    }

    private async Task EditRoomAsync(Room? existing)
    {
        var dialog = new RoomDialog(existing, ServerSession.Instance.Roles) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result is null)
        {
            return;
        }

        await Guarded(() => existing is null
            ? ServerSession.Instance.CreateRoomAsync(result.Create)
            : ServerSession.Instance.UpdateRoomAsync(result.Update));

        Render();
    }

    // --- bots ---

    /// <summary>Which bot's playlists are open, and which playlist's tracks.</summary>
    private string _openBot = "";

    private string _openPlaylist = "";

    private async Task RenderBotsAsync()
    {
        var session = ServerSession.Instance;

        IReadOnlyList<Bot> bots;

        try
        {
            bots = await session.GetBotsAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        // The tab may have been changed while that request was in flight.
        if (_tab != "bots")
        {
            return;
        }

        Body.Children.Clear();

        var add = Action(Loc.Get("Admin.NewBot"), () => EditBotAsync(null));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        Body.Children.Add(add);

        // Where the open bot's playlists belong: directly under that bot's card,
        // not at the end of the list, where they would appear to belong to
        // whichever bot happens to be last.
        var slot = -1;

        foreach (var bot in bots.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Body.Children.Add(BotCard(bot));

            if (bot.Id == _openBot)
            {
                slot = Body.Children.Count;
            }
        }

        if (bots.Count == 0)
        {
            Body.Children.Add(Empty(Loc.Get("Admin.NoBots")));
        }

        if (slot >= 0)
        {
            await RenderPlaylistsAsync(_openBot, slot);
        }
    }

    private StackPanel BotCard(Bot bot)
    {
        var session = ServerSession.Instance;
        var card = Card();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        // A dot in the bot's own colour, the same one that identifies it in the
        // room tree — so the two views are recognisably the same bot.
        header.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Colours.Parse(bot.Color, Microsoft.UI.Colors.Gray)),
        });

        header.Children.Add(new TextBlock
        {
            Text = bot.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        });

        card.Children.Add(header);

        var bits = new List<string>
        {
            Loc.Get("Admin.BotState." + (bot.State.Length == 0 ? "idle" : bot.State)),
            Loc.Get("Admin.BotTracks", bot.TrackCount),
            bot.PlaylistName.Length > 0
                ? Loc.Get("Admin.BotPlaying", bot.PlaylistName)
                : Loc.Get("Admin.BotLibrary"),
        };

        if (!bot.Enabled)
        {
            bits.Add(Loc.Get("Admin.BotDisabled"));
        }

        card.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", bits),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var open = bot.Id == _openBot;
        var playlists = new Button { Content = Loc.Get(open ? "Admin.HidePlaylists" : "Admin.Playlists") };
        playlists.Click += (_, _) =>
        {
            _openBot = open ? "" : bot.Id;
            _openPlaylist = "";
            Render();
        };

        actions.Children.Add(playlists);
        actions.Children.Add(Action(Loc.Get("Admin.Edit"), () => EditBotAsync(bot)));
        actions.Children.Add(Action(Loc.Get("Admin.Delete"), () => DeleteBotAsync(bot), danger: true));

        card.Children.Add(actions);
        return card;
    }

    private async Task EditBotAsync(Bot? existing)
    {
        var dialog = new BotDialog(existing) { XamlRoot = XamlRoot };
        var spec = await dialog.ShowAsync();

        if (spec is null)
        {
            return;
        }

        await Guarded(() => existing is null
            ? ServerSession.Instance.CreateBotAsync(spec)
            : ServerSession.Instance.UpdateBotAsync(spec));
    }

    /// <summary>
    /// Deleting a bot takes its uploaded music with it, which no dialog can undo
    /// — so it asks first. Nothing else in this page does, because nothing else
    /// destroys files.
    /// </summary>
    private async Task DeleteBotAsync(Bot bot)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Admin.DeleteBot", bot.Name),
            Content = Loc.Get("Admin.DeleteBotBody"),
            PrimaryButtonText = Loc.Get("Admin.Delete"),
            CloseButtonText = Loc.Get("Admin.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        dialog.Themed(XamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (_openBot == bot.Id)
        {
            _openBot = "";
        }

        await Guarded(() => ServerSession.Instance.DeleteBotAsync(bot.Id));
    }

    private async Task RenderPlaylistsAsync(string botId, int slot)
    {
        var session = ServerSession.Instance;

        BotPlaylistList lists;

        try
        {
            lists = await session.GetPlaylistsAsync(botId);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        if (_tab != "bots" || _openBot != botId)
        {
            return;
        }

        var panel = Card();
        panel.Margin = new Thickness(24, 0, 0, 0);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8),
        };

        header.Children.Add(new TextBlock
        {
            Text = Loc.Get("Admin.Playlists"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        });

        header.Children.Add(Action(Loc.Get("Admin.NewPlaylist"), () => NamePlaylistAsync(botId, null)));
        panel.Children.Add(header);

        // The library is a row like any other, so "play everything loose in the
        // bot's own folder" is reachable rather than only being the state you
        // end up in after deleting a playlist.
        panel.Children.Add(PlaylistRow(botId, null, lists.Active.Length == 0));

        foreach (var list in lists.Playlists)
        {
            panel.Children.Add(PlaylistRow(botId, list, list.Id == lists.Active));

            if (list.Id == _openPlaylist)
            {
                await RenderTracksAsync(panel, botId, list);
            }
        }

        // The list may have been rebuilt while the playlists were being fetched,
        // so the slot is clamped rather than trusted.
        Body.Children.Insert(Math.Min(slot, Body.Children.Count), panel);
    }

    private StackPanel PlaylistRow(string botId, BotPlaylist? list, bool active)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 4),
        };

        var label = list is null
            ? Loc.Get("Admin.BotLibrary")
            : $"{list.Name} · {Loc.Get("Admin.BotTracks", list.TrackCount)}";

        row.Children.Add(new TextBlock
        {
            Text = active ? "▶ " + label : label,
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[
                active ? "TcTextPrimaryBrush" : "TcTextSecondaryBrush"],
        });

        if (!active)
        {
            row.Children.Add(Action(
                Loc.Get("Admin.UsePlaylist"),
                () => ServerSession.Instance.SelectPlaylistAsync(botId, list?.Id ?? "")));
        }

        if (list is null)
        {
            return row;
        }

        var open = list.Id == _openPlaylist;
        var tracks = new Button { Content = Loc.Get(open ? "Admin.HideTracks" : "Admin.Tracks") };
        tracks.Click += (_, _) =>
        {
            _openPlaylist = open ? "" : list.Id;
            Render();
        };

        row.Children.Add(tracks);
        row.Children.Add(Action(Loc.Get("Admin.AddTracks"), () => AddTracksAsync(botId, list.Id)));
        row.Children.Add(Action(Loc.Get("Admin.Rename"), () => NamePlaylistAsync(botId, list)));
        row.Children.Add(Action(
            Loc.Get("Admin.Delete"),
            () => ServerSession.Instance.DeletePlaylistAsync(botId, list.Id),
            danger: true));

        return row;
    }

    private async Task RenderTracksAsync(StackPanel panel, string botId, BotPlaylist list)
    {
        IReadOnlyList<BotTrack> tracks;

        try
        {
            tracks = await ServerSession.Instance.GetPlaylistTracksAsync(botId, list.Id);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        if (_openPlaylist != list.Id)
        {
            return;
        }

        if (tracks.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get("Admin.NoTracks"),
                FontSize = 12,
                Margin = new Thickness(24, 0, 0, 6),
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            return;
        }

        foreach (var track in tracks)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(24, 2, 0, 2),
            };

            row.Children.Add(new TextBlock
            {
                Text = $"{track.Index + 1}. {track.Title}",
                Width = 280,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            // The index is a position in this playlist, and every delete shifts
            // the ones after it — which is why the whole tab is redrawn after.
            row.Children.Add(Action(
                Loc.Get("Admin.Delete"),
                () => ServerSession.Instance.DeleteTrackAsync(botId, list.Id, track.Index),
                danger: true));

            panel.Children.Add(row);
        }
    }

    private async Task NamePlaylistAsync(string botId, BotPlaylist? existing)
    {
        var box = new TextBox
        {
            Header = Loc.Get("Admin.PlaylistName"),
            Text = existing?.Name ?? "",
            Width = 320,
        };

        var dialog = new ContentDialog
        {
            Title = Loc.Get(existing is null ? "Admin.NewPlaylist" : "Admin.Rename"),
            Content = box,
            PrimaryButtonText = Loc.Get("Admin.Save"),
            CloseButtonText = Loc.Get("Admin.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["TcAccentButtonStyle"],
        };

        dialog.Themed(XamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        await Guarded(() => existing is null
            ? ServerSession.Instance.CreatePlaylistAsync(botId, name)
            : ServerSession.Instance.RenamePlaylistAsync(botId, existing.Id, name));
    }

    /// <summary>
    /// Uploads tracks one at a time, and stops at the first refusal.
    ///
    /// Carrying on after one is rejected would leave the user guessing which of
    /// twenty files actually arrived; the message says which one failed, and
    /// whatever went before it is already in the playlist.
    /// </summary>
    private async Task AddTracksAsync(string botId, string playlistId)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var extension in AudioExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        // An unpackaged app has no implicit window for a picker to sit on, and
        // it throws without one.
        var window = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return;
        }

        Status.Visibility = Visibility.Collapsed;

        try
        {
            foreach (var file in files)
            {
                await ServerSession.Instance.UploadTrackAsync(botId, playlistId, file.Path);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }

        Render();
    }

    /// <summary>
    /// What the server's scanner accepts. Filtering here as well means somebody
    /// picking a .txt is told by the file picker rather than by a refusal.
    /// </summary>
    private static readonly string[] AudioExtensions =
        [".mp3", ".ogg", ".opus", ".flac", ".m4a", ".aac", ".wav", ".wma"];

    // --- shared bits ---

    private static StackPanel Card() => new()
    {
        Padding = new Thickness(16),
        Spacing = 0,
        Background = (Brush)Application.Current.Resources["TcSurfaceBrush"],
        CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"],
    };

    private static TextBlock Empty(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 20, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
    };

    private Button Action(string label, Func<Task> action, bool danger = false)
    {
        var button = new Button { Content = label };

        if (danger)
        {
            button.Foreground = (Brush)Application.Current.Resources["TcDangerBrush"];
        }

        button.Click += (_, _) => Run(action);
        return button;
    }

    private void Run(Func<Task> action) => _ = Guarded(action).ContinueWith(
        _ => { },
        TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>
    /// Runs a server call and shows what came back if it was refused.
    ///
    /// A refusal is a normal outcome here, not a bug: the priority rule means an
    /// administrator can be told no by their own server, and they need to see
    /// that rather than watch a button do nothing.
    /// </summary>
    private async Task Guarded(Func<Task> action)
    {
        Status.Visibility = Visibility.Collapsed;

        try
        {
            await action();
            Render();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        Status.Text = ex.Message;
        Status.Visibility = Visibility.Visible;
    }
}
