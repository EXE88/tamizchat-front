using Microsoft.UI.Xaml.Media.Imaging;
using TamizChat.Core.Protocol;

namespace TamizChat.Services;

/// <summary>
/// Everybody's profile picture, fetched once and kept.
///
/// A room of thirty people is redrawn constantly — every join, every mute, every
/// speaking ring — and each redraw asks for the same faces again. So the answer
/// is cached by the picture's tag rather than by the person: the same tag is the
/// same picture and never needs fetching twice, and a *different* tag is
/// somebody having changed theirs, which is exactly when the old one should be
/// thrown away.
///
/// Nothing here throws. A picture that will not load leaves the coloured circle
/// and its letter in place, which is a complete and perfectly usable avatar —
/// it is what every user had until now.
/// </summary>
public static class Avatars
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, Task<BitmapImage?>> Cache = [];
    private static readonly Lock Gate = new();

    /// <summary>Raised when a picture finishes loading, so anything already drawn can pick it up.</summary>
    public static event EventHandler<string>? Loaded;

    /// <summary>
    /// The picture for one user, or null when they have none.
    ///
    /// Returns immediately with what is already known; a fetch in flight is
    /// shared rather than started twice, because a room redraw asks for the same
    /// face from a dozen places at once.
    /// </summary>
    public static BitmapImage? Get(User user)
    {
        if (string.IsNullOrEmpty(user.Avatar) || string.IsNullOrEmpty(user.ClientUuid))
        {
            return null;
        }

        var key = Key(user);
        Task<BitmapImage?> pending;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var found))
            {
                return found.IsCompletedSuccessfully ? found.Result : null;
            }

            // Anything this user had before is a different picture now.
            foreach (var stale in Cache.Keys.Where(k => k.StartsWith(user.ClientUuid + "@", StringComparison.Ordinal))
                         .ToList())
            {
                Cache.Remove(stale);
            }

            pending = LoadAsync(user.ClientUuid);
            Cache[key] = pending;
        }

        _ = Announce(pending, user.ClientUuid);
        return null;
    }

    /// <summary>
    /// Whatever picture is already loaded for this person, without asking for
    /// one.
    ///
    /// For the moment a download finishes: the caller knows the person, not the
    /// tag it arrived under, and looking it up by user would need a User object
    /// the event does not carry.
    /// </summary>
    public static BitmapImage? TryGet(string clientUuid)
    {
        lock (Gate)
        {
            foreach (var (key, pending) in Cache)
            {
                if (key.StartsWith(clientUuid + "@", StringComparison.Ordinal)
                    && pending.IsCompletedSuccessfully)
                {
                    return pending.Result;
                }
            }
        }

        return null;
    }

    private static string Key(User user) => user.ClientUuid + "@" + user.Avatar;

    private static async Task Announce(Task<BitmapImage?> pending, string clientUuid)
    {
        var image = await pending.ConfigureAwait(true);
        if (image is not null)
        {
            Loaded?.Invoke(null, clientUuid);
        }
    }

    private static async Task<BitmapImage?> LoadAsync(string clientUuid)
    {
        var server = ServerSession.Instance.Server;
        if (server is null)
        {
            return null;
        }

        try
        {
            var url = $"{server.HttpUrl.TrimEnd('/')}/api/v1/avatar/{clientUuid}";
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(true);

            // Through a memory stream rather than handing the URL to
            // BitmapImage: the fetch then happens on our own HttpClient, with
            // our own timeout, and a server that is slow or gone cannot leave a
            // decoder waiting inside the layout pass.
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            // No picture, an unreachable server, or bytes that will not decode.
            // The letter is a perfectly good avatar.
            return null;
        }
    }

    /// <summary>
    /// Forgets everything, for leaving a server.
    ///
    /// Pictures are per server: the same person on two servers can have two, and
    /// keeping one across a disconnection would show the wrong face on the next
    /// one — the client id is the same everywhere.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }
}
