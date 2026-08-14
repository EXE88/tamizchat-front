# MEMORY — TamizChat frontend

This file is the memory carried between chats for the **frontend**. Read it first
at the start of any new conversation, and update "Current status" at the end of
any piece of work.

The backend has its own memory at `../../backend/MEMORY.md`, and its wire
contract at `../../backend/docs/PROTOCOL.md` — that document is the spec this
client is built against.

Last updated: 2026-08-14 — F0 to F10 done

---

## What this is

The desktop client for TamizChat: a self-hosted Discord/Meet hybrid with
TeamSpeak's philosophy. The user types a server address and port and connects.

- Frontend: **C# / WinUI 3**, this repo (`front/tamizchat`)
- Backend: **Go**, complete, in `backend/`

**The whole project is written in English** — UI strings, code comments, docs.
The one exception is the Persian localization resource file, which exists because
the app itself is bilingual. Persian must not appear anywhere else; it renders
badly in terminals and breaks column alignment.

## Locked decisions

| Topic | Decision |
|-------|----------|
| Framework | WinUI 3, .NET 10 (`net10.0-windows10.0.26100.0`, min 10.0.19041.0) |
| Windows App SDK | 2.4.0, **self-contained** |
| Packaging | **Unpackaged** (`WindowsPackageType=None`). No MSIX, no certificate, no Store identity |
| Helper library | **DevWinUI** 10.3.0 — it is why the TFM must be net10; it ships no net8 target |
| MVVM | CommunityToolkit.Mvvm 8.4.2 |
| Assembly / namespace | `TamizChat` (the csproj file itself is still `tamizchat.csproj`) |
| Languages | English (default) and Persian, with RTL flow direction on Persian |
| Voice changer / soundboard | In-process DSP on our own capture path. **No virtual audio driver.** See below |
| LiveKit client | `Livekit.Rtc.Dotnet` 0.1.3 — the official Rust FFI, referenced from Core |
| Audio devices | `NAudio.Wasapi` 2.3.0, **not** the `NAudio` meta-package (it drags in WinForms) |

### Themes

Three themes, each with a light and a dark variant (six palettes total).

| Theme | Colors |
|-------|--------|
| Salt and Pepper | `#FFFFFF`, `#D4D4D4`, `#B3B3B3`, `#2B2B2B` |
| Violet and Lavender | Lavender `#D2C3F6`, Violet `#36205C` |
| Carbon and Lime | Carbon `#171717`, Lime `#C6FF34` |

In the dark variant the dark color is the background and the light one is the
text/accent; the light variant swaps them. The two-color themes have their
surface, border and hover colors derived from those two.

### Backdrops

Four, exposed to the user under friendlier names. **Acrylic ("Glass") is the
default.**

| User-facing name | WinUI backdrop |
|------------------|----------------|
| Matte | Mica |
| Matte High | Mica Alt |
| Glass *(default)* | Desktop Acrylic |
| Glass High | Desktop Acrylic, Thin |

The title bar uses `ExtendsContentIntoTitleBar`, so there is no visible seam
between the title bar and the page.

### Navigation and layout

- Three pre-server pages: **Home** (default), **Settings**, **Servers**. Moving
  between them uses slide left/right, chosen by the relative index of the target.
- Entering or leaving a server, and opening a feature page (paint board, chat),
  uses **DrillIn**. Feature pages get a back button at the top left.
- A **Floating Bottom Navigation Bar**: floating near the bottom rather than
  stuck to it, rounded corners, acrylic, icon above a text label. It has one item
  set before entering a server and a longer one after. When items would overflow,
  the important ones stay and the rest move behind a `...` button that opens a
  floating menu.
- **Room grid:** one room fills the view, two rooms take half each, four take a
  quarter each. Scrolling only starts at five or more. Each room is a card with
  its name and member count, and a showcase of member avatars with `+N` for the
  ones that do not fit.
- **Avatars** are a circle containing the first letter of the username. Users have
  no profile pictures.
- On joining, the user's own room goes fullscreen but can be collapsed to see the
  others. Double-clicking another room moves the user into it. Whoever is talking
  gets a halo.

## Current status

**Phase F0 (foundation) — done.** The project was converted from the stock VS
template (packaged, net8, Windows App SDK 2.3.1) to unpackaged net10 with SDK
2.4.0 and DevWinUI. Verified by running the built exe:

```
packaged   = False
OK    ExtendsContentIntoTitleBar
OK    Mica / Mica Alt / Acrylic / Acrylic Thin
OK    AppWindow.TitleBar tall
OK    DevWinUI loaded
```

Setting `TAMIZCHAT_SELFTEST=1` makes the app run those checks, write
`selftest.txt` next to the exe and exit — that is how it gets verified without a
human looking at the window.

**Phase F1 (dev backend) — done.** See "Running the dev backend" below.

