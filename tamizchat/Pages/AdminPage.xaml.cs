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
    private string _tab = "users";

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
        }
    }

    private void RenderTabs()
    {
        Tabs.Children.Clear();

        foreach (var (key, label) in new[]
        {
            ("users", Loc.Get("Admin.Users")),
            ("bans", Loc.Get("Admin.Bans")),
            ("roles", Loc.Get("Admin.Roles")),
            ("rooms", Loc.Get("Admin.Rooms")),
        })
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
