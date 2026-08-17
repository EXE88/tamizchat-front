using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    /// <summary>
    /// Which render is the current one.
    ///
    /// Every section that fetches from the server finishes after an await, by
    /// which time another render may have started — a click, or a presence event
    /// arriving. Without this, two runs each cleared the page and then each
    /// appended their own copy, which is exactly what made the playlists card
    /// appear twice after adding a playlist.
    /// </summary>
    private int _render;

    private bool Stale(int token) => token != _render;

    private void Render()
    {
        var token = ++_render;

        RenderTabs();

        Body.Children.Clear();

        switch (_tab)
        {
            case "users":
                RenderUsers();
                break;

            case "bans":
                _ = RenderBansAsync(token);
                break;

            case "roles":
                RenderRoles();
                break;

            case "rooms":
                RenderRooms();
                break;

            case "bots":
                _ = RenderBotsAsync(token);
                break;
        }

        Reveal();
    }

    /// <summary>
    /// A short rise-and-fade on the section body.
    ///
    /// The page is rebuilt wholesale on every change, so without this a tab
    /// switch or a refresh is an instantaneous swap with nothing to follow. The
    /// transform is on the panel, not on each row, so the cost does not grow
    /// with the number of rows.
    /// </summary>
    private void Reveal()
    {
        var slide = new TranslateTransform { Y = 10 };
        Body.RenderTransform = slide;
        Body.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var story = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = ease,
        };

        Storyboard.SetTarget(fade, Body);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);

        var rise = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = ease,
        };

        Storyboard.SetTarget(rise, slide);
        Storyboard.SetTargetProperty(rise, "Y");
        story.Children.Add(rise);

        story.Begin();
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
            var face = new AvatarView(user.Username, size: 28);
            face.SetUser(user);
            header.Children.Add(face);
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

            // Granting: only roles they do not already have. A role that is not
            // marked default and that they hold can be taken back — the tag is
            // also a way to do it, but a tag is not an obvious button, and
            // "I could not revoke it" turned out to mean "I never found it".
            var grantable = session.Roles.Where(r => !user.Roles.Contains(r.Id) && !r.IsDefault).ToList();
            if (grantable.Count > 0)
            {
                var grant = new DropDownButton { Content = Loc.Get("Admin.GrantRole") };
                var menu = new MenuFlyout();

                foreach (var role in grantable)
                {
                    var entry = new MenuFlyoutItem { Text = role.Name };
                    entry.Click += (_, _) => Run(
                        () => session.GrantRoleAsync(user.ClientUuid, role.Id),
                        Loc.Get("Admin.RoleGranted", role.Name, user.Username));
                    menu.Items.Add(entry);
                }

                grant.Flyout = menu;
                actions.Children.Add(grant);
            }

            var revocable = session.Roles
                .Where(r => user.Roles.Contains(r.Id) && !r.IsDefault)
                .ToList();

            if (revocable.Count > 0)
            {
                var revoke = new DropDownButton { Content = Loc.Get("Admin.RevokeRoleButton") };
                var menu = new MenuFlyout();

                foreach (var role in revocable)
                {
                    var entry = new MenuFlyoutItem { Text = role.Name };
                    entry.Click += (_, _) => Run(
                        () => session.RevokeRoleAsync(user.ClientUuid, role.Id),
                        Loc.Get("Admin.RoleRevoked", role.Name, user.Username));
                    menu.Items.Add(entry);
                }

                revoke.Flyout = menu;
                actions.Children.Add(revoke);
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

    private async Task RenderBansAsync(int token)
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

        // The tab may have been changed, or the page rebuilt, while that request
        // was in flight.
        if (Stale(token))
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

        var add = Opens(Loc.Get("Admin.NewRole"), () => EditRoleAsync(null));
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

            actions.Children.Add(Opens(Loc.Get("Admin.Edit"), () => EditRoleAsync(role)));

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
    }

    // --- rooms ---

    private void RenderRooms()
    {
        var session = ServerSession.Instance;

        var add = Opens(Loc.Get("Admin.NewRoom"), () => EditRoomAsync(null));
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

            actions.Children.Add(Opens(Loc.Get("Admin.Edit"), () => EditRoomAsync(room)));
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
    }

    // --- bots ---

    /// <summary>Which bot's playlists are open. Only one at a time.</summary>
    private string _openBot = "";

    private async Task RenderBotsAsync(int token)
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

        if (Stale(token))
        {
            return;
        }

        Body.Children.Clear();

        var add = Opens(Loc.Get("Admin.NewBot"), () => EditBotAsync(null));
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
            await RenderPlaylistsAsync(_openBot, slot, token);
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

        if (bot.State == "playing" && bot.Track is { } track)
        {
            header.Children.Add(new TextBlock
            {
                Text = "♪ " + track.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TcAccentBrush"],
            });
        }

        card.Children.Add(header);

        var room = session.Rooms.FirstOrDefault(r => r.Id == bot.RoomId);

        var bits = new List<string>
        {
            room is null
                ? Loc.Get("Admin.BotState.idle")
                : Loc.Get("Admin.BotInRoom", room.Name) + " · " + Loc.Get("Admin.BotState." + bot.State),
            Loc.Get("Admin.BotTracks", bot.TrackCount),
            bot.PlaylistName.Length > 0
                ? Loc.Get("Admin.BotPlaying", bot.PlaylistName)
                : Loc.Get("Admin.BotNoPlaylist"),
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

        // Playback. A bot that is not in a room is silent wherever it is
        // pointed, so the room picker comes first and everything else is
        // disabled until it has somewhere to play.
        if (session.Can("control_bots"))
        {
            card.Children.Add(BotControls(bot, room));
        }

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
            Render();
        };

        actions.Children.Add(playlists);
        actions.Children.Add(Opens(Loc.Get("Admin.Edit"), () => EditBotAsync(bot)));
        actions.Children.Add(Danger(Loc.Get("Admin.Delete"), () => DeleteBotAsync(bot)));

        card.Children.Add(actions);
        return card;
    }

    /// <summary>The transport: where the bot is, and what it is doing there.</summary>
    private StackPanel BotControls(Bot bot, Room? room)
    {
        var session = ServerSession.Instance;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var picker = new DropDownButton
        {
            Content = room is null ? Loc.Get("Admin.BotSendToRoom") : Loc.Get("Admin.BotInRoom", room.Name),
        };

        var menu = new MenuFlyout();

        foreach (var candidate in session.Rooms)
        {
            var entry = new MenuFlyoutItem { Text = candidate.Name, IsEnabled = candidate.Id != bot.RoomId };
            entry.Click += (_, _) => Run(() => session.MoveBotAsync(bot.Id, candidate.Id));
            menu.Items.Add(entry);
        }

        if (room is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var out_ = new MenuFlyoutItem { Text = Loc.Get("Admin.BotLeaveRoom") };
            out_.Click += (_, _) => Run(() => session.MoveBotAsync(bot.Id, ""));
            menu.Items.Add(out_);
        }

        picker.Flyout = menu;
        row.Children.Add(picker);

        // Playing needs a room and something to play; saying so up front beats a
        // refusal after the press.
        var playable = bot.Enabled && room is not null && bot.TrackCount > 0;
        var playing = bot.State == "playing";

        var play = new Button
        {
            Content = Loc.Get(playing ? "Admin.BotStop" : "Admin.BotPlay"),
            IsEnabled = playable,
        };

        play.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, playing ? "stop" : "play"));
        row.Children.Add(play);

        var prev = new Button { Content = "⏮", IsEnabled = playable };
        prev.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, "prev"));
        row.Children.Add(prev);

        var next = new Button { Content = "⏭", IsEnabled = playable };
        next.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, "next"));
        row.Children.Add(next);

        if (!playable && bot.Enabled)
        {
            row.Children.Add(new TextBlock
            {
                Text = room is null ? Loc.Get("Admin.BotNeedsRoom") : Loc.Get("Admin.BotNeedsTracks"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });
        }

        return row;
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

    private async Task RenderPlaylistsAsync(string botId, int slot, int token)
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

        if (Stale(token) || _openBot != botId)
        {
            return;
        }

        var panel = Card();
        panel.Margin = new Thickness(24, 0, 0, 0);
        panel.ChildrenTransitions = [new EntranceThemeTransition { FromVerticalOffset = 12 }, new RepositionThemeTransition()];

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

        header.Children.Add(Opens(Loc.Get("Admin.NewPlaylist"), () => NamePlaylistAsync(botId, null)));
        panel.Children.Add(header);

        foreach (var list in lists.Playlists)
        {
            panel.Children.Add(PlaylistExpander(botId, list, list.Id == lists.Active));
        }

        if (lists.Playlists.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get("Admin.NoPlaylists"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });
        }

        // The list may have been rebuilt while the playlists were being fetched,
        // so the slot is clamped rather than trusted.
        Body.Children.Insert(Math.Min(slot, Body.Children.Count), panel);
    }

    /// <summary>
    /// One playlist, as something that opens.
    ///
    /// The tracks live inside the expander rather than behind a button, because
    /// "what is in this playlist" is the question being asked, and they are
    /// fetched only when it is opened — a server with a dozen playlists should
    /// not read every folder to draw a list of names.
    /// </summary>
    private Expander PlaylistExpander(string botId, BotPlaylist list, bool active)
    {
        var session = ServerSession.Instance;

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        title.Children.Add(new TextBlock
        {
            Text = list.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextPrimaryBrush"],
        });

        title.Children.Add(new TextBlock
        {
            Text = Loc.Get("Admin.BotTracks", list.TrackCount),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        });

        if (active)
        {
            title.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["TcAccentBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Loc.Get("Admin.PlaylistActive"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TcOnAccentBrush"],
                },
            });
        }

        var body = new StackPanel { Spacing = 6 };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (!active)
        {
            actions.Children.Add(Action(
                Loc.Get("Admin.UsePlaylist"),
                () => session.SelectPlaylistAsync(botId, list.Id)));
        }

        actions.Children.Add(Opens(Loc.Get("Admin.AddTracks"), () => AddTracksAsync(botId, list.Id)));
        actions.Children.Add(Opens(Loc.Get("Admin.Rename"), () => NamePlaylistAsync(botId, list)));
        actions.Children.Add(Action(
            Loc.Get("Admin.Delete"),
            () => ServerSession.Instance.DeletePlaylistAsync(botId, list.Id),
            danger: true));

        body.Children.Add(actions);

        var tracks = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 6, 0, 0),
            ChildrenTransitions = [new EntranceThemeTransition { FromVerticalOffset = 8 }],
        };

        body.Children.Add(tracks);

        var expander = new Expander
        {
            Header = title,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = list.Id == _openPlaylist,
        };

        // Loading on expand keeps the common case — a glance at the list of
        // playlists — down to one request.
        expander.Expanding += (_, _) =>
        {
            _openPlaylist = list.Id;
            _ = FillTracksAsync(tracks, botId, list);
        };

        expander.Collapsed += (_, _) =>
        {
            if (_openPlaylist == list.Id)
            {
                _openPlaylist = "";
            }
        };

        if (expander.IsExpanded)
        {
            _ = FillTracksAsync(tracks, botId, list);
        }

        return expander;
    }

    private string _openPlaylist = "";

    private async Task FillTracksAsync(StackPanel into, string botId, BotPlaylist list)
    {
        into.Children.Clear();
        into.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });

        IReadOnlyList<BotTrack> tracks;

        try
        {
            tracks = await ServerSession.Instance.GetPlaylistTracksAsync(botId, list.Id);
        }
        catch (Exception ex)
        {
            into.Children.Clear();
            Fail(ex);
            return;
        }

        into.Children.Clear();

        if (tracks.Count == 0)
        {
            into.Children.Add(new TextBlock
            {
                Text = Loc.Get("Admin.NoTracks"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            });

            return;
        }

        foreach (var track in tracks)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"{track.Index + 1}. {track.Title}",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
            };

            row.Children.Add(label);

            // The index is a position in this playlist, and every delete shifts
            // the ones after it — which is why the list is redrawn after one.
            var remove = new Button
            {
                Content = "✕",
                Padding = new Thickness(8, 2, 8, 2),
                Foreground = (Brush)Application.Current.Resources["TcDangerBrush"],
            };

            ToolTipService.SetToolTip(remove, Loc.Get("Admin.Delete"));
            remove.Click += (_, _) => Run(() =>
                ServerSession.Instance.DeleteTrackAsync(botId, list.Id, track.Index));

            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            into.Children.Add(row);
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
    /// Uploads tracks one at a time, showing which file is going and how far it
    /// has got.
    ///
    /// One at a time rather than all at once: several large files in parallel
    /// compete for the same connection and finish no sooner, and a failure in
    /// the middle of a parallel batch leaves nobody able to say what arrived.
    /// The upload stops at the first refusal, and says which file it was —
    /// everything before it is already in the playlist.
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
        Upload.Visibility = Visibility.Visible;
        UploadBar.Value = 0;

        var done = 0;

        try
        {
            // Converted files go to a temporary folder and are cleaned up after,
            // so a music library never gets .opus copies scattered through it.
            var scratch = Path.Combine(Path.GetTempPath(), "tamizchat-upload");
            Directory.CreateDirectory(scratch);

            foreach (var file in files)
            {
                var index = done + 1;
                var progress = new Progress<double>(percent => UploadBar.Value = percent);

                // A bot plays Opus, and the conversion belongs here: Windows
                // already decodes mp3 and m4a, and this app already carries an
                // Opus encoder for the microphone. The server never transcodes
                // anything.
                UploadLabel.Text = files.Count == 1
                    ? Loc.Get("Admin.Converting", file.Name)
                    : Loc.Get("Admin.ConvertingOf", file.Name, index, files.Count);

                var converted = Path.Combine(scratch,
                    Path.GetFileNameWithoutExtension(file.Name) + ".ogg");

                await TamizChat.Audio.OpusFile.ConvertAsync(file.Path, converted, progress);

                UploadLabel.Text = files.Count == 1
                    ? Loc.Get("Admin.Uploading", file.Name)
                    : Loc.Get("Admin.UploadingOf", file.Name, index, files.Count);

                try
                {
                    await ServerSession.Instance.UploadTrackAsync(botId, playlistId, converted, progress);
                }
                finally
                {
                    File.Delete(converted);
                }

                done++;
            }

            Note(files.Count == 1
                ? Loc.Get("Admin.Uploaded", files[0].Name)
                : Loc.Get("Admin.UploadedCount", done));
        }
        catch (Exception ex)
        {
            // Written down as well as shown: an upload that fails halfway
            // through a conversion is exactly the case where the message on
            // screen is not enough to work out what happened.
            CrashLog.Write("upload", ex);
            Fail(ex);
        }
        finally
        {
            Upload.Visibility = Visibility.Collapsed;
        }

        Render();
    }

    /// <summary>
    /// What can be picked. Anything here is converted to Ogg/Opus on the way up,
    /// because that is the one thing a bot can publish — see OpusFile.
    /// </summary>
    private static readonly string[] AudioExtensions = TamizChat.Audio.OpusFile.SupportedExtensions;

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

    /// <summary>
    /// A button that opens a dialog.
    ///
    /// Unlike Action, this does not wrap the call in Guarded: the dialog does
    /// its own saving and its own refresh afterwards, and wrapping it meant two
    /// renders raced each other — one of them holding the list as it was before
    /// the dialog, which is how a new playlist could fail to appear until the
    /// panel was closed and opened again.
    /// </summary>
    private Button Opens(string label, Func<Task> open)
    {
        var button = new Button { Content = label };

        button.Click += (_, _) => _ = OpenGuarded(open);
        return button;
    }

    private async Task OpenGuarded(Func<Task> open)
    {
        try
        {
            await open();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>A dialog-opening button that destroys something.</summary>
    private Button Danger(string label, Func<Task> open)
    {
        var button = Opens(label, open);
        button.Foreground = (Brush)Application.Current.Resources["TcDangerBrush"];
        return button;
    }

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

    private void Run(Func<Task> action, string? done = null) => _ = Guarded(action, done).ContinueWith(
        _ => { },
        TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>
    /// Runs a server call and shows what came back if it was refused.
    ///
    /// A refusal is a normal outcome here, not a bug: the priority rule means an
    /// administrator can be told no by their own server, and they need to see
    /// that rather than watch a button do nothing.
    /// </summary>
    private async Task Guarded(Func<Task> action, string? done = null)
    {
        Status.Visibility = Visibility.Collapsed;

        try
        {
            await action();

            // Something that worked has to say so. A grant that changed the
            // server but drew nothing back looked exactly like a dead button.
            if (done is not null)
            {
                Note(done);
            }

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
        Status.Foreground = (Brush)Application.Current.Resources["TcDangerBrush"];
        Status.Visibility = Visibility.Visible;
    }

    /// <summary>Confirms something that succeeded, and fades itself away.</summary>
    private void Note(string text)
    {
        Status.Text = text;
        Status.Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"];
        Status.Visibility = Visibility.Visible;

        var mine = ++_note;

        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(
            _ =>
            {
                // Only the most recent note clears itself, or a fast second
                // action would be wiped by the first one's timer.
                if (mine == _note)
                {
                    Status.Visibility = Visibility.Collapsed;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private int _note;
}
