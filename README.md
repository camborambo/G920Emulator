# G920 Emulator

Windows input bridge that maps **any DirectInput wheel, pedals, H-pattern shifter, or controller** onto a virtual **Logitech G920** (with Driving Force Shifter gears and force feedback forwarding).

Works as a system HID device for any PC game that accepts a G920 / DirectInput wheel.

## Features

- Detects connected DirectInput game controllers / wheelbases / pedals / shifters
- Per-control binding UI (listen for axis/button)
- Virtual G920 identity: `VID 046D` / `PID C262`
- H-pattern shifter targets: **R, 1–6** (exclusive or passthrough)
- Force feedback: virtual PID/output → physical DirectInput FFB wheel
- JSON mapping profiles

## Requirements

- Windows 10/11
- .NET 8 runtime (SDK to build)
- [WinUHid](https://github.com/cgutman/WinUHid) driver + `WinUHid.dll` for real virtual device exposure ([install guide](docs/driver-install.md))
- [HidHide](https://github.com/nefarius/HidHide) (**required**) to hide physical devices from games

## Run as EXE (recommended)

Double-click:

```
Launch G920 Emulator.bat
```

That builds a self-contained Release app on first run (needs the .NET 8 SDK once), then starts:

```
dist\G920Emulator\G920Emulator.exe
```

Or publish manually:

```powershell
.\publish.ps1 -OpenFolder
```

Then run `dist\G920Emulator\G920Emulator.exe` anytime. The publish is **self-contained** (Windows x64), so other PCs don’t need the .NET runtime installed—just copy the whole `dist\G920Emulator\` folder.

WinUHid is bundled under `dist\G920Emulator\winuhid\`. Use **Dependencies → Install WinUHid** (one button, no download).

## Dev run

```powershell
dotnet run --project src/G920Emulator.App
```

## Quick start

1. Double-click **Launch G920 Emulator.bat** (or run the published EXE)
2. Open **Dependencies…**, install **WinUHid** and **HidHide** (both required), then click **Configure HidHide**
3. Name your profile and click **Save** (stored under `%AppData%\G920Emulator\profiles`)
4. **Refresh devices**, then **Listen / bind** for steering, pedals, buttons, and shifter gears R/1–6
5. Pick an FFB output device (your wheelbase)
6. Hide your physical pad in HidHide, **Start bridge**, launch your game

Profiles support **Save**, **Save As**, **Delete**, **Export**, and **Import**, plus quick switching from the Saved dropdown.

## Solution layout

```
src/G920Emulator.App          WPF UI
src/G920Emulator.Core         Input hub, mapper, FFB, bridge loop
src/G920Emulator.VirtualHid   WinUHid P/Invoke + G920 descriptor
profiles/                   JSON mapping profiles
docs/                       Driver install & validation
```

## License

Project code is yours to use in this repository. WinUHid remains under its upstream license.
