# Steering Wheel Emulator

**Use any wheel, pedals, H-shifter, or controller as a virtual racing wheel** - Logitech G920 by default, or Fanatec DD1 when a title prefers that identity.

Steering Wheel Emulator is a **hobby project**: a Windows app that creates a **virtual wheel** on your PC (toolbar **Emulated device**: **Logitech G920** `046D:C262`, or **Fanatec DD1** `0EB7:0004`). You bind your physical DirectInput devices to that virtual wheel, hide the real hardware from games with HidHide, and optionally forward **force feedback** to your Fanatec, Simucube, Simagic, Moza, or other DirectInput FFB base. Both identities share **`emuffb.dll`** for OEM FFB.

```
Physical devices  →  Steering Wheel Emulator  →  Virtual G920 / Fanatec PC Comp  →  Game
Game FFB effects  →  emuffb.dll    →  Physical base
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
| **[Building](docs/building.md)** | Publish the EXE or build `emuffb.dll` from source |
| **[Research notes](docs/research-logitech-g920.md)** | Read G HUB / HID++ findings and the NFS Unbound probe |

## Tested so far

| Category | Tested |
|----------|--------|
| Input | DualSense |
| FFB bases | **Fanatec Podium Wheel Base DD2**, **Simucube** |
| Games | **NFS Heat**, **NFS Unbound**, **Forza Horizon 5**, **Forza Horizon 6** |

**Gear R:** Heat uses default button **19**. Unbound needs Bind Gear R → **12**. Full matrix: [compatibility](docs/compatibility.md).

**Unbound tip:** Accessibility → Controls → **Controller Vibration** must be **On**, or the game streams spring-only (walls/rumble stay at magnitude 0).

**Forza tip:** Set toolbar **Emulated device** to **Fanatec DD1** (while Stopped) so Forza detects a Fanatec PC Comp wheel. In Forza, pick **any Fanatec wheel profile**, then **custom bind** axes/buttons to build your layout. The virtual G920 identity is a worse fit for Forza (sparse G920-class FFB). Details: [user guide](docs/user-guide.md#forza-horizon--use-fanatec-dd1) · [force-feedback.md](docs/force-feedback.md#how-games-author-ffb-forza-vs-nfs-g920-vs-dd).

### After WinUHid is installed (important)

WinUHid is test-signed, so install needs **Windows test signing on** and usually **Secure Boot off** in UEFI. **Once WinUHid is installed, you do not leave the PC in that state:**

1. **Turn test signing off** - **Install WinUHid** already does this at the end. If Dependencies still shows test signing on: **Disable test signing** → reboot. (Forza Horizon exits at splash with code **100** while test signing is on.)
2. **Turn Secure Boot back on** in UEFI/BIOS if you disabled it for the install. WinUHid (UMDF) usually keeps working with Secure Boot on and test signing off.

Emulator and Forza can coexist on the same boot after that. Details: [driver-install](docs/driver-install.md#forza-horizon-6-and-test-signing).

## Quick start (download a release)

No Visual Studio or .NET SDK required.

1. Download **`SteeringWheelEmulator-win-x64.zip`** from the latest [**Releases**](https://github.com/camborambo/SteeringWheelEmulator/releases) page.
2. Extract the zip - you get a **`SteeringWheelEmulator`** folder.
3. Run **`SteeringWheelEmulator.exe`** inside that folder.
4. Open **Dependencies** → **Install WinUHid** (bundled; approve UAC; may reboot once or twice). When install finishes: confirm **test signing is off**, then **re-enable Secure Boot** in UEFI if you turned it off for install (see [above](#after-winuhid-is-installed-important)).
5. Install **HidHide** from its download link → **Configure HidHide**.  
   Step-by-step: [driver install](docs/driver-install.md).
6. Pick **Emulated device** while Stopped (**Fanatec DD1** for Forza; **Logitech G920** for Heat / Unbound-style titles). **Refresh** devices, bind steering / pedals / buttons / gears **R-6** (and D-pad via hat or **D-pad Up/Down/Left/Right**), pick an **FFB output device** + **FFB profile**, then **Save** your input profile (also stores a HidHide snapshot when capture succeeds).
7. **Start bridge**, then launch your game and select the virtual wheel (matching **Emulated device**). In Forza: choose any Fanatec profile → custom bind.

Later releases: the app can show an **Update** banner when GitHub has a newer zip. That only downloads to **Downloads** - unzip over your `SteeringWheelEmulator` folder yourself (AppData profiles are kept). You can turn the check off under **Settings**.

Binding changes apply while the bridge is running - no need to Stop/Start after rebinding. Full walkthrough: [user guide](docs/user-guide.md).

## Profiles (where your binds / FFB live)

Profiles are **not** inside the install / zip folder. On first run the app creates:

```
%AppData%\SteeringWheelEmulator\profiles\              ← input bindings (Default.json + your Save / Save As)
%AppData%\SteeringWheelEmulator\ffb-profiles\          ← force-feedback presets (Raw + your Save / Save As)
%AppData%\SteeringWheelEmulator\telemetry-profiles\    ← SimHub simulation presets (Save / Save As)
%AppData%\SteeringWheelEmulator\settings.json
```

Older installs under `%AppData%\G920Emulator\` (and matching ProgramData / LocalAppData) are migrated into these folders on first launch after the rename.

**Input**, **FFB**, and **Telemetry** profiles are separate. Default FFB is **Raw** (exact game mix). Adjust master / per-effect gains / output feel / torque shaping, then Save / Save As under Force feedback; each input profile stores which FFB profile it links to. Telemetry host/port/rate stay in `settings.json`.

That way unzipping a newer release over `SteeringWheelEmulator\` does not wipe or replace your buttons or feel. The status bar shows this path when you refresh devices.

- Bindings also store a stable DirectInput **product** id so devices can rematch if Windows changes the instance GUID after a replug.
- Optional USB-stick mode: create an empty `portable.txt` beside `SteeringWheelEmulator.exe` to keep `profiles\`, `ffb-profiles\`, and `settings.json` next to the app instead (created at runtime - never shipped in the zip).

Details: [user guide → Profiles](docs/user-guide.md#profiles).

## Features

### Virtual G920 (why this exists)

Many racing games only expose a full Logitech wheel profile for the G920. This app creates a **virtual G920** that Windows and those games treat as a real wheel (`VID 046D` / `PID C262`). You keep using your Fanatec, Simucube, DualSense, pedals, H-shifter, or other DirectInput gear - the game only sees the G920.

### Input mapping

Bind axes, buttons, POV hats, and **D-pad Up/Down/Left/Right** (for pads without a hat), including axis-to-button. H-pattern gears **R** and **1-6** map to the official G920 shifter buttons (R defaults to **19**; Unbound needs **12**). Changes apply while the bridge is running - no restart needed after rebinding.

- **Axis range** (start/end) on axis→axis binds remaps the usable throw.
- **Activate on Axis** on axis→button binds sets the % where the digital press fires.
- **Custom bindings** (after Gear 6): Binding Wizard with **Bind Button**, optional **Bind FN**, and a **Toggle** checkbox (hold vs latch).

### Force feedback

Games write Logitech OEM FFB to the virtual G920. Our `emuffb.dll` driver captures that mix and applies it to your physical DirectInput base (Fanatec, Simucube, Simagic, Moza, and similar). Tune with separate **FFB profiles**: master gain, per-effect gains, output feel, torque shaping, and optional in-car **Bind** buttons so you can adjust while driving. Default profile **Raw** passes the game mix unchanged.

### SimHub telemetry (games without Data Out)

Titles like NFS Heat / Unbound have no native SimHub plugin. The **Telemetry** tab sends a simulated UDP feed SimHub can use for dashboards and ShakeIt:

- **Speed / RPM / gear** from your pedals and shifter, with **gear-ratio simulation** (ratios, Diff, Tire, Redline) so each gear has a realistic top speed and RPM curve
- **G-force** and FFB-derived rumble / impact / road load from the virtual G920 mix
- Optional **Handbrake** / **NOS** binds for arcade-style speed effects
- **Register with SimHub** installs the External Sim definition plus the **Steering Wheel Emulator RPM** plugin so built-in ShakeIt Engine vibrations work

Use this only for games that lack real telemetry. For Forza and similar, keep SimHub on the native game plugin.

### Overlays and updates

- **FFB Debug Overlay** / **Telemetry Debug Overlay** - live meters on top of the game
- **Effect Changes Overlay** - brief on-screen toasts when you tweak FFB with Bind buttons
- Optional GitHub **Update** check downloads the release zip to Downloads (profiles stay in AppData)

### Profiles

Input, FFB, and Telemetry settings are separate JSON profiles under `%AppData%\SteeringWheelEmulator\` so updating the app zip does not wipe your binds or feel. See [Profiles](#profiles-where-your-binds--ffb-live).

## Requirements

- Windows 10/11 (x64)
- [WinUHid](https://github.com/cgutman/WinUHid) - **bundled** in the release; install from the app ([guide](docs/driver-install.md))
- [HidHide](https://github.com/nefarius/HidHide) - **required** (separate download) so games don’t see both your real pad and the virtual G920
- Logitech Steering Wheel SDK - **bundled**; pinned only while the bridge is running so NFS Heat / Unbound-style games see a wheel, then restored on Stop/Close ([guide](docs/driver-install.md#logitech-steering-wheel-sdk--oem-ffb-session-scoped)). G HUB / Logitech Gaming Software are **not** needed. The bundled `LogitechSteeringWheel.dll` is © Logitech and not covered by this project's license ([details](native/logisdk/README.md)).

## Build from source

For contributors (not needed to play):

```powershell
.\publish.ps1 -OpenFolder
```

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Output: `dist\SteeringWheelEmulator\` and `dist\SteeringWheelEmulator-win-x64.zip` (includes `README.md` / `CHANGELOG.md`; no `profiles\` or `ffb-profiles\` - user data is AppData-only).

Day-to-day development:

```powershell
dotnet run --project src/SteeringWheelEmulator.App
```

Details: [building](docs/building.md).

## Repository layout

```
SteeringWheelEmulator.sln
src/SteeringWheelEmulator.App          WPF UI
src/SteeringWheelEmulator.Core         Input hub, mapper, bridge, FFB apply, AppData profiles
src/SteeringWheelEmulator.VirtualHid   WinUHid + G920 identity + OEM registration
src/SteeringWheelEmulator.SimHubPlugin SimHub RPM capability plugin
native/emuffb                DirectInput OEM EffectDriver (emuffb.dll)
native/winuhid                Bundled WinUHid runtime + INF (copied into dist)
native/logisdk                Bundled Logitech Steering Wheel SDK runtimes
docs/                         User and technical guides
simhub/                       SimHub External Sim definition + SteeringWheelEmulator.SimHubPlugin.dll (Register installs both)
tools/                        Build helpers (emuffb, WinUHid, probes)
publish.ps1                   Self-contained win-x64 → dist\SteeringWheelEmulator + zip
CHANGELOG.md                  Version notes
profiles/                     Repo reference only (not shipped; runtime data → AppData)
```

User bindings and FFB presets live in **`%AppData%\SteeringWheelEmulator\`** at runtime - not in this `profiles/` folder and not in the release zip.

## Version notes

Release history lives in **[CHANGELOG.md](CHANGELOG.md)** (not the README). Current package: **0.2.8**.

## Support

Report bugs and ask questions on [GitHub Issues](https://github.com/camborambo/SteeringWheelEmulator/issues). From the app: **Debug** (status bar) → short reproduce → **Stop debug** → **Export log…**, then attach the zip to your issue. Leave **Debug** off during normal play (especially Forza) - it is for brief captures only. Release notes on GitHub should match the matching section in [CHANGELOG.md](CHANGELOG.md).

## License

This is a **hobby project**. Source is public for personal, **non-commercial** use only — see [LICENSE](LICENSE). You may read, modify, and run it yourself; you may **not** use it for commercial purposes. Third-party components bundled with releases (for example WinUHid) remain under their upstream licenses.
