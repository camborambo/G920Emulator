# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.7] - 2026-10-07

### Added

- **Telemetry** tab: optional SimHub External Sim UDP feed for games with no native telemetry (Heat / Unbound first). Speed/RPM/gear are simulated from pedals and shifter; rumble/impact/road load come from the virtual G920 FFB mix. Requires SimHub 9.11.5+. No game-process injection.
- **G920 Emulator (simulated)** External Sim registration (**Register with SimHub** / **Remove registration**) plus bundled **G920 Emulator RPM** plugin so ShakeIt built-in **Engine vibrations** works like native games.
- Telemetry **simulation profiles** (Save / Save as / Default / Delete) under `telemetry-profiles`. Host/port/rate stay global in settings.
- Telemetry **gearing** sliders (ratios, Diff, Tire, Redline) and hard-cut **Rev-limiter** with hysteresis.
- Telemetry **arcade buttons**: Handbrake / NOS-Turbo binds plus strength/boost sliders.
- Telemetry **Engine** scale (0-200%) under ShakeIt / FFB scales; RPM always sent, plugin applies the scale to Engine vibrations force.
- Simulated G-force for SimHub (`LocalSurgeMs2` / `LocalSwayMs2` / `LocalHeaveMs2`) and a live **G-force circle** on the Telemetry tab.
- **Telemetry Debug Overlay** (Settings): topmost live SimHub UDP packet (gear, speed/RPM, pedals, G-force, FFB→SimHub scales).
- FFB **Bind** dialog **Set as Default**: snap value + hardware button that restores that slider while driving (saved on the input profile).
- Telemetry UDP send rate slider **30-60 Hz** (60 is SimHub's External Sim max).

### Changed

- Settings **Debug Overlay** renamed to **FFB Debug Overlay** (same window; settings key unchanged).
- Telemetry RPM tracks **speed-in-gear** (not WOT→max): upshifts drop RPM; taller gears build RPM slower with Accel.
- Telemetry layout: controls stacked left; Live Telemetry alone on the right. Status notices use the main status bar.

### Fixed

- Install folder no longer stays "in use" after close: leave the app directory as CWD at startup, run helpers from `%TEMP%`, skip deferred pnputil cleanup on exit, and register `g920ffb.dll` from `%ProgramData%\G920Emulator\g920ffb\` so Steam/games do not lock the Desktop install folder. **Full clean restore** also deletes that `g920ffb` cache (and the whole `%ProgramData%\G920Emulator` tree when elevated).
- Telemetry RPM can reach configured **Max**: soft headroom uses √(remaining/band). Coast stays off-throttle only; aero scales with (1−throttle)² so WOT can pin the gear but lifting brings RPM down again.
- Telemetry tall gears can reach their configured max again: aero is per gear cap, and throttle fades free drag while climbing.
- Telemetry idle RPM: no longer set `SessionPaused` when OEM FFB goes quiet (SimHub was zeroing the dash), and keep `EngineRpm` floored at the configured idle while the session is live.
- SimHub **Engine vibrations** (built-in): External Sims never advertise the RPM feedback capability on their own; the **G920 Emulator RPM** plugin patches that when **G920 Emulator (simulated)** is active.
- HidHide **Start** / restore: batched CLI, shorter timeouts, gaming-devices-only hide list, detect open HidHide Client, and actually reapply the saved config on Stop/Exit.
- Settings menu labels: **Apply HidHide on Start** and **Restore my HidHide on Stop** (both off = leave HidHide alone).
- Telemetry **Redline** is a separate absolute RPM (SimHub `EngineShiftRpm`), not a percent of max.
- Telemetry no longer hard-clamps speed to the current gear cap (instant drop on downshift / N→gear).

## [0.2.6] - 2026-10-06

### Added

- App version in the window title, About, tray tip, and exported diagnostics (`Directory.Build.props`)
- **Check for GitHub updates** (Settings, on by default): banner when a newer published release exists. **Update** downloads `G920Emulator-{version}-win-x64.zip` to Downloads and opens that folder - unzip over your app folder yourself. **Later** hides that version until the next tag
- **Bind** on every Force Feedback slider (Master, Custom, feel, shaping, centering, mix): hardware −/+ while driving (1% tap / 5% hold on gains). Green **Bind** means a − or + is assigned; the bind dialog has **Clear** per side
- **Debug Overlay** (Settings): topmost live G920 inputs + FFB diagnostics
- **Effect Changes Overlay** (Settings, on by default): brief top-center HUD of category, slider name, and value when FFB bind buttons fire
- FFB debug / overlay lists each DirectInput OEM effect type (seen / playing) with MIX totals, matching `g920ffb-effects.log`
- Diagnostics capture emulator vs game process CPU/RAM (10 s snapshots + export). Game GPU/CPU being high is expected; GPU is not sampled

## [0.2.5] - 2026-10-05

### Added

- **Forza Horizon 6 FFB (work in progress):** OEM path now arms Constant Force / periodics when Forza re-downloads full `DIEP_ALL` every frame (previously silent FFB). Input + force feedback are usable; feel tuning and broader validation are still ongoing.
- FFB profile **Default** button resets sliders to Raw defaults; main window widened so Save / Save As / Default / Delete fit

### Removed

- **SessionWatch** crash watchdog - OEM/SDK pins still restore on Stop/close; after a hard crash they clear on the next emulator launch

### Changed

- Docs / diagnostics HOW-TO: leave status-bar **Debug** off for normal play (especially Forza); use only for short captures
- README: prominent post-install step - turn **test signing off** and **re-enable Secure Boot** once WinUHid is installed

### Fixed

- **Forza FFB silent:** games that re-download full DIEP_ALL (`0x3FF`) every frame (Forza / some Steam Input paths) never armed Constant Force - only Unbound-style `0x100` param streams did. Updates on an existing effect handle now arm CF/periodics; Steam Aux publishes the full mix when no game Torque channel is live.
- **Steam unload harden:** `g920ffb` PIN now falls back to a permanent `LoadLibrary` ref if pin fails, and holds a COM lock for process lifetime (crash stamp `0x6AC32A35` was the pre-pin Oct 4 build; no `steam.exe` faults after the pinned builds).
- **OEM FFB polarity:** Constant / ramp / periodic forces were published in DI device sense while springs used app sense (`+` = right); after `FfbBridge`’s base negation, CF pushed into the turn (Unbound’s Invert Constant Force was compensating). Non-condition effects now convert DI→app; conditions no longer take `DIEFFECT` direction; Unbound/Heat seed drops the CF invert workaround.
- **Debug FFB log stall:** with Debug on, Forza’s per-frame OEM downloads (~600 lines/s) opened/wrote/closed the log on the game thread and could freeze game input while the emulator UI stayed live - log is now rate-limited, keeps the file open, and rotates at 4 MB
- **Start after Stop:** OEM/SDK restore always runs even if WinUHid stop is slow; Start waits longer for teardown before recreating the virtual G920

## [0.2.4] - 2026-10-05

### Added

- **Dependencies → Windows test signing** as its own step (Enabled / Disabled / Reboot required / Off OK) with Enable/Disable actions and a Secure Boot warning
- **Forza-friendly WinUHid install:** enable test signing only for install → install driver → turn test signing off automatically (WinUHid usually keeps working; FH6 can launch)
- **Uninstall WinUHid** removes the driver **and** turns off Windows test signing, then offers a reboot
- **Dependencies → Full clean restore…** - optional nuclear wipe of app system leftovers (OEM/SDK, SDK cache, WinUHid, test signing, etc.)
- Session-scoped OEM / Logitech SDK pins on Start bridge (restored on Stop/close / next launch); Dependencies status card (Active / Idle / Needs restore)
- Docs / README: Forza Horizon + Secure Boot guidance (Secure Boot can be re-enabled after install)

### Changed

- Dependencies UI: numbered setup (test signing → WinUHid → HidHide); OEM/SDK is a session status card like G HUB guard
- Main window dependency chips show test signing instead of OEM/SDK
- Test signing shows **Off (OK)** when WinUHid is installed and test mode is off - not permanently required

### Fixed

- **Steam crash** (`steam.exe` / `g920ffb.dll_unloaded` ACCESS_VIOLATION): pin OEM FFB DLL for process lifetime; `DllCanUnloadNow` refuses unload
- Forza Horizon 6 splash exit (code 100): Windows **test signing** left on (not OEM/SDK pins)
- WinUHid Install no longer stops after enabling test signing without installing the driver (avoids duplicate enumerators / false “Not installed”)
- WinUHid status distinguishes **Reboot required** / **Not responding** from **Not installed**
- OEM restore deletes the whole `VID_046D&PID_C262` OEM tree when needed; stronger WinUHid uninstall for leftovers

## [0.2.3] - 2026-10-04

### Added

- FFB profile **Need For Speed Unbound / Heat** (Desktop-era mix: CF/Spring/Damper gains, Invert Constant Force, damper velocity/deadband scales, light torque shaping)
- Advanced mix options on FFB profiles: Invert Constant Force, damper velocity scale, damper deadband scale (applied in `g920ffb.dll`)
- Click FFB % / value labels to type exact numbers
- Shifter mode tooltip explaining Exclusive H-pattern vs Passthrough

### Fixed

- Fanatec (pinned FFB) wheel buttons frozen while steering still worked - overlay buttons/hat from the exclusive FFB handle
- Bind-on-the-fly while the bridge is running (bind dialog uses FFB overlay poll)
- Live Buttons line now shows View, Menu, LSB, RSB (and active gear)
- Newly bound devices included in the poll set without restarting the bridge
- Live preview respects pinned FFB when FFB debug Attach is used without Start

## [0.2.2] - 2026-10-04

### Fixed

- Steering hitches on Simucube / multi-device rigs: device rescan no longer blocks the 500 Hz input loop; poll only bound devices; cache winning FFB `SetParameters` flags; soften rim catch-up after slow DI applies

## [0.2.1] - 2026-10-04

### Fixed

- Start bridge no longer freezes the UI when the wheel is moving (DirectInput preview poll moved off the UI thread)
- Closing the app always exits the process (capped DI/WinUHid teardown + hard exit failsafe)

## [0.2.0] - 2026-10-04

### Added

- Status-bar **Debug** session that gates OEM (`g920ffb-effects.log`) and HID++ ingress file logging; **Export log…** only after **Stop debug**
- Shared-memory v6 with game `Torque` plus Steam/overlay `AuxTorque` layer
- Separate FFB profiles (`ffb-profiles\`), per-effect gains, and optional output-feel / torque-shaping controls (default **Raw** = pass-through)
- Diagnostics zip extras: live FFB snapshot, HidHide dump, OEM registry, `game-ffb-analysis.txt` (Unbound race signature / Vibration hint)
- Device rematch by product id when Windows changes instance GUIDs
- AppData profile storage (input + FFB); portable mode via `portable.txt`

### Changed

- Physical FFB apply runs on a side thread so slow DirectInput bases (e.g. Simucube) do not stall virtual G920 axis updates
- Boot soft-arm for Constant Force / periodics: placeholder full creates stay quiet until Start or a non-zero param stream
- Steam OEM loads as **SESSION HOST** (DI effects ok; SHM publish is Aux only) so helpers cannot wipe the game mix
- Diagnostics export removed from **About** (use Debug → Stop → Export log…)
- Docs / README: Simucube listed as tested; Unbound **Controller Vibration** callout

### Fixed

- Pinning an FFB device no longer freezes steering (skip Poll on the pinned exclusive joystick; use axis cache)
- Micro-stutter / lag on axes when the physical base’s `SetParameters` was slow
- Startup null-reference when UI timers were not ready before slider events

### Tested

- FFB bases: Fanatec Podium Wheel Base DD2 (NFS Heat / Unbound), Simucube (NFS Unbound)
- Games: NFS Heat, NFS Unbound (Controller Vibration must be On for race rumble)

## [0.1.0] - 2026-10-03

### Added

- Initial public build: virtual Logitech G920 (WinUHid), binding UI, HidHide helpers, OEM `g920ffb.dll` path, bundled WinUHid + Logitech Steering Wheel SDK
- NFS Heat / Unbound gear mapping (reverse button 19 vs 12)

[Unreleased]: https://github.com/camborambo/G920Emulator/compare/v0.2.7...HEAD
[0.2.7]: https://github.com/camborambo/G920Emulator/compare/v0.2.6...v0.2.7
[0.2.6]: https://github.com/camborambo/G920Emulator/compare/v0.2.5...v0.2.6
[0.2.5]: https://github.com/camborambo/G920Emulator/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/camborambo/G920Emulator/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/camborambo/G920Emulator/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/camborambo/G920Emulator/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/camborambo/G920Emulator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/camborambo/G920Emulator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/camborambo/G920Emulator/releases/tag/v0.1.0
