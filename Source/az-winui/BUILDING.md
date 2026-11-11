# Building Accessibility Zoom (WinUI 3) on Windows

The WinUI 3 XAML compiler is Windows-only, so the app must be built on a
Windows 10/11 PC. One command does it.

## Prerequisites

- Windows 10 version 1809+ / Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.400+)

No Visual Studio required. No Windows App SDK runtime install required —
the publish is self-contained.

## Build

From a terminal in this folder:

```powershell
dotnet publish AccessibilityZoom.WinUI.csproj -c Release
```

That produces a self-contained `win-x64` build in
`bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.
Run `AccessibilityZoom.exe` from that folder (the exe needs the DLLs next
to it — copy the whole `publish` folder if you move it).

> Single-file publishing is intentionally off: the Windows App SDK targets
> reject `PublishSingleFile` for unpackaged apps (they demand MSIX tooling).
> The build is still fully self-contained — no .NET install needed on the
> target machine.

## What you get

- `AccessibilityZoom.exe` — control panel (WinUI 3, Mica backdrop), tray icon,
  single-instance app. Closing the window hides to the tray; Exit quits.
- The zoom engine is the proven C# engine (Magnification API / direct
  compositor / GPU overlay / AMD ADLX), untouched except for removing the
  WinForms dependency.
- CLI flags: `--ui-preview`, `--self-test`, `--crosshair-self-test`, `--gpu-self-test`,
  `--direct-self-test`, `--amd-self-test`, `--amd-roundtrip-self-test`.

## Verify on first run

1. Launch the exe — the panel opens with a Mica backdrop, zoom readout,
   and color-profile controls.
2. Hold Mouse 4 — zoom engages until released; Task Manager
   should show the GPU engine active, not the panel.
3. Right-click the tray icon — menu with Open / Display color /
   Cycle profiles / Crosshair / Exit. Each item shows its current shortcut.
4. Close the window with X — it hides to the tray instead of exiting.
5. Compare Task Manager GPU/CPU/RAM against the old 4.0 build while zoom
   is actively running.

## Controls (4.3.8 defaults)

| Binding | Action |
| --- | --- |
| Hold Mouse 4 (Back) | Zoom until released; change the Zoom shortcut to Toggle to latch it with a press |
| F7 | Show/hide the centered crosshair |
| F9 | Toggle display color |
| Tap F10 | Mute/unmute the focused application's audio |
| Hold F10 for at least 0.5 seconds, then release | Reduce the focused app to the **Quiet volume** percentage of its current level (30% by default). Repeat to restore its previous level. |
| F11 | Cycle saved monitor/color presets, with the preset name shown at the top right for 1.8 seconds |
| Ctrl + Alt + + / = | Increase zoom |
| Ctrl + Alt + - | Decrease zoom |
| Ctrl + Alt + 0 | Reset zoom and display color |
| Ctrl + Alt + Q | Reset and exit |

Open **Settings > Hotkeys & bindings** to edit every action. Choose a key or
mouse button and optional Ctrl/Alt/Shift/Win modifiers, or choose Unassigned.
The **Zoom** row also lets you choose **Hold** or **Toggle**. If an older
installation uses both shortcuts, both stay active until you edit this row.
Assigning an existing shortcut swaps the two actions. Changes apply and save
immediately; Restore default bindings restores the table above. Left/right mouse
bindings apply outside the app's control panel so settings remain clickable.
The volume binding always carries both tap and hold actions together. Set the
remaining percentage from 0 to 100 under **Settings > Quiet volume**.

Settings and color profiles are stored under `%LOCALAPPDATA%\AccessibilityZoom`
and retain compatibility with earlier zoom/color settings. Existing installations
without explicit hotkey settings start with Mouse 4, even if the old build
automatically selected Mouse 5.

Create color presets with **New profile**, which captures the current color
settings. Every saved profile can be renamed or deleted. An empty list stays
empty across restarts.

## Crosshairs

The Crosshair card includes a design selector, preview, on/off switch, monitor,
size (25–400%), opacity (5–100%), and three import options:

- **Load image:** PNG/JPEG/BMP/GIF. PNG alpha is preserved; GIF uses its static
  first frame. Images are copied into the saved design and remain available if
  the source file is moved. Large imported images fit inside 256 px initially.
- **Paste code / link:** a Crosshair X short code or crosshairx.gg/s/ share URL.
  The design and any HTTPS image layers download once and are stored locally.
- **Import from Crosshair X:** select one or several designs from the installed
  app's saved library, including its currently selected crosshair. A preview is
  provided and Select all imports the whole list. Crosshair X's global size
  setting is retained as the imported design's native scale. Crosshair X files
  stay intact.

Imports use the static base design. Firing animations are ignored. Crosshairs
use a small, click-through, non-activating DirectComposition/D3D11 alpha window.
There is no desktop capture or animated render loop. The original crosshair
pixels are uploaded only when design/size/opacity change. Zoom changes reassert
topmost placement without resampling the image. A one-second timer handles
monitor/topmost maintenance. The selected monitor falls back to the primary
display if disconnected. The user confirmed this path preserves the visible
crosshair shape. It enlarges with Windows fullscreen zoom; that behavior is
accepted to preserve shape and minimize CPU/GPU overhead. No zoom-time pixel
resampling is performed.

Crosshair settings and imported designs live in
`%LOCALAPPDATA%\AccessibilityZoom\Crosshairs`. F7 is remappable in the same
settings section as every other binding. The tray menu can also toggle the
crosshair. Hide Crosshair X's own overlay when testing to avoid duplicate reticles.
Use borderless/windowed mode when a game hides desktop overlays in exclusive
fullscreen.

Static Crosshair X geometry rendering uses MIT-licensed components from Reticly,
pinned to commit `ea9364b5ac090106db02cfdf6e5be288c31e1ae3`. Attribution and license
are included at `Assets\ThirdParty\Reticly-LICENSE.txt`. Image decoding is bounded,
uses only embedded copies, and does not execute file or network references while
rendering. Share service availability is independent of the offline library.

## Automated checks (no visible overlay or audio changes)

```powershell
dotnet run --project Tests/AccessibilityZoom.Tests.csproj -c Release
```

Version 4.3.7 keeps the established eager graphics initialization from 4.3.4.
The experimental deferred fallback in 4.3.5/4.3.6 was reverted: the measured
memory saving did not justify the additional complexity for this user.

Monitor presets default to ON whenever the app opens. Settings includes an
independent startup toggle. The last color target is saved by Windows display
name; legacy settings choose the primary display. Startup waits for two matching
monitor layouts 750 ms apart (normally about 1.5 seconds), after the controls
have loaded. The bounded async startup sequence replaces the earlier XAML timer. If the saved display is
missing, startup leaves color off instead of writing to another monitor.
AMD recovery locks and native target validation remain active. Exit saves
preferences before temporarily restoring normal display color.

F10 quiet mode searches child processes (including Signal audio helpers) and
commits restore state only after a successful audio change. Tap mute preserves
the session level and sets/reads the Windows session mute flag. All sessions in
the focused process tree receive the same mute state, including inactive ones.
This is the application's mixer mute state; system master volume is unaffected.

These check shortcut routing, repeat suppression, tap/hold timing, modifier changes,
swaps, keyboard/mouse assignments, restart persistence, malformed settings, and
the existing zoom math and color profile self-tests. Crosshair checks cover image
alpha, static geometry, imported image independence, code parsing, malformed data,
library/preferences persistence, monitor/zoom placement, and hidden native alpha
surfaces with repeated GDI resource replacement. Transform-state checks exercise
cross-thread delivery, atomic scale/offset snapshots, unchanged-state suppression,
counter-scaling to retain display size and restoration after zoom release.

To additionally download a public example share code and render local Crosshair X
designs (read-only):

```powershell
dotnet run --project Tests/AccessibilityZoom.Tests.csproj -c Release -- --live-crosshair-tests
```

The published app accepts `--self-test` for the existing headless math/profile
checks and `--crosshair-self-test` for drawing/alpha-surface dependency checks
with the native window kept hidden. Visible window behavior and picker/dialog
interaction need a manual check.

To verify real Windows session mute/restore against a silent child-process stream:

```powershell
dotnet run --project Tests/AccessibilityZoom.Tests.csproj -c Release -- --audio-self-test
```

The audio check controls only its own child process, with zero-valued PCM data.
It exercises the Signal-style parent-without-audio case, reduce/restore, Windows
mute readback with the level preserved, and hold while muted. It never changes
the system master volume or another application's audio.

## Managed installation and updates (4.3.20)

Use `Build-Release.ps1 -RepositoryUrl https://github.com/OWNER/REPOSITORY` to
produce the installer and GitHub update assets. See [UPDATES.md](docs/UPDATES.md)
for release publishing, first installation, retained settings and legacy cleanup.
Installed builds use the permanent launcher for Start with Windows. Update
checks run in the background; download and restart require user confirmation.

