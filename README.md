# FrostDock

FrostDock makes Windows look and feel a bit like macOS — without changing
Windows itself. It is a small, single-file Windows app written in C# that
adds three things on top of your normal desktop:

- **A dock** at the bottom centre of the screen. App icons magnify when you
  hover over them, a dot appears under running apps, and clicking an icon
  launches the app or jumps to its window.
- **A top bar** across the top of the screen, showing the active app's name,
  battery level and the clock.
- **quick search** — press Alt+Space (or Ctrl+Space), type an app name,
  press Enter to launch it.

Right-click the dock for more: show or hide the top bar, auto-hide the
Windows taskbar, and exit FrostDock. Nothing is installed — closing FrostDock
puts your desktop exactly back how it was.

## Run it

Download `FrostDock.exe` from the Releases page and double-click it. No
installer, no admin rights, nothing else to download — the .NET runtime is
bundled inside the file.

## Build it yourself

You need the .NET 8 SDK. From the `src` folder:

```sh
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

The output is a single `FrostDock.exe` for 64-bit Windows.

## A note on Smart App Control

Unsigned builds of FrostDock are blocked by Windows Smart App Control, because
Smart App Control only trusts apps signed with a valid certificate. Signed
releases are published here when available; until then, the source code in
this repo is fully open so anyone can read it and build it themselves.

## Project layout

```
src/
  Program.cs        startup, single-instance check, wires the three windows together
  DockForm.cs       the dock — custom-painted icons, magnification, running dots
  TopBarForm.cs     the top bar — active app name, battery, clock
  quick searchForm.cs  quick search over Start Menu apps
  DockApps.cs       finding pinned apps and their icons on the machine
  NativeMethods.cs  the few Windows API calls (hotkeys, taskbar auto-hide)
```

## License

MIT — see LICENSE.
