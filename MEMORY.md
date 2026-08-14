# MEMORY — TamizChat frontend

This file is the memory carried between chats for the **frontend**. Read it first
at the start of any new conversation, and update "Current status" at the end of
any piece of work.

The backend has its own memory at `../../backend/MEMORY.md`, and its wire
contract at `../../backend/docs/PROTOCOL.md` — that document is the spec this
client is built against.

Last updated: 2026-08-14 — phases F0, F1 and F2 done

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

Remaining phases are tracked as tasks: F3 navigation shell, F4 protocol client,
F5 user simulator, F6 server list and room grid, F7 chat/files/paint, F8
localization, F9 LiveKit media, F10 audio effects, F11 installer.

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
