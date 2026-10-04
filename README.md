# PlayMe

A compact, always-on-top media widget for Windows: transport controls for
whatever is playing, a browser panel that opens sites itself, and a volume
mixer for every app on the machine.

All three windows follow you across **every virtual desktop**, stay on top,
and never steal keyboard focus.

## Download

Grab the latest build from the
**[releases page](https://github.com/C-Squared-Solutions-LLC/PlayMe/releases/latest)**:

| | |
|---|---|
| `PlayMe-v*-win-x64.exe` | the widget - download and run it, nothing to install |
| `PlayMe.Mcp-v*-win-x64.exe` | optional, lets Claude control your media |

Both are self-contained, so there is no .NET runtime to install. 64-bit
Windows 10 2004+ or Windows 11; the player panel needs the WebView2 runtime,
which ships with Windows 11. The builds are unsigned, so SmartScreen will ask
the first time - *More info* then *Run anyway*.

Settings live in `%AppData%\PlayMe\settings.json` and the player keeps its
own browser profile in `%AppData%\PlayMe\browser`, so signing into a site in
the panel doesn't touch your real browser.

## The widget

A small card showing album art, title, artist, prev / play-pause / next, and a
click-to-seek progress bar, plus a volume row for the playing app.

It drives the Windows **system media session** (SMTC —
`GlobalSystemMediaTransportControlsSessionManager`), the same channel the
hardware media keys use, so it controls any app that publishes there: the
player panel, a browser, Spotify, the Amazon Music app.

- **Chevron (top-left)** — every stream Windows reports, each with its site's
  logo. Click one to control it; your pick sticks until that stream ends.
- **Camcorder** — the player panel.
- **Sliders** — the mixer.
- **Drag** the card to move it; position is remembered.
- **Right-click** for always-on-top, the panels, and Exit.
- Always-on-top is reasserted every 2 s, since fullscreen apps knock windows
  out of the topmost band.

There is no source allow-list any more. It existed because PlayMe had no say
in what a browser was playing and had to guess which tab was music; now that
it opens sites itself, every stream is simply listed and you pick one.

## Player

The **camcorder button** opens PlayMe's own browser panel. It is not a mirror
of someone else's window — PlayMe loads the site itself, which is the whole
point: nothing can cloak it, and it follows you across desktops like the rest
of the widget.

- **Favourites strip** — one button per site, showing that site's favicon.
  Click to switch to it, or to open it if it isn't open yet. Right-click a
  button to add/remove it from favourites, mute it, or close it.
- **Several sites at once.** Each stays loaded and keeps playing; only the one
  you picked is on screen. Music in one, a stream in another, switch with a
  click. The active one is underlined in accent; anything else open is
  underlined grey.
- **Globe button** — open anything: a URL, a bare domain (`music.amazon.com`),
  or just a Twitch channel name.
- **Options** — add to favourites, reload, open in your real browser, per-site
  volume and mute, close a site, and "move with the widget".
- **Volume** — a slider and mute button in the bar, applied to the site you
  are looking at. The player also appears in the mixer as **PlayMe (player)**
  for its overall level: its sound is actually rendered by WebView2 child
  processes, so without grouping those it would hide inside a generic
  "Microsoft Edge WebView2" row shared with every other app that embeds one.
- **Video only** (the crop button, next to the keyboard one) — shows just the
  picture, without the chat, menus or anything else. Remembered per site.
- **The panel takes the shape of what is playing.** A site's `<video>` box is
  not the picture: Twitch's player is 16:9, so a 4:3 or ultrawide stream sits
  letterboxed *inside* it. PlayMe crops to the picture itself and holds the
  window to the stream's own ratio while you resize, so there are no bars on
  either axis. **Fill the panel** (options) crops the edges to fill whatever
  shape the panel already is instead.
- **Drag the top bar** to move the panel; resize from the corner grip or any
  window edge. Ctrl+wheel over the bar nudges the size.

### Signing in

The panel deliberately never takes keyboard focus, which is what keeps it out
of your way — but that also means you cannot type into it. The **keyboard
button** lends it focus for as long as you need; click it again to go back to
staying out of the way. Sign-ins persist in PlayMe's own browser profile at
`%AppData%\PlayMe\browser`.

Sites are started with autoplay permitted, so a stream begins on its own
rather than waiting for a click it will never get.

> Video-only does not measure the page and zoom it. That approach broke
> whenever a site swapped its player - a Twitch ad left the picture at the
> wrong scale beside a black gap and a stray scrollbar - because the geometry
> it had measured no longer described what was drawn. Instead the `<video>`
> itself is lifted to cover the window with `object-fit`, so the browser does
> the fitting and nothing has to be measured. If a transformed ancestor traps
> the fixed positioning, the element is moved to the top of the document; the
> original styles and position are put back when you turn it off.

## Mixer (every app, up to 300%)

The **sliders button** opens a volume mixer for everything making sound —
games, calls, browsers — not just what PlayMe controls. It parks above the
widget and shows the output device, then one row per app, sorted by name so
rows never move under the pointer: each app's icon, its real name, a live peak
meter, and a mute button.

### Past 100%

Windows caps a mixer channel at 100%, so going higher means taking the app's
audio and playing it again, louder. Past the 100% mark PlayMe:

1. captures that app alone with the Windows process-loopback tap (what Game
   Bar uses to record a single app),
2. parks the app's own mixer level at 2%, dropping its direct output to about
   -34 dB, inaudible,
3. re-renders the captured audio with the gain you asked for, scaled back up
   by exactly the factor the leak cost it, through a soft limiter.

What you hear is the amplified copy; the residual direct path is ~37 dB below
it, far too quiet to comb-filter, so there is no echo. Measured against a
reference tone: 150% → 1.49x, 200% → 1.98x, 300% → 2.99x, and releasing a
boost puts the app back exactly where it was.

Two things worth knowing: boost adds about **40 ms of audio delay**, so leave
it off when lip sync matters, and while an app is boosted its own Windows
slider no longer changes what you hear, because PlayMe is holding it at the
leak level on purpose. If PlayMe ever dies mid-boost, the next start puts that
app's level back.

Boost needs Windows 11 (or Windows 10 build 20348+). Without it the sliders
stop at 100%, and if a particular app refuses to be captured its row snaps
back to 100% and says why.

> The audio interfaces behind this can only be activated from an MTA thread.
> WPF's UI thread is STA, so every call into them is marshalled off it — doing
> it directly fails with `E_NOINTERFACE` and looks exactly like a slider that
> won't go past 100%.

## Album art fallback

Some players (the Amazon Music desktop app) publish no artwork to Windows.
When the active track has no thumbnail, PlayMe looks it up by artist + title
on the keyless iTunes Search API and shows that cover instead (cached per
track; only artist/title leave the machine).

## MCP server (control from Claude)

`PlayMe.Mcp.exe` is a stdio MCP server exposing the same controls. It talks to
Windows directly, so it works whether or not the widget is running.

Tools: `now_playing`, `play_pause`, `next_track`, `previous_track`, `seek`,
`set_volume`, `mute`, `duck` (most take an optional `source` to target a
specific stream).

```
claude mcp add --scope user playme -- "C:\mPrograms\PlayMe\PlayMe.Mcp.exe"
```

For the Claude desktop app, in `%AppData%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "playme": { "command": "C:\\mPrograms\\PlayMe\\PlayMe.Mcp.exe" } } }
```

## Memory

The widget is meant to sit at a few tens of MB forever, so the two things that
could grow without bound are held down deliberately:

- **Mixer sessions.** Reading an app's volume enumerates the Windows mixer,
  which hands back a COM wrapper per audio session holding state in the audio
  service until released. Polling that twice a second and dropping the
  wrappers grew memory under *Windows Audio* without limit — measured at
  +5.1 MB per 600 polls. Every wrapper that isn't kept is now disposed at
  once, matching ones are cached and re-enumerated only every few seconds, and
  the poll runs once a second: +28 KB over the same 600 polls.
- **Album art.** Players fire metadata events in bursts and each rebuild
  re-read the artwork — hundreds of KB onto the large object heap. Rebuilds
  are coalesced, they only wake the UI when something visible changed, and
  artwork is read once per track.

Workstation non-concurrent GC with `System.GC.ConserveMemory=5` keeps the
large object heap compacted. Idle, with no panels open, it sits at ~135 MB
working set / ~71 MB private and stays flat. The player panel is a full
browser engine, so expect it to cost what a browser tab costs while open.

## Why the panel is a browser and not a window mirror

PlayMe used to mirror another window with the Windows Graphics Capture API.
Capture is not the problem — a window on another virtual desktop keeps
delivering frames (26 per 2.5 s cloaked vs 28 uncloaked). **Chromium is**: it
stops drawing a window it believes nobody can see — cloaked, minimized, or
fully covered — so the picture froze on the last frame whenever you switched
desktops (73 frames per 3 s visible, 6 hidden). The only cure was launching
the browser with `--disable-features=CalculateNativeWinOcclusion`, which is
not something a widget should ask of anyone. Opening the site itself sidesteps
all of it.

## Usage notes

- The panels are tool windows: no taskbar entry, no Alt-Tab, which is also
  what makes them show on every virtual desktop.
- Clicks never steal focus (`WS_EX_NOACTIVATE`), except while the player's
  keyboard button is on.
- Settings live in `%AppData%\PlayMe\settings.json`. If it can't be parsed it
  is kept as `settings.json.bad` rather than silently overwritten.

## Build

Requires the .NET 8 SDK. Targets the 22621 Windows SDK projection but runs on
Windows 10 2004+ (build 19041). The player needs the WebView2 runtime, which
ships with Windows 11.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\gen-icon.ps1
dotnet run
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The exe lands in `bin\Release\net8.0-windows10.0.22621.0\win-x64\publish\`.

## Ideas / not yet

- Start with Windows (registry Run key) toggle