## Windows sign-in startup (4.3.8–4.3.19, historical)

Settings > Start with Windows is opt-in. It writes this app's current executable
path, quoted, with `--start-minimized` to the current user's Run key. Turning the
switch off removes only the AccessibilityZoom entry. Existing opt-ins follow the
version the user now opens. No administrator rights or background service is
needed. Sign-in launches into the tray; Show controls opens the window.
The monitor-preset-on-open preference is separate and still applies in tray mode.

Startup tests use an isolated temporary HKCU key, never the actual Run key. They
cover quoted paths, enable/read/disable, moving builds, idempotent removal and
preserving other values. Sequence tests cover stable/arriving/missing monitors,
failed applies, disabled/already-on states, and cancellation during exit.
Preset startup logs a few diagnostic lines to `%TEMP%\az-startup-color.txt`,
then stops. Runtime UI strings use Unicode escapes for the zoom/separator symbols;
the packaged assembly is checked for correct text and absence of garbled forms.

## Crosshair input fix (4.3.9)

The system cursor is hidden while the crosshair surface is shown and restored
when disabled, on overlay failure, and during normal exit. Cursor suppression
owns its Magnification API initialization separately from the zoom backend.
The GPU crosshair window now combines WS_EX_LAYERED with WS_EX_TRANSPARENT
and full window alpha, while the existing DirectComposition surface retains
per-pixel alpha. This allows mouse buttons to pass through to game processes;
HTTRANSPARENT alone only forwards hit tests within the same thread.

