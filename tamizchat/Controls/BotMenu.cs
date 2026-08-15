using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TamizChat.Core.Protocol;
using TamizChat.Localization;
using TamizChat.Services;

namespace TamizChat.Controls;

/// <summary>
/// The right-click menu on a bot: what it plays, and where.
///
/// Like the member menu, entries are built from the permissions the server
/// reported. That is politeness rather than security — every action is checked
/// again on the server, and a refusal comes back as a failed request.
/// </summary>
public static class BotMenu
{
    public static void Attach(FrameworkElement target, Func<Bot> bot)
    {
        target.RightTapped += (_, e) =>
        {
            e.Handled = true;
            Build(bot()).ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        };
    }

    private static MenuFlyout Build(Bot bot)
    {
        var session = ServerSession.Instance;
        var menu = new MenuFlyout();

        menu.Items.Add(new MenuFlyoutItem { Text = bot.Name, IsEnabled = false });

        if (bot.Track is { } track)
        {
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = $"♪ {track.Title}  ({bot.TrackCount})",
                IsEnabled = false,
            });
        }

        if (!session.Can("control_bots"))
        {
            return menu;
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var playing = bot.State == "playing";

        // Playing needs something to play. Saying so on a disabled entry beats
        // a button that answers with an error.
        var play = new MenuFlyoutItem
        {
            Text = Loc.Get(playing ? "Admin.BotStop" : "Admin.BotPlay"),
            IsEnabled = bot.Enabled && (playing || bot.TrackCount > 0),
        };

        play.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, playing ? "stop" : "play"));
        menu.Items.Add(play);

        var previous = new MenuFlyoutItem
        {
            Text = Loc.Get("Bot.Previous"),
            IsEnabled = bot.Enabled && bot.TrackCount > 1,
        };

        previous.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, "prev"));
        menu.Items.Add(previous);

        var next = new MenuFlyoutItem
        {
            Text = Loc.Get("Bot.Next"),
            IsEnabled = bot.Enabled && bot.TrackCount > 1,
        };

        next.Click += (_, _) => Run(() => session.ControlBotAsync(bot.Id, "next"));
        menu.Items.Add(next);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Bringing it to your own room is the common case, so it is its own
        // entry rather than something to find inside a submenu of every room.
        if (!string.IsNullOrEmpty(session.MyRoomId) && session.MyRoomId != bot.RoomId)
        {
            var here = new MenuFlyoutItem { Text = Loc.Get("Bot.BringHere") };
            here.Click += (_, _) => Run(() => session.MoveBotAsync(bot.Id, session.MyRoomId));
            menu.Items.Add(here);
        }

        var move = new MenuFlyoutSubItem { Text = Loc.Get("Bot.MoveTo") };

        foreach (var room in session.Rooms)
        {
            var entry = new MenuFlyoutItem { Text = room.Name, IsEnabled = room.Id != bot.RoomId };
            entry.Click += (_, _) => Run(() => session.MoveBotAsync(bot.Id, room.Id));
            move.Items.Add(entry);
        }

        menu.Items.Add(move);

        var leave = new MenuFlyoutItem { Text = Loc.Get("Admin.BotLeaveRoom") };
        leave.Click += (_, _) => Run(() => session.MoveBotAsync(bot.Id, ""));
        menu.Items.Add(leave);

        return menu;
    }

    /// <summary>
    /// A refusal is a normal outcome — the server re-checks everything this menu
    /// offered — so it is swallowed here rather than crashing the room view. The
    /// bot's state simply does not change, which is what the user sees.
    /// </summary>
    private static void Run(Func<Task> action) => _ = Swallow(action);

    private static async Task Swallow(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
        }
    }
}
