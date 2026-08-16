# TamizChat

The Windows client for [TamizChat](https://github.com/<you>/tamizchat-server) —
a self-hosted voice and text chat: rooms you can see into before you walk in,
voice and video, screen sharing, a shared paint board, files, and music bots.

Built with WinUI 3 on .NET 10. It ships as a plain folder with its own runtime
inside, so there is nothing to install first and no Store account involved.

*[راهنمای فارسی](README.fa.md)*

---

## Contents

- [Install](#install)
- [First run](#first-run)
- [What it does](#what-it-does)
- [Where your data lives](#where-your-data-lives)
- [Building from source](#building-from-source)
- [Building the installer](#building-the-installer)
- [Project layout](#project-layout)
- [The simulator](#the-simulator)
- [Troubleshooting](#troubleshooting)

---

## Install

Download `TamizChat-x.y.z-setup.exe` from the releases page and run it.

It installs for the current user by default, so Windows does not ask for
administrator rights. Choosing "install for all users" in the wizard is fine too.

**Requirements:** Windows 10 version 2004 (build 19041) or newer, 64-bit. Nothing
else — .NET and the Windows App SDK are inside the app.

Prefer no installer? Take the `TamizChat-win-x64` folder from the release, put it
anywhere, and run `TamizChat.exe`.

## First run

1. **Settings → your name.** This is what people see in a room.
2. **Servers → add a server.** Its address, e.g. `chat.example.com` or
   `192.168.1.50:8080`. If the server has a password, it is asked for here.
3. **Join**, then double-click a room to walk into it.

Your identity is a UUID the app generates once and keeps. It is the same person
on every server, and it survives a rename — which is why turning somebody's
volume down sticks even after they change their name.

## What it does

**Rooms you can see into.** The server page shows every room and who is in it, so
you can see where the conversation is before joining. Double-click to enter.

**Voice.** You join a room's voice automatically. The bottom bar owns the
microphone and speakers. Mute or deafen yourself and everyone sees a badge on
your avatar — and you see theirs.

**Video and screen sharing.** Camera or a chosen window/screen, in the same cell
the avatar was in.

**Voice changer and soundboard.** Deep, chipmunk, robot, radio — applied to your
own microphone in-process, so no virtual audio device is installed. Soundboard
clips are your own files and are heard by everyone.

**Paint board.** Shared, per room, streamed stroke by stroke as it is drawn.

**Chat and files.** Text, stickers and file/image sharing. Room content —
messages, files, drawings — is deliberately temporary and is erased when the last
person leaves.

**Overlays.** Members and messages float above full-screen games as click-through
windows.

**Admin panel.** For administrators: users and roles, bans, rooms, and bots —
with a visual designer for role tags. Running a server no longer means SSH for
everyday work.

**Music bots.** A bot joins a room like a real participant and plays a playlist.
Upload tracks from the admin panel; the app converts them to Ogg/Opus on the way
out, which is what lets the server publish them without touching the audio.

## Where your data lives

`%LOCALAPPDATA%\TamizChat`

| File | What |
|---|---|
| `settings.json` | Your name, identity, theme, devices, volumes, key bindings |
| `servers.json` | Saved servers |
| `sounds\` | Sound clips you added to the soundboard |

Uninstalling asks whether to remove that folder; it keeps it if you say no.

If the app ever closes unexpectedly, it leaves `crash.log` next to
`TamizChat.exe` — that file is the fastest way to get a bug fixed.

## Building from source

**Requirements:** .NET 10 SDK, Windows 10 SDK 10.0.26100, and Visual Studio 2022
or the command line.

```powershell
git clone https://github.com/<you>/tamizchat.git
cd tamizchat
dotnet build tamizchat.slnx -p:Platform=x64
```

Run it:

```powershell
.\tamizchat\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\TamizChat.exe
```

A release build, which is also the distributable folder:

```powershell
dotnet build tamizchat\tamizchat.csproj -c Release -p:Platform=x64
```

> **Use `dotnet build`, not `dotnet publish`.** For an unpackaged WinUI app,
> publish drops the compiled XAML (`.xbf`) and the app's `.pri`, and the result
> starts and dies with a `XamlParseException`. The Release build folder is
> already self-contained and complete.

## Building the installer

The installer script lives beside the client and server repositories, in
`installer/`. It needs [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
cd installer
.\build.ps1                  # builds the app and compiles the setup
.\build.ps1 -SkipApp         # only the setup, reusing dist\
.\build.ps1 -Version 1.1.0   # stamp a version
```

The setup lands in `dist\installer\`. Inno Setup does not ship a Persian
translation; dropping `Persian.isl` from its
[language pack](https://jrsoftware.org/files/istrans/) into Inno's `Languages`
folder makes the installer offer Persian as well.

## Project layout

| Project | What it is |
|---|---|
| `tamizchat` | The WinUI 3 app: pages, controls, overlays, services |
| `TamizChat.Core` | Protocol client and media session. No UI dependency |
| `TamizChat.Audio` | Capture, playback, voice effects, Opus conversion |
| `TamizChat.Simulator` | A console tool that pretends to be users |

`TamizChat.Core` is a plain library on purpose: it can be reused by a different
front end, and it keeps the protocol out of the interface code.

## The simulator

Testing a chat app alone is hard, so `tamizsim` plays the other people:

```powershell
tamizsim check                  # connect once, print what the server said
tamizsim run    Bob   Lobby     # sit in a room as somebody who is there
tamizsim talk   Alice Lobby     # publish a tone into the room's voice
tamizsim listen Carol Lobby     # play what arrives, to test your microphone
tamizsim video  Dave  Lobby     # publish a moving test pattern, no webcam needed
tamizsim paint  Erin  Lobby     # draw a streamed stroke
tamizsim bots   Admin           # walk the whole bot admin flow and check it
tamizsim convert song.mp3       # time the client's own Opus conversion
```

`TAMIZSIM_SERVER` overrides `localhost:8080`, and `TAMIZSIM_SECONDS` bounds how
long a mode runs. Each name maps to a stable identity, so reconnecting looks like
the same person returning.

## Troubleshooting

**The window opens and closes immediately.** Read `crash.log` next to the
executable — it names the exception.

**No sound from anybody.** Check the output device in Settings, and that you are
not deafened (the Speaker button in the bottom bar).

**Nobody hears me.** Check the input device and level meter in Settings. The
meter moves when the microphone is working, before anything is sent.

**Voice does not connect at all.** The server may not have LiveKit configured —
text chat works regardless. Ask the server's operator.

**Uploading a track takes a while.** It is converted to Opus first: about seven
seconds for a four-minute song. The progress bar shows both the conversion and
the upload.
