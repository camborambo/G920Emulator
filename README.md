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
| **[Force feedback](docs/force-feedback.md)** | Understand OEM FFB, Debug logging, and effect probing |
| **[Changelog](CHANGELOG.md)** | See what’s new in each release (version notes) |
| **[Architecture](docs/architecture.md)** | See how input and FFB flow through the stack |
| **[Building](docs/building.md)** | Publish the EXE or build `g920ffb.dll` from source |
| **[Research notes](docs/research-logitech-g920.md)** | Read G HUB / HID++ findings and the NFS Unbound probe |

## Tested so far

| Category | Tested |
|----------|--------|
| Input | DualSense |
| FFB bases | **Fanatec Podium Wheel Base DD2** (Heat / Unbound), **Simucube** (Unbound) |
| Games | **NFS Heat**, **NFS Unbound**, **Forza Horizon 6** (FFB work in progress) |

**Gear R:** Heat uses default button **19**. Unbound needs Bind Gear R → **12**. Full matrix: [compatibility](docs/compatibility.md).

**Unbound tip:** Accessibility → Controls → **Controller Vibration** must be **On**, or the game streams spring-only (walls/rumble stay at magnitude 0).

### After WinUHid is installed (important)

WinUHid is test-signed, so install needs **Windows test signing on** and usually **Secure Boot off** in UEFI. **Once WinUHid is installed, you do not leave the PC in that state:**

1. **Turn test signing off** — **Install WinUHid** already does this at the end. If Dependencies still shows test signing on: **Disable test signing** → reboot. (Forza Horizon exits at splash with code **100** while test signing is on.)
2. **Turn Secure Boot back on** in UEFI/BIOS if you disabled it for the install. WinUHid (UMDF) usually keeps working with Secure Boot on and test signing off.

Emulator and Forza can coexist on the same boot after that. Details: [driver-install](docs/driver-install.md#forza-horizon-6-and-test-signing).

## Quick start (download a release)

No Visual Studio or .NET SDK required.

1. Download **`G920Emulator-win-x64.zip`** from the latest [**Releases**](https://github.com/camborambo/G920Emulator/releases) page.
2. Extract the zip — you get a **`G920Emulator`** folder.
3. Run **`G920Emulator.exe`** inside that folder.
4. Open **Dependencies** → **Install WinUHid** (bundled; approve UAC; may reboot once or twice). When install finishes: confirm **test signing is off**, then **re-enable Secure Boot** in UEFI if you turned it off for install (see [above](#after-winuhid-is-installed-important)).
5. Install **HidHide** from its download link → **Configure HidHide**.  
   Step-by-step: [driver install](docs/driver-install.md).
6. **Refresh** devices, bind steering / pedals / buttons / gears **R–6** (and D-pad via hat or **D-pad Up/Down/Left/Right**), pick an **FFB output device** + **FFB profile**, then **Save** your input profile.
7. **Start bridge**, then launch your game and select the G920.

Binding changes apply while the bridge is running — no need to Stop/Start after rebinding. Full walkthrough: [user guide](docs/user-guide.md).

## Profiles (where your binds / FFB live)

Profiles are **not** inside the install / zip folder. On first run the app creates:

```
%AppData%\G920Emulator\profiles\       ← input bindings (Default.json + your Save / Save As)
%AppData%\G920Emulator\ffb-profiles\   ← force-feedback presets (Raw + your Save / Save As)
%AppData%\G920Emulator\settings.json
```

**Input profiles** and **FFB profiles** are separate. Default FFB is **Raw** (exact game mix). Adjust master / per-effect gains / output feel / torque shaping, then Save / Save As under Force feedback; each input profile stores which FFB profile it links to.

That way unzipping a newer release over `G920Emulator\` does not wipe or replace your buttons or feel. The status bar shows this path when you refresh devices.

- Bindings also store a stable DirectInput **product** id so devices can rematch if Windows changes the instance GUID after a replug.
- Optional USB-stick mode: create an empty `portable.txt` beside `G920Emulator.exe` to keep `profiles\`, `ffb-profiles\`, and `settings.json` next to the app instead (created at runtime — never shipped in the zip).

Details: [user guide → Profiles](docs/user-guide.md#profiles).

## What you get

- Virtual G920 that games see as a normal system HID / DirectInput wheel
- Binding UI for axes, buttons, POV hats, **D-pad Up/Down/Left/Right** (for pads without a hat), and axis→button mappings
- H-pattern gears **R, 1–6** (R defaults to button **19** / LGS; pick **12** in Bind Gear R for Unbound)
- Force feedback via our DirectInput OEM driver (`g920ffb.dll`) — not Logitech HID++ — with separate FFB profiles, per-effect gains, optional feel and torque-shaping sliders
- JSON input + FFB profiles in AppData (see above)

## Requirements

- Windows 10/11 (x64)
- [WinUHid](https://github.com/cgutman/WinUHid) — **bundled** in the release; install from the app ([guide](docs/driver-install.md))
- [HidHide](https://github.com/nefarius/HidHide) — **required** (separate download) so games don’t see both your real pad and the virtual G920
- Logitech Steering Wheel SDK — **bundled**; pinned only while the bridge is running so NFS Heat / Unbound-style games see a wheel, then restored on Stop/Close ([guide](docs/driver-install.md#logitech-steering-wheel-sdk--oem-ffb-session-scoped)). G HUB / Logitech Gaming Software are **not** needed. The bundled `LogitechSteeringWheel.dll` is © Logitech and not covered by this project's license ([details](native/logisdk/README.md)).

## Build from source

For contributors (not needed to play):

```powershell
.\publish.ps1 -OpenFolder
```

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Output: `dist\G920Emulator\` and `dist\G920Emulator-win-x64.zip` (includes `README.md` / `CHANGELOG.md`; no `profiles\` or `ffb-profiles\` — user data is AppData-only).

Day-to-day development:

```powershell
dotnet run --project src/G920Emulator.App
```

Details: [building](docs/building.md).

## Repository layout

```
src/G920Emulator.App          WPF UI
src/G920Emulator.Core         Input hub, mapper, bridge, FFB apply, AppData profiles
src/G920Emulator.VirtualHid   WinUHid + G920 identity + OEM registration
native/g920ffb                DirectInput OEM EffectDriver (g920ffb.dll)
native/winuhid                Bundled WinUHid runtime + INF (copied into dist)
native/logisdk                Bundled Logitech Steering Wheel SDK runtimes
docs/                         User and technical guides
tools/                        Build helpers (g920ffb, WinUHid, probes)
publish.ps1                   Self-contained win-x64 → dist\G920Emulator + zip
CHANGELOG.md                  Version notes
profiles/                     Repo reference only (not shipped; runtime data → AppData)
```

User bindings and FFB presets live in **`%AppData%\G920Emulator\`** at runtime — not in this `profiles/` folder and not in the release zip.

## Version notes

Release history lives in **[CHANGELOG.md](CHANGELOG.md)** (not the README). Current package: **0.2.5**.

## Support

Report bugs and ask questions on [GitHub Issues](https://github.com/camborambo/G920Emulator/issues). From the app: **Debug** (status bar) → short reproduce → **Stop debug** → **Export log…**, then attach the zip to your issue. Leave **Debug** off during normal play (especially Forza) — it is for brief captures only. Release notes on GitHub should match the matching section in [CHANGELOG.md](CHANGELOG.md).

## License

Project code in this repository is available for use with the project. WinUHid remains under its upstream license.
