# About — TamizChat

## For the repository's About box

*Short, for the sidebar description (under 350 characters):*

> Windows client for the self-hosted TamizChat server. WinUI 3 on .NET 10: rooms
> you can see into before joining, voice and video, screen sharing, a shared
> paint board, a voice changer and soundboard, overlays for full-screen games,
> and music bots.

*Suggested topics:*

`winui3` · `dotnet` · `csharp` · `windows-app-sdk` · `voice-chat` · `webrtc` ·
`livekit` · `self-hosted` · `opus` · `discord-alternative` ·
`teamspeak-alternative`

---

## The longer version

TamizChat is the Windows client for a self-hosted voice and text chat server. It
ships as an ordinary folder carrying its own .NET and Windows App SDK, so it
installs per-user without administrator rights, without MSIX, and without a Store
account.

**What it does**

- A room grid you can see into: who is where, before you join
- Voice, camera and screen sharing, with the sharer's video filling the cell
  their avatar was in
- Mic and speaker badges on every avatar, so it is obvious who cannot hear you
- A voice changer and a soundboard, applied in-process — no virtual audio driver
- A shared paint board, streamed as it is drawn
- Chat, stickers, files and images
- Click-through overlays for members and messages, over full-screen games
- An admin panel: users, roles with a visual tag designer, bans, rooms and bots
- Music bots, with playlists filled by uploading tracks from the app

**Decisions worth knowing before reading the code**

- **Unpackaged, on purpose.** No MSIX means no signing certificate and no Store
  identity, at the cost of doing the installer ourselves (Inno Setup).
- **`TamizChat.Core` has no UI dependency.** The protocol client and the media
  session are a plain library, reusable by another front end and testable without
  a window.
- **The simulator is part of the project.** `tamizsim` plays the other people —
  talking, listening, drawing, uploading — because a chat client cannot be tested
  alone, and "it looked right" is not a measurement.
- **Music is converted in the client.** Uploaded tracks become Ogg/Opus here,
  using the same encoder as the microphone, which is what lets the server publish
  them without decoding anything.
- **Per-listener volume is yours alone.** You can set anyone's volume for
  yourself; nobody, administrator included, can change how loud you are for other
  people.

**Requirements.** Windows 10 version 2004 (19041) or newer, 64-bit. A TamizChat
server to connect to — see the
[server repository](https://github.com/<you>/tamizchat-server).

Built with [Claude Code](https://claude.com/claude-code).