**Phase F2 (theme, backdrops, title bar) — done.** All six palettes and all four
backdrops apply and persist; verified both by the self-test sweep and by
screenshots of the running window.

**Phase F3 (navigation shell and floating bar) — done.** Home/Servers/Settings
slide between each other, joining a server and opening a feature page drill, the
bar swaps from three items to eight (four plus More), and leaving unwinds back to
the server list.

**Phase F4 (protocol client) — done.** `TamizChat.Core` is a plain net10.0
library with no UI dependency. Verified against the running backend: HTTP probe,
WebSocket connect, hello handshake, all five rooms in the welcome, a `room.join`
request matched to its reply by id, `chat.send`, and the resulting
`chat.message` event coming back.

**Phase F5 (simulator) — done.** `TamizChat.Simulator` builds on Core.

**Phase F6 (server list, join flow, room grid) — done.** Verified against the
real backend with nine simulated users spread across three rooms: the grid showed
Lobby 6/25 with six coloured avatars, Gaming 2/25, the rest empty, and joining a
room switched to the focused view with a "Show all rooms" way back.

**Phase F7 (chat, files, paint board) — done.** All three verified against the
real backend with simulated users.

**Phase F9 (voice, camera, screen) — done and verified end to end.** Measured
against the real backend and a real LiveKit:

| Path | Evidence |
|------|----------|
| Receive audio | Halo on the speaker's avatar; 1399 frames in 14 s at `tamizsim listen` |
| **Send audio (microphone)** | 1999 frames in 20 s from the app — exactly 100/s |
| Receive video | Test pattern rendered in the sender's cell, aspect preserved |
| **Send video (screen)** | `1280x720 Screen` arriving from the app at a subscriber |

The microphone is no longer the untested half: the app captures from a real
headset and publishes at exactly the expected rate.

**Camera is now verified against real hardware** (an HP HD Camera): 241 frames in
15 s at 1280x720 reaching a subscriber, plus a local preview. Finding it took two
real bugs, both of which had the same shape — the camera worked locally and
published nothing:

- **A track published at 0x0 kills the process.** `CameraCapture` only learned its
  size from the first frame to arrive, but the caller publishes as soon as
  `StartAsync` returns, so the size was still zero. libwebrtc does not throw for
  that — it fails an assert and aborts, with no managed stack to catch. The size
  now comes from the chosen *format*, and `MediaSession.StartVideoAsync` rejects
  a non-positive size in managed code before it can reach native again.
- **Never drop a frame for not matching the published size.** That guard silently
  published nothing at all while the local preview looked perfect, which is a
  horrible way to fail. The published size is only an initial hint, libwebrtc
  handles a source changing size, and a driver delivering something other than
  the format it was asked for is normal. What is checked now is the buffer
  against its own claimed size, since a short buffer is an out-of-bounds read.

**Phase F10 (audio effects) — done.** A voice changer and a soundboard, both
in-process on our own capture path, exactly as the no-virtual-driver decision
requires. Verified with `tamizsim fx` (offline measurement) and end to end: the
app publishing with the robot voice and an airhorn mixed in delivered 1199 frames
in 12 s to a listener — a clean 100/s, no drops.

| Effect | Input | Measured |
|--------|-------|----------|
| None | 200 Hz | 198 Hz |
| Deep | 200 Hz | 157 Hz |
| Chipmunk | 200 Hz | 310 Hz |
| Robot | 200 Hz | 250 Hz (ring-mod sideband) |
| Radio | 200 Hz | 200 Hz, louder — the clipping |

Remaining phase: F11 installer.

**Phase F8 (localization) — done.** Every user-visible string in the app goes
through the resource layer; 113 keys, English and Persian. Verified by running
the app in Persian: Home, Settings and Chat all read Persian, the whole shell
lays out right to left, and switching language takes effect without a restart.


### How the effects work

- **The voice changer runs before the soundboard is mixed in.** The other order
  would put the airhorn through the pitch shifter too, so choosing Chipmunk would
  change what the clips sound like — and the clips are meant to be fixed,
  recognisable sounds.
- Pitch shifting is a **granular shifter**: the signal is written to a ring at the
  normal rate and read at a different one, with two read heads half a window apart
  crossfaded so the wrap is never heard. Cheap enough for every 10 ms frame with
  no fourier transform.
- Robot is **ring modulation**, not a vocoder. Two lines instead of a filter bank,
  and it lands in the same place for a soundboard-grade effect.
- Radio is a one-pole band-pass plus `tanh` soft clipping. Losing the bass is what
  makes it read as "through a speaker"; `tanh` saturates smoothly, which is the
  difference between overdriven and broken.
- **The clips are synthesised, not shipped.** No licence, no asset folder, no
  installer step. Applause is shaped noise, which is very nearly the real
  mechanism — a crowd *is* hundreds of noise bursts.
- **The user hears their own soundboard but never their own voice.** A voice
  monitor is a loop at the round trip's delay and is disorienting to talk over; a
  clip is something you triggered and expect to hear.