Automated checks exercise cursor hide/restore/re-enable, repeated refresh and
shutdown, initialization/suppression failures, and native overlay styles without
showing windows or changing the real cursor. In-game right-click and appearance
require manual verification.

Windows API references:
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowsystemcursor
- https://learn.microsoft.com/en-us/windows/win32/dwm/bestpractices-ovw
- https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts
## Contextual cursor correction (4.3.10)

Supersedes 4.3.9's unconditional cursor hiding. Crosshair visibility alone no
longer hides the mouse. A 50 ms input-state timer checks a conservative gameplay
heuristic: another focused window covering the selected display, cursor bounds
contained in that display, and the pointer within two pixels of the crosshair
center for 350 ms. Movement away from center, focus changes, desktop/own windows,
or failed native queries restore the pointer on the next timer tick. This does
not identify game processes; fullscreen non-game windows can also meet the
heuristic, and games without a centered pointer will retain their cursor.
The 50 ms timer does not upload or render crosshair content. Monitor maintenance
continues at one second. Cross-process click-through from 4.3.9 is retained.

908 automated checks passed, including focus changes, menu movement, desktop
windows, unconfined multi-monitor movement, and negative display coordinates.
Real game behavior requires manual verification.

## Lightweight zoom restoration (4.3.11)

Restores DirectFullscreenZoom.cs from the pre-crosshair source backup, verified
against the shipped 4.2 assembly. This keeps the original event-driven Windows
fullscreen transform worker, 1.0001x startup warmup, transform verification,
retry/reset handling, and existing GPU compatibility fallback. The current
hotkeys, monitor/color settings, and 4.3.10 contextual cursor policy are retained.

The zoom worker no longer publishes DesktopTransformState changes, and the
crosshair no longer subscribes/queues UI work on zoom transitions. Its existing
one-second maintenance handles placement/topmost. Crosshair pixels still upload
only when settings/design change. The brief preset popup reads the native
transform on its existing timer while visible, independently of the zoom worker.
No desktop capture or frame renderer was added to the direct zoom path.

908 automated checks passed. Packaged math and hidden crosshair checks are run
for the release. Game responsiveness still requires manual comparison with 4.2;
this change does not claim an FPS or latency improvement from those checks.
