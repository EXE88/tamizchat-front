using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TamizChat.Core.Protocol;
using TamizChat.Localization;
using TamizChat.Services;

namespace TamizChat.Controls;

/// <summary>
/// The right-click menu on a person: how they sound to you, and — if you are
/// allowed — what you can do to them.
///
/// Entries are built from the permissions the server reported, so an ordinary
/// user sees only the volume control and the details. That is politeness rather
/// than security: the server checks every action again, and refuses anything
/// this menu should not have offered.
/// </summary>
public static class MemberMenu
{
    public static void Attach(FrameworkElement target, Func<User> member)
    {
        target.RightTapped += (_, e) =>
        {
            e.Handled = true;
            Build(target, member()).ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        };
    }

    private static MenuFlyout Build(FrameworkElement anchor, User member)
    {
        var session = ServerSession.Instance;
        var menu = new MenuFlyout();
        var isSelf = member.ClientUuid == session.MyUuid;

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = member.Username,
            IsEnabled = false,
        });

        menu.Items.Add(new MenuFlyoutSeparator());

        // Volume is yours alone — it changes nothing for anyone else, so it is
        // offered for everybody except yourself.
        if (!isSelf)
        {
            menu.Items.Add(VolumeItem(anchor, member));
        }

        menu.Items.Add(DetailsItem(member));

        if (isSelf || !session.IsModerator)
        {
            return menu;
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        if (session.Can("mute"))
        {
            var muted = member.Muted;
            menu.Items.Add(Action(
                Loc.Get(muted ? "Member.Unmute" : "Member.Mute"),
                () => muted
                    ? session.UnmuteAsync(member.ClientUuid)
                    : session.MuteAsync(member.ClientUuid, "", 0)));
        }

        if (session.Can("move_users"))
        {
            menu.Items.Add(MoveMenu(member));
        }

        if (session.Can("manage_roles") && session.Roles.Count > 0)
        {
            menu.Items.Add(RolesMenu(member));
        }

        if (session.Can("kick"))
        {
            menu.Items.Add(Action(Loc.Get("Member.Kick"), () => session.KickAsync(member.ClientUuid, "")));
        }

        if (session.Can("ban"))
        {
            menu.Items.Add(Action(Loc.Get("Member.Ban"), () => session.BanAsync(member.ClientUuid, "", 0)));
        }

        return menu;
    }

    /// <summary>
    /// Opens a small flyout holding a real slider.
    ///
    /// A menu cannot host arbitrary content — `MenuFlyoutItem` takes text and
    /// nothing else — so a slider has to live in a flyout of its own. It is one
    /// extra click, and it is a genuine continuous control rather than a list of
    /// preset percentages to hunt through by ear.
    /// </summary>
    private static MenuFlyoutItemBase VolumeItem(FrameworkElement anchor, User member)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get("Member.Volume") };

        item.Click += (_, _) =>
        {
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 200,
                StepFrequency = 5,
                Value = VoiceService.Instance.GetVolume(member.ClientUuid) * 100,
                Width = 200,
            };

            slider.ValueChanged += (_, _) =>
                VoiceService.Instance.SetVolume(member.ClientUuid, slider.Value / 100);

            var panel = new StackPanel { Spacing = 4, Padding = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get("Member.VolumeFor", member.Username),
                FontSize = 12,
            });
            panel.Children.Add(slider);

            new Flyout { Content = panel }.ShowAt(anchor);
        };

        return item;
    }

    private static MenuFlyoutItem DetailsItem(User member)
    {
        var roles = member.Roles.Count == 0
            ? Loc.Get("Member.NoRoles")
            : string.Join(", ", member.Roles.Select(NameOfRole));

        var item = new MenuFlyoutItem { Text = Loc.Get("Member.Roles", roles), IsEnabled = false };
        return item;
    }

    private static string NameOfRole(string id) =>
        ServerSession.Instance.Roles.FirstOrDefault(r => r.Id == id)?.Name ?? id;

    private static MenuFlyoutSubItem MoveMenu(User member)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Member.MoveTo") };

        foreach (var room in ServerSession.Instance.Rooms)
        {
            if (room.Id == member.RoomId)
            {
                continue;
            }

            var target = room;
            sub.Items.Add(Action(room.Name, () => ServerSession.Instance.MoveAsync(member.ClientUuid, target.Id)));
        }

        return sub;
    }

    private static MenuFlyoutSubItem RolesMenu(User member)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Member.RolesMenu") };

        foreach (var role in ServerSession.Instance.Roles)
        {
            var held = member.Roles.Contains(role.Id);
            var current = role;

            // A toggle rather than separate grant and revoke lists: the state is
            // the useful information, and it halves the depth of the menu.
            var item = new ToggleMenuFlyoutItem { Text = role.Name, IsChecked = held };
            item.Click += (_, _) => Run(() => held
                ? ServerSession.Instance.RevokeRoleAsync(member.ClientUuid, current.Id)
                : ServerSession.Instance.GrantRoleAsync(member.ClientUuid, current.Id));

            sub.Items.Add(item);
        }

        return sub;
    }

    private static MenuFlyoutItem Action(string text, Func<Task> run)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => Run(run);
        return item;
    }

    /// <summary>
    /// Runs a moderation call without letting a refusal reach the dispatcher.
    ///
    /// The server rejects anything the priority rule forbids — acting on someone
    /// at or above your own level — and that arrives as a failed request. It is
    /// a normal outcome, not a crash.
    /// </summary>
    private static async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // The room tree refreshes either way, so the UI stays truthful.
        }
    }
}
