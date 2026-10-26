# FrostDock ◆

FrostDock is a Windows app that gives your desktop a macOS-style dock and top
bar, without changing Windows itself. It is a single file, written in C#, and
nothing is installed — quit FrostDock and your desktop goes exactly back to
how it was.

It is not affiliated with Apple.

## What you get

- **Dock** — a pill at the bottom centre of the screen. Icons magnify as you
  hover, dots mark running apps. Click an icon to launch an app, focus it, or
  minimize it back into the dock with a genie animation (click again to
  restore it). Drag apps out of Launchpad onto the dock to pin them;
  right-click a pinned icon to unpin it.
- **Top bar** — the active app's name, Wi-Fi and volume status, battery,
  clock, and a Control Center button.
- **Control Center** — volume and brightness sliders, Wi-Fi/Bluetooth status
  with links to Settings. Status only, never fake toggles.
- **Launchpad** — a full-screen grid of your apps (Start Menu, Desktop, and
  Store apps), with search. Open it from the dock icon or with F4.
- **Quick search** — press Alt+Space (or Ctrl+Space), type an app name,
  press Enter to launch it.
- **Now Playing** — media controls for whatever is playing, no login needed.
- **Trash** — the Recycle Bin, at the end of the dock.

Right-click the dock for more: show or hide the top bar, auto-hide the
Windows taskbar, and exit FrostDock.

## Run it

Download `FrostDock.exe` from the
[latest release](https://github.com/parthsingh03/FrostDock/releases/latest)
(or [direct download](https://github.com/parthsingh03/FrostDock/releases/latest/download/FrostDock.exe))
and double-click it. No installer, no admin rights, nothing else to
download — the .NET runtime is bundled inside the file. 64-bit Windows 10/11.

## A note on Smart App Control

These builds are unsigned, so Smart App Control may block a brand-new build
at first, saying it cannot verify the publisher. That block has cleared on
its own after a few hours in the past (Microsoft reviews the app), so if it
happens, wait and retry the same file before assuming it is broken. Never
turn Smart App Control off to get around it — it cannot be turned back on
without reinstalling Windows.

How signing works for this project is described in
[docs/CODE-SIGNING-POLICY.md](docs/CODE-SIGNING-POLICY.md).

## Build it yourself

You need the .NET 8 SDK. From the `src` folder:

```sh
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

The output is a single `FrostDock.exe` for 64-bit Windows.

## Project layout

```
src/
  Program.cs          startup, single-instance check, wires the windows together
  DockForm.cs         the dock — icons, magnification, running dots, genie toggle, pin drop target
  TopBarForm.cs       the top bar — active app, status icons, clock, Control Center button
  ControlCenterForm.cs  Control Center panel — volume/brightness sliders, Wi-Fi/Bluetooth status
  LaunchpadForm.cs    Launchpad grid, search, drag-to-pin source
  QuickSearchForm.cs  Alt+Space quick search
  DockApps.cs         finding apps (Start Menu, Desktop, Store) and their icons
  ShellIcons.cs       high-res icon extraction, classic-icon fallback
  SquircleTiles.cs    tile/icon drawing helpers
  AudioVolume.cs      system volume via Core Audio, legacy mixer fallback
  BrightnessControl.cs  screen brightness
  GenieEffect.cs      genie minimize/restore animation
  GlassStyle.cs       frosted-glass control painting
  WallpaperBlur.cs    blurred-wallpaper surface behind the dock and Launchpad
  NowPlaying.cs       media session info for Now Playing
  PinnedStore.cs      pinned apps saved between runs
  NativeMethods.cs    the Windows API calls (hotkeys, taskbar, layered windows)
  FrostDock.csproj    project file
```

## License

MIT — see LICENSE.
