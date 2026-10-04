# G920 Emulator

**Use any wheel, pedals, H-shifter, or controller as a Logitech G920** — including games that only offer a G920 / Logitech wheel profile.

G920 Emulator is a Windows app that creates a **virtual Logitech G920** (`VID 046D` / `PID C262`) on your PC. You bind your physical DirectInput devices to that virtual wheel, hide the real hardware from games with HidHide, and optionally forward **force feedback** to your Fanatec, Simucube, Simagic, Moza, or other DirectInput FFB base.

```
Physical devices  →  G920 Emulator  →  Virtual G920  →  Game
Game FFB effects  →  g920ffb.dll    →  Physical base
```

## Guides

| Guide | Start here if you want to… |
|-------|----------------------------|
| **[User guide](docs/user-guide.md)** | Install dependencies, bind controls, save profiles, Start bridge |
| **[Driver install](docs/driver-install.md)** | Set up WinUHid and HidHide (required for games) |
| **[Compatibility](docs/compatibility.md)** | Devices and games tested so far |
| **[Force feedback](docs/force-feedback.md)** | Understand OEM FFB, FFB debug, and effect probing |
| **[Architecture](docs/architecture.md)** | See how input and FFB flow through the stack |
| **[Building](docs/building.md)** | Publish the EXE or build `g920ffb.dll` from source |
| **[Research notes](docs/research-logitech-g920.md)** | Read G HUB / HID++ findings and the NFS Unbound probe |

## Tested so far

| Category | Tested |
|----------|--------|
| Input | DualSense |
| FFB base | Fanatec Clubsport DD2 |
| Games | **NFS Heat**, **NFS Unbound** (tested / compatible) |

**Gear R:** Heat uses default button **19**. Unbound needs Bind Gear R → **12**. Full matrix: [compatibility](docs/compatibility.md).

## Quick start (download a release)

No Visual Studio or .NET SDK required.

1. Download **`G920Emulator-win-x64.zip`** from the latest [**Releases**](https://github.com/camborambo/G920Emulator/releases) page.
2. Extract the zip — you get a **`G920Emulator`** folder.
3. Run **`G920Emulator.exe`** inside that folder.
4. Open **Dependencies** → **Install WinUHid** (bundled; approve UAC; reboot if asked for test signing) → install **HidHide** from its download link → **Configure HidHide**.  
   Step-by-step: [driver install](docs/driver-install.md).
5. **Refresh** devices, bind steering / pedals / buttons / gears **R–6**, pick an **FFB output device**.
6. **Start bridge**, then launch your game and select the G920.

Binding changes apply while the bridge is running — no need to Stop/Start after rebinding. Full walkthrough: [user guide](docs/user-guide.md).

## What you get

- Virtual G920 that games see as a normal system HID / DirectInput wheel
- Binding UI for axes, buttons, hats, and axis→button mappings
- H-pattern gears **R, 1–6** (R defaults to button **19** / LGS; pick **12** in Bind Gear R for Unbound)
- Force feedback via our DirectInput OEM driver (`g920ffb.dll`) — not Logitech HID++
- JSON profiles in `profiles\` next to the exe (Save / Export / Import; AppData fallback if that folder is not writable)

## Requirements

- Windows 10/11 (x64)
- [WinUHid](https://github.com/cgutman/WinUHid) — **bundled** in the release; install from the app ([guide](docs/driver-install.md))
- [HidHide](https://github.com/nefarius/HidHide) — **required** (separate download) so games don’t see both your real pad and the virtual G920

## Build from source

For contributors (not needed to play):

```powershell
.\publish.ps1 -OpenFolder
```

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Output: `dist\G920Emulator\` and `dist\G920Emulator-win-x64.zip`.

Day-to-day development:

```powershell
dotnet run --project src/G920Emulator.App
```

Details: [building](docs/building.md).

## Repository layout

```
src/G920Emulator.App          WPF UI
src/G920Emulator.Core         Input hub, mapper, bridge, FFB apply
src/G920Emulator.VirtualHid   WinUHid + G920 identity + OEM registration
native/g920ffb                DirectInput OEM EffectDriver (g920ffb.dll)
docs/                         User and technical guides
profiles/                     Profile JSON (default.json starter + your saves)
```

## License

Project code in this repository is available for use with the project. WinUHid remains under its upstream license.