- Pressing a soundboard button **unmutes first**. It is a deliberate act, and
  doing nothing because the microphone happened to be off reads as a broken
  button.
- Menu choices are matched **by index, not by label** — labels are translated, so
  matching on them would break the moment the language changes.
- `tamizsim fx` runs both offline and prints dominant frequency and peak level.
  That is how "does the DSP work" gets a number instead of an opinion; it caught
  applause clipping at full scale.
- `TAMIZCHAT_AUTOVOICE` and `TAMIZCHAT_AUTOEFFECT` trigger the two menus at
  startup, because **a flyout cannot be driven from a script** — taking a
  screenshot steals focus and dismisses it.

### How localization works

- **Plain .NET `ResourceManager` over embedded .resx, not `x:Uid` and .resw.**
  Most of this UI is built in C#, so `x:Uid` would only reach a minority of the
  strings; and .resw resolves through PRI, the part of the resource stack least
  happy in an unpackaged app. A ResourceManager behaves the same either way.
- **`Loc.Get` never throws.** A missing translation falls back to English and a
  missing key returns the key, so a gap looks wrong in the UI instead of crashing
  the page.
- **Nav items hold `LabelKey`, not `Label`.** `ShellItems` is static data built
  once, so storing translated text would freeze it into whichever language loaded
  first. The label is resolved at display time.
- **The bar needs `Retranslate()` after a language change.** `SetItems`
  deliberately short-circuits when the item set is the same object, which it is,
  so it would keep the old labels. Toggle state survives the rebuild because it
  lives in `_toggles`, not on the buttons.
- **FlowDirection is set on the root content**, so it inherits to the bar, every
  page and every dialog at once. Setting it per page leaves the bar facing the
  wrong way.
- **The whole title row is kept left-to-right**, via `TitleRow`, not just
  `AppTitleBar`. Windows keeps the minimise/maximise/close buttons on the right
  whatever the app's flow direction is, so a mirrored strip puts our content
  underneath them. This was got wrong once by setting it on `AppTitleBar` alone:
  the back button sits *outside* that element on purpose (anything inside the
  drag region never receives clicks), so it stayed mirrored and landed on top of
  the close button. Set it on the parent both of them inherit from.
- **XAML keeps its English literals as design-time defaults**, and each page's
  `Translate()` overwrites them at runtime. That way the designer still shows
  something readable and there is exactly one source of truth at run time.
- `Translate()` is called from the page constructor only. Language can be changed
  on the Settings page, which retranslates itself in place, and the frame builds
  a fresh instance of every other page on navigation.
- `CurrentUICulture` is switched but **not `CurrentCulture`** — Persian digits
  next to a latin server address read worse than plain ones, and this app is full
  of such mixtures.

### Chat

- Messages live only in the room's memory on the server, so there is nothing to
  cache locally. The page reads one page of history on open and follows the live
  stream from there.
- The sender receives their own message **twice** — once as the reply to
  `chat.send` and once as the broadcast. Rows are de-duplicated by message id.
- The view only auto-scrolls when the user was already at the bottom; scrolling up
  to read is not interrupted by new arrivals.
- Paging back preserves the reading position by measuring `ExtentHeight` before
  and after the insert, rather than jumping to the new top.
- Typing indicators are throttled to one every 3 seconds on send, and aged out
  after 5 seconds on receive — the protocol has no guaranteed "stopped" message.

### Files

Permission travels over the socket, bytes over HTTP. `file.upload_request` returns
a single-use ticket; the raw bytes are POSTed to the ticket's URL with the token
as a bearer header. **Nothing is posted afterwards** — the server puts the file
into the room itself, so it arrives as an ordinary `chat.message` whose
`attachment` is set.

Download links are fetched per view: they are short-lived and only valid for the
room the user is in right now, so they cannot be cached or handed on.

The file picker needs `InitializeWithWindow` — an unpackaged app has no implicit
window for it to sit on and it throws without one.

### Which paint messages actually reply

This cost a hang before it was found, and the protocol document does not spell it
out — the backend handlers do:

| Message | Reply on success? |
|---------|-------------------|
| `paint.begin` | **yes**, the Stroke with its new id |
| `paint.append` | no, ever |
| `paint.end` | **no** — only on failure |
| `paint.undo` / `paint.clear` / `paint.state` | yes |

So `paint.end` must be *sent*, not *requested*. Awaiting a reply that only comes
on failure waits forever.

Other things the board depends on:

- Coordinates on the wire are normalized 0..1, never pixels, so a drawing lands in
  the same place on any window size. Resizing rebuilds the pixel copies from the
  board rather than stretching them.
- Points are drawn locally the instant the pointer moves and sent in 60 ms
  batches. Waiting for the round trip is what makes drawing feel sluggish.
- Points closer than 0.004 apart are dropped — a fast scribble would otherwise be
  hundreds of frames a second.
