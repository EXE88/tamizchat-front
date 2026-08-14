# MEMORY — TamizChat frontend

This file is the memory carried between chats for the **frontend**. Read it first
at the start of any new conversation, and update "Current status" at the end of
any piece of work.

The backend has its own memory at `../../backend/MEMORY.md`, and its wire
contract at `../../backend/docs/PROTOCOL.md` — that document is the spec this
client is built against.

Last updated: 2026-08-14 — phases F0 to F7 done

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

Remaining phases are tracked as tasks: F8 localization, F9 LiveKit media, F10
audio effects, F11 installer.

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
  `shot.ps1`.

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

## Known risks

- **LiveKit client for .NET is the biggest open risk.** There is no first-party
  .NET client SDK. The candidates are `Livekit.Rtc.Dotnet` (version 0.1.3, very
  young), calling the LiveKit Rust FFI directly the way the Unity and Flutter
  SDKs do, or a WebView2 bridge running the JS SDK. This is investigated at the
  start of phase F9 and not before; everything except voice/video runs over our
  own WebSocket and is unaffected.
- LiveKit needs host networking for its ICE candidates, which Docker Desktop on
  Windows does not provide properly, so LiveKit is deliberately not in the dev
  compose stack.

## Working rules

- Frontend work happens only inside `front/tamizchat/`.
- `../../backend/docs/PROTOCOL.md` is the spec. If the client needs something the
  protocol does not have, that is a backend change and a backend decision.
- Test unpackaged, never packaged — packaged needs signing and a Store identity.
- After each phase: update "Current status" here.
