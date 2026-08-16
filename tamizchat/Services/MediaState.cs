using TamizChat.Core.Protocol;

namespace TamizChat.Services;

/// <summary>
/// What to draw above somebody's name: microphone closed, speakers off.
/// </summary>
public static class MediaState
{
    /// <summary>
    /// Reads one person's switches.
    ///
    /// For everybody else this is what the server last heard from them. For
    /// **this** user it is their own live state instead, because the two must
    /// never disagree: the bottom bar reads the local switch directly, and a
    /// badge saying "muted" beside a bar saying "live" is the kind of
    /// contradiction that makes people stop trusting both.
    /// </summary>
    public static (bool MicOff, bool Deafened) Of(User member)
    {
        if (member.ClientUuid == ServerSession.Instance.MyUuid)
        {
            var voice = VoiceService.Instance;
            return (voice.IsConnected && voice.IsMuted, voice.IsDeafened);
        }

        return (!member.Media.Mic, member.Media.Deaf);
    }
}