- The eraser paints the board's own colour rather than removing points, which
  keeps every stroke a simple append-only line. That colour must be **opaque** —
  `TcBoardBrush`, never `TcSurfaceBrush`, which is translucent and only tints
  what is under it instead of covering it. The eraser is also 2.5× the pen width,
  or it never feels like it is erasing. Each client resolves the colour from its
  own theme, so an eraser stroke drawn under one theme still erases under another.
- **Undo and clear reply to the caller instead of broadcasting to them.** The
  server excludes whoever asked from the broadcast and answers with an
  id-correlated reply — the project's standard pattern. `UndoStrokeAsync` and
  `ClearBoardAsync` therefore deserialize that reply and raise the local event
  themselves, or the caller's own board never changes until the page is left and
  re-entered.
- **A resize must not re-fetch the board.** `_strokes` holds the normalized copy
  and a resize re-projects from it. Re-fetching raced with itself: several size
  changes fire during one page load, and an older reply landing last would clear
  the canvas and leave a stale — sometimes empty — board.

### Room content is temporary, and that is not a bug

Measured against the dev server, with `rooms.purge_grace_sec = 30`:

| Step | Strokes |
|------|---------|
| Draw, then leave the room empty | 1 |
| Return immediately | 1 |
| Return 20s later (inside the grace) | 1 |
| Return after the grace | 0 |

So a drawing survives a quick reconnect and is gone after a slow one. Reconnecting
by hand — open the app, Join, pick the room, open Paint — easily takes longer than
30 seconds, which is why the same test can look inconsistent. To keep a board or a
chat alive while inspecting it, leave a `tamizsim run` simulator in the room.

### The room grid

- `Services/ServerSession` owns the one live connection and the room tree. Pages
  read from it and listen to `Changed`; nothing else touches `TamizChatClient`.
- Membership events do **not** patch the tree. Any of them queues a debounced
  `room.list`, which returns the whole tree with members in one frame and is
  always self-consistent. Twenty people joining costs one round trip, not twenty.
- The server, not our own copy, decides which room we are in: `MyRoomId` is
  recomputed from the refreshed tree, or it would drift after a moderator move.
- `ServerPage.Relayout` is arithmetic, not a panel: one room fills the view, two
  take half each, four take quarters, and from five on the tiles stay quarters and
  the grid scrolls. It re-runs on every size change.
- `RoomTile` rebuilds its avatar showcase on size change, fitting as many as the
  width allows and collapsing the rest into "+N".
- `AvatarView` derives its circle colour from the username hash, so the same
  person is the same colour on every client and every run.
- **`Border` is sealed in WinUI 3.** Custom controls that want border, corner and
  padding properties derive from `Grid`, which has all of them.

### Dev environment: turn off the per-IP connection cap

The backend's `network.max_conns_per_ip` defaults to 8, and on a dev machine the
app and every simulator all come from `127.0.0.1`. The ninth connection gets an
HTTP **429** and the WebSocket upgrade fails. It is already set to 0 (unlimited)
on the dev backend; if the volume is ever recreated, set it again:

```bash
docker compose -f deploy/docker-compose.dev.yml exec backend tamizchat
```

Then Server settings → Network → Max connections per IP → 0.

### Dev-only entry points on MainWindow

`TAMIZCHAT_START_PAGE` (`server` / `servers` / `settings`) and `TAMIZCHAT_AUTOJOIN`
(a room name, or `none` to connect without joining) open the app straight into a
state, which is how the grid gets screenshotted without driving the UI. Note that
a PowerShell argument value must not be `-`; it gets parsed as a parameter name.

## Solution layout

| Project | What it is |
|---------|------------|
| `tamizchat/` | The WinUI 3 app. Unpackaged, net10, x64 |
| `TamizChat.Core/` | Protocol client and server probe. **No Windows dependency**, so the simulator can use it |
| `TamizChat.Audio/` | WASAPI capture and playback. Shared by the app and the simulator |
| `TamizChat.Simulator/` | Console fake user (`tamizsim`) |

### The protocol client

- `TamizChatClient` owns one WebSocket. Requests carry a generated id and are
  matched back through a `TaskCompletionSource` keyed by it; anything without a
  matching id is raised as `ServerEvent`. An `error` frame carrying a pending id
  fails that request rather than surfacing as an event.
- Sends are serialised behind a semaphore — a WebSocket has a single writer.
- `ServerProbe` reads `/api/v1/server-info` over plain HTTP. It needs no session,
  which is what makes the Home page able to show online state for a whole list of
  servers cheaply.

### Running the simulator

From `TamizChat.Simulator/bin/Debug/net10.0` (or `dotnet run --project`):

```bash
tamizsim check
```

```bash
tamizsim run Bob Lobby
```

```bash
tamizsim paint Painter Lobby
```

```bash
tamizsim file Uploader Lobby
```

```bash
tamizsim talk Talker Lobby
```

```bash
tamizsim listen Listener Lobby
```

```bash
tamizsim video Talker Lobby
```

`talk` joins the room's voice and publishes a 440 Hz tone. `listen` **plays what
arrives out of the speakers**, which is how a real microphone gets tested — talk
in the app and hear yourself come back round the trip; use a headset, or the two
ends will howl at each other. `video` publishes a moving colour-bar pattern, so
the whole video path can be tested on a machine with no webcam — and because it
moves, a frozen feed is obvious at a glance. All take `TAMIZSIM_SECONDS` and need
the dev LiveKit running.

`check` connects once, prints what the server said and exits — the quickest way
to tell whether the backend and the client still agree. `run` stays connected as
a fake user in a room and talks occasionally, so the client can be tested with
somebody else present. `paint` draws one streamed stroke and `file` uploads a
small file, both into the named room. `TAMIZSIM_SERVER` overrides
`localhost:8080`. Each name maps to a stable client UUID, so reconnecting looks
like the same person.

A room's board and chat are **erased when the last person leaves**, so keep a
`run` simulator in the room while inspecting either, or there will be nothing to
see by the time the app connects.

### How the shell is wired

- `MainWindow` is the shell: title bar, `ContentFrame`, and the `FloatingNavBar`
  overlaid at the bottom. It decides the transition for every move.
- `Navigation/ShellItems` holds the two item sets. **Their order is also the
  left-to-right screen order**, which is what the slide direction is computed
  from — reordering that list changes which side pages arrive from.
- `Navigation/NavigationService` maps `NavTransition` onto WinUI's
  `SlideNavigationTransitionInfo` / `DrillInNavigationTransitionInfo`.
- The bar keeps `MaxPrimaryItems` (5) buttons; beyond that the last slot becomes
  a "More" flyout. Eight in-server items therefore render as four plus More.
- `FeaturePage` is a shared placeholder for Chat/Files/Members/Bots. Because
  several items point at the same page type, item identity is the `Key`, not the
  page type — F7 replaces these with real pages.
- The back button must sit **outside** the element passed to `SetTitleBar`, or it
  never receives clicks: everything inside the drag region is inert.
- `TAMIZCHAT_START_PAGE=server` opens the app directly on the in-server shell,
  which is how that state gets screenshotted without driving the UI.
- `TAMIZCHAT_TREEDUMP=1` writes the laid-out visual tree of the Home page to
  `treedump.txt`. Worth reaching for before believing a screenshot: it is what
  proved the "missing" buttons were actually present and correctly sized.

### Two bugs already fixed here, worth not reintroducing

- **Identity is the item `Key`, never the page type.** Several bar entries used
  to share one placeholder page, so selection always stuck to whichever came
  first in the list and the others were dead. If two entries ever share a page
  again, only the key keeps them apart.
- **The bar rebuilds its buttons only when the item set changes.** Rebuilding on
  every selection change destroyed the button being pressed, so fast clicking
  landed on whatever replaced it. Selection is a restyle, not a rebuild.
- Icon glyphs live in Unicode's private use area and get silently stripped when
  pasted through tooling, leaving every icon blank. They are written as escapes
  in `ShellItems` for that reason.

### Layout rule: never combine MaxWidth with HorizontalAlignment="Stretch"

Inside a ScrollViewer that pairing centres the block, and the centring came out
wrong: the content was pushed sideways until its right edge — where the action
buttons live — fell outside the window. Home and Servers both lost their Join,
Refresh, Add, Edit and Remove buttons to it, while Settings was fine because it
uses `HorizontalAlignment="Left"`.

Pages either fill the available width (no MaxWidth) or align Left with a
MaxWidth. Not both.

**Diagnosing this needs positions, not sizes.** The first tree dump only printed
sizes, every element looked correct, and that led to the wrong conclusion that
the app was fine and the screenshot tool was lying. `TAMIZCHAT_TREEDUMP=1` now
prints `x` for every element, which is what actually showed the offset.

### Accent buttons

Use `TcAccentButtonStyle`, never WinUI's `AccentButtonStyle` — the latter reads
the Windows system accent (blue) and ignores the chosen theme entirely.

### How the theme system works

- `Themes/Palette.xaml` declares every brush the app draws with, as shared
  instances. `Theming/ThemeManager` rewrites their `Color` when the theme
  changes. Swapping merged ResourceDictionaries at runtime does **not** reliably
  re-evaluate `StaticResource` references in WinUI; mutating a brush that
  everything already points at always does.
- `ThemeManager` also overrides `AccentFillColorDefaultBrush` and friends, so
  stock WinUI controls pick up the family's accent.
- DevWinUI's `ThemeService` applies the backdrop (`BackdropType.Mica`/`MicaAlt`/
  `Acrylic`/`AcrylicThin`). It is configured with `ConfigureAutoSave(false)`
  because our own `settings.json` is the source of truth. There is a fallback
  path that sets `Window.SystemBackdrop` directly if DevWinUI ever throws.
- New UI must use the `Tc*` brushes, never hard-coded colours, or it will not
  follow the theme.

### Two DPI traps that already cost time

- `AppWindow.Resize` takes **physical** pixels. On this machine (250% scaling) a
  "1100x760" window only gets 440x304 of layout space and the page is clipped.
  `MainWindow.SizeAndCentre` multiplies by `GetDpiForWindow / 96` to fix this.
- The screenshot script must call `SetProcessDPIAware()` and force the window
  topmost with `SetWindowPos`, or it captures the wrong screen region and
  whatever window happens to be in front. The script lives in the scratchpad as
  `shot.ps1`. It must match the **process name** (`TamizChat`), not the window
  title: Visual Studio's title contains the solution name and wins a title
  search, which produces a very convincing screenshot of the wrong application.
  Give the window a moment to settle after raising it, or the capture catches it
  mid-restore and saves a sliver.

## Running the dev backend

The backend runs in Docker and keeps its data in the named volume
`tamizchat-dev-data`, so rooms and settings survive restarts. **Do not delete
that volume** — it holds the seeded dev state.

From `backend/`:

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
```

```bash
docker compose -f deploy/docker-compose.dev.yml down
```

```bash
docker compose -f deploy/docker-compose.dev.yml logs -f
```

Open the admin panel against the same database:

```bash
docker compose -f deploy/docker-compose.dev.yml exec backend tamizchat
```

The server listens on `http://localhost:8080`; the client connects to
`ws://localhost:8080/ws`.

### Running the dev LiveKit (media phases only)

LiveKit used to be absent from the dev stack because the production compose runs
it with host networking, which Docker Desktop on Windows does not have. It now
has its own loopback-only file, separate from the backend's because it is only
needed for the media phases:

```bash
docker compose -f deploy/docker-compose.livekit.dev.yml up -d
```

Three things make it work on Windows, and all three are load-bearing:

- **`use_external_ip: false` with `node_ip: 127.0.0.1`.** Left at `true`, LiveKit
  discovers the container's `172.x` bridge address and advertises it in ICE.
  Signalling then succeeds and no audio ever arrives, which looks like a client
  bug and is not one.
- **One UDP port (`udp_port: 7882`)** instead of a range, because Docker has to
  publish each one.
- **`livekit.url` must be `ws://host.docker.internal:7880`, not `127.0.0.1`.**
  TamizChat has a single URL setting and derives the LiveKit *server API* address
  from it, so one name has to work from two places: the client on the host, and
  the backend inside its container, where `127.0.0.1` is the backend itself. The
  ports are therefore published on all interfaces rather than pinned to loopback.
  ICE still runs over loopback, so media never leaves the machine.

**Proxy trap.** This machine has an HTTP proxy whose no-proxy list covers
`127.0.0.1` but not `host.docker.internal` (a `10.x` address), so `curl` returns
a bare `503` with `Proxy-Connection: close` and it reads exactly like LiveKit
being down. `curl --noproxy '*'` is the check. Windows' own `ProxyOverride`
includes `10.*`, so the app is unaffected — but a shell with `HTTP_PROXY` set
will break the spike, which is why it is run with `NO_PROXY` extended.

**Credentials** are in `deploy/livekit.dev.yaml` and already entered in the dev
backend's panel (`livekit.url` / `api_key` / `api_secret` / `enabled`). They are
dev-only and must never reach a real server.

**Seeded state:** `network.public_host = http://localhost:8080`, and five rooms
(Lobby, Gaming, Music, Study, AFK) — five on purpose, so the room grid's
scrolling case (more than four) is testable.

### Why the image does not compile the Go code

`go mod download` inside the container gets **403 Forbidden** from
`proxy.golang.org`, because Docker containers do not inherit the Windows host's
proxy settings. So the binary is cross-compiled on the host instead — the project
is CGo-free, so `GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build` produces a
static binary — and the Dockerfile only copies it in. The image build then needs
no network at all.

Rebuild after a backend change (from `backend/`):

```bash
make docker
```

## Voice changer and soundboard: no virtual audio driver

VB-CABLE is **not** needed and will not be bundled.

A virtual audio cable exists to route processed audio into a *different*
application. TamizChat is the application doing the capture and the publishing,
so the effects can be applied in our own pipeline — capture the mic, run the DSP,
mix in soundboard clips, hand the result to the media publisher. Nothing has to
leave the process.

This also avoids three real problems: VB-CABLE requires a redistribution
agreement with VB-Audio for bundling, installing a driver needs administrator
rights and would break the point of shipping unpackaged, and a system-wide
virtual device would affect the user's other applications.

The one thing this does not give us is using the voice changer *in other apps*
(Discord, games). If that is ever wanted, it needs a virtual device and a
licensing conversation with VB-Audio — a separate decision.

## Media: the F9 decision (settled 2026-08-14)

**`Livekit.Rtc.Dotnet` 0.1.3 is the client.** Measured, not guessed — the spike
connected two participants to the dev LiveKit through tokens issued by our own
backend, published a tone from one and received **1215 audio frames /
1,166,400 bytes of PCM** on the other in 12 seconds. Connect took 260–470 ms.

The three candidates turned out to be two, not three:

- The package **is** the LiveKit Rust FFI. It ships the official
  `livekit_ffi` binary from `livekit/rust-sdks` for win-x64, win-arm64, linux
  and macOS, and drives it over the same generated protobuf protocol the Unity
  and Flutter SDKs use. "Use the package" and "call the Rust FFI ourselves" are
  therefore the same decision, minus the work.
- Its author also maintains `Livekit.Server.Sdk.Dotnet`, the .NET server SDK
  LiveKit's own documentation points at. The 0.1.x version number reflects the
  package's age, not a hobby project.
- The **WebView2 bridge is not needed** and should not be revisited: it would put
  a browser between our capture path and the network, which is exactly what the
  no-virtual-audio-driver decision was meant to avoid.

### Why it fits the voice changer decision so well

The SDK does **no device I/O at all**. Publishing is
`AudioSource.CaptureFrameAsync(new AudioFrame(short[], 48000, 1, 480))`, and
receiving is an `AudioStream` of the same frames. That is precisely the seam the
in-process DSP needs:

```
WASAPI capture -> voice changer + soundboard mix -> AudioSource -> LiveKit
LiveKit -> AudioStream -> our mixer -> WASAPI render
```

So capture and playback are **ours to write** (F10 territory): the SDK will not
open a microphone for us. 10 ms of 48 kHz mono is 480 samples, which is the
frame size to build the whole pipeline around.

### How the voice path is put together

| Piece | Where | What it owns |
|-------|-------|--------------|
| `MediaSession` | `TamizChat.Core/Media` | The LiveKit room. **No Windows dependency**, so the simulator can talk |
| `MicrophoneCapture` | `TamizChat.Audio` | WASAPI in, converted to 48 kHz mono 10 ms frames |
| `SpeakerPlayback` | `TamizChat.Audio` | Per-speaker jitter buffers, summed, converted to the device's format |
| `AudioFormat` | `TamizChat.Audio` | Downmix, linear resample, float↔PCM16 |
| `CameraCapture` | `tamizchat/Video` | WinRT MediaCapture, BGRA frames |
| `ScreenCapture` | `tamizchat/Video` | GDI BitBlt of the primary monitor into a DIB section |
| `VoiceService` | `tamizchat/Services` | Ties them together; mirrors `ServerSession`'s shape |

`TamizChat.Audio` is a **fourth project**, split out of the app so the simulator
can play sound too. It is plain `net10.0` rather than Windows-targeted because
NAudio ships a netstandard2.0 asset that both the app and the console tool can
resolve. Video capture stayed in the app: it needs WinRT, which needs the
Windows TFM.

### Controls live on the bottom bar, nowhere else

Mic, Speaker, Camera and Screen are toggles in `ShellItems.InServer`, wired in
`MainWindow.OnNavStateChanged`. A duplicate Mute button on the room page was
built and then removed — **do not reintroduce a second control surface for
these.** The bar is the one place that owns them.

- **Mic is live on joining**, because the bar declares it on by default and the
  two must not disagree; an icon claiming you are live while you are not is
  worse than either state.
- **Speaker is deafen, not unsubscribe.** It stops playback and leaves the
  LiveKit subscription alone, so undeafening is instant instead of a
  renegotiation.
- Camera and screen are off by default and gated on `can_publish_video` /
  `can_share_screen` from the token.
- `TAMIZCHAT_AUTOSHARE=screen|camera` turns one on at startup, which is how the
  publishing side is tested without clicking.

Decisions worth keeping:

- **The DSP seam is `MicrophoneCapture.Process`**, a `Func<float[], float[]>` on
  mono 48 kHz floats. F10's voice changer and soundboard go there, and nothing in
  the pipeline has to move to accommodate them.
- Conversions are written by hand rather than delegated to a resampler object,
  for the same reason: a conversion buried inside somebody else's stream is not
  somewhere effects can be inserted.
- **The resampler's fractional position must persist between buffers.** Resetting
  it each callback puts a discontinuity at every buffer boundary, which is a
  steady buzz at the buffer rate, not something anyone would recognise as a
  resampling bug.
- **`Read` on the playback provider always returns a full buffer.** Returning
  less tells NAudio the stream ended and playback stops permanently; silence is
  the right output when nobody is talking.
- Per-speaker buffers are capped at half a second and **drop the oldest** rather
  than growing. A buffer that only grows converts one hiccup into permanent lag.
- The mic track is published on first unmute and then kept; muting stops sending
  frames. Publishing is a round trip, and doing it per toggle clips the first
  word.
- Voice joins the room automatically but **starts muted with the microphone
  closed**. Entering a room must never begin broadcasting it.
- `tamizsim talk` and `tamizsim listen` are the test tools: `talk` is a fake
  speaker to develop the client against, `listen` returns non-zero if no audio
  arrived. Measured 1399 frames in 14 s, which is exactly the expected 100/s.

### Things the spike established that are easy to get wrong

- `LiveKit.Rtc.Room` and our `TamizChat.Core.Protocol.Room` collide. Alias one at
  every use site; they mean genuinely different things (a media session versus a
  room definition).
- `ParticipantConnected`/`Disconnected` hand over the `Participant` itself, while
  `TrackSubscribed` hands over an event-args object with `.Participant`. Not
  consistent, and the compiler is the only thing that tells you.
- `paint.end`-style asymmetry has an equivalent here: **`AudioStream` must be
  drained** (`await foreach`) or nothing arrives. Subscribing is not receiving.
- The LiveKit room name really is the TamizChat `room_id`, confirmed on the wire.
- Active speaker events arrive from LiveKit as promised, so the room grid's
  "who is talking" halo needs nothing from our own server.

### Screen sharing asks first, and shows you what you are sharing

Two things were missing when the toggle first went in, and together they made it
look completely broken: it published the primary monitor with no dialog, and the
user saw nothing happen.

- **`SharePicker` lists monitors and windows** — our own dialog, not the system
  `GraphicsCapturePicker`, which hands back a Direct3D surface and so belongs to
  a different capture stack from the GDI path used here. TamizChat's own windows
  are filtered out, along with cloaked and tool windows.
- **Cancelling puts the toggle back.** A bar showing screen sharing as on while
  nothing is being sent is the worst of the three possible states.
- **The publisher sees their own share.** LiveKit does not loop a published track
  back to whoever published it, so without a local preview the one person who
  cannot see the share is the person sharing — which reads as a dead button.
  `VoiceService` raises its own frames through the same event as remote ones.
- A window is captured with `PrintWindow` and `PW_RENDERFULLCONTENT`, so it works
  when the window is behind another; without that flag modern apps come back
  blank. A monitor is `BitBlt` from the desktop DC **at that monitor's offset**,
  or the second screen captures as a copy of the first.

### Video, and the one number that is not good enough

- Frames cross as **BGRA** and LiveKit converts. Both Windows capture APIs
  produce BGRA naturally, so nothing hand-unpacks NV12 or MJPEG.
- **A video track must be drained like an audio one.** `VideoStream` behaves
  exactly like `AudioStream`: subscribing is not receiving.
- Camera and screen are told apart by the **publication's source**, not by the
  track, and screen wins over camera in a cell — someone sharing a screen is
  showing it for a reason.
- Screen tracks publish with **simulcast off**: LiveKit dropping resolution on a
  camera is graceful, on a spreadsheet it is unreadable.
- Frames are **dropped while the previous one is still being handed to XAML**.
  Queueing builds a backlog the moment the UI thread is busy, and stale video is
  worse than fewer frames.
- Odd dimensions are cropped away before publishing; chroma subsampling needs
  even ones.

**Screen share runs at about 5 frames a second, and it is the GDI capture.** The
open question is now answered: the camera reaches the *same console subscriber*
at ~16 fps (241 frames in 15 s), so `RoomOptions.AdaptiveStream` was not the
limit and the suspicion was wrong. The cost is in `ScreenCapture` — a full-screen
BitBlt plus a 3.7 MB `Marshal.Copy` every frame. Removing `CAPTUREBLT` only moved
it from 4.3 to 5.2, so the flag was not the main cost either. Moving to
`Windows.Graphics.Capture` is the real fix; it is contained to that one class.

`ScreenCapture` is GDI on purpose: `Windows.Graphics.Capture` is the better API
(hardware accelerated, single-window) but returns Direct3D surfaces, so it needs
a D3D11 device and a staging copy before LiveKit sees a byte. The output shape is
identical either way, so swapping it is contained to that one class.

## Known risks

- **Camera capture has never met a camera.** Everything downstream of it is
  proven, but `CameraCapture` itself needs a real webcam to verify.
- Screen share is about 5 frames a second at a console subscriber; see the open
  question above before treating that as the real figure.
- `Livekit.Rtc.Dotnet` is young and has one maintainer. The mitigation is that it
  is a thin binding over LiveKit's own FFI, so a stall there is recoverable by
  building the same binding ourselves against a newer `livekit-ffi` release.
- The native binary is ~24 MB per architecture, which the F11 installer has to
  account for.

## Working rules

- Frontend work happens only inside `front/tamizchat/`.
- `../../backend/docs/PROTOCOL.md` is the spec. If the client needs something the
  protocol does not have, that is a backend change and a backend decision.
- Test unpackaged, never packaged — packaged needs signing and a Store identity.
- After each phase: update "Current status" here.
