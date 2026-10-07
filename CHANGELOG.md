# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Settings **Debug Overlay** renamed to **FFB Debug Overlay** (same window; settings key unchanged).

### Added

- **Telemetry Debug Overlay** (Settings): topmost live SimHub UDP packet (gear, speed/RPM, pedals, G-force, FFB→SimHub scales).
- **G920 Emulator RPM** SimHub plugin: installed by **Register with SimHub**; enables ShakeIt built-in **Engine vibrations** for the External Sim (sets the RPM feedback capability the same way native games like Forza do). Restart SimHub after registering.
- Telemetry **estimation profiles** (Save / Save as / Default / Delete), same pattern as FFB profiles — stored under `telemetry-profiles`. Host/port/rate stay global in settings.
- Telemetry **Engine** scale (0–200%) under ShakeIt / FFB scales — same pattern as Rumble / Impact / Road load. RPM is always sent; the SimHub RPM plugin applies the scale to Engine vibrations force.
- Telemetry **RPM bounce** / **Bounce rate**: rev-limiter flutter when estimated speed/RPM is pinned at a gear’s top (adjustable depth and Hz; 0 bounce = flat ceiling).
- Telemetry RPM now tracks **speed-in-gear** (not WOT→max): upshifts drop RPM, and taller gears build RPM slower with **Gear pull** / Accel.
- Telemetry **Gear settle** rate: after a downshift, estimated speed tapers toward the new gear's max instead of snapping; adjustable under Estimation tuning.
- **Telemetry** tab: optional SimHub External Sim UDP feed for games with no native telemetry (Heat / Unbound first). Speed/RPM/gear are estimated from pedals and shifter; rumble/impact/road load come from the virtual G920 FFB mix. Requires SimHub 9.11.5+. No game-process injection.
- **Remove registration** on the Telemetry tab deletes the SimHub `.simlink` so you can drop the game tile (and cached icon) after a restart.
- Telemetry UDP send rate slider is **30–60 Hz** (60 is SimHub's External Sim max; faster packets are ignored).
- Simulated G-force for SimHub: `LocalSurgeMs2` / `LocalSwayMs2` / `LocalHeaveMs2` from speed change, steering, and FFB impact/rumble (ShakeIt-ready; not real chassis physics). Re-register the External Sim after updating.
- Telemetry **G-force circle** (friction circle): live ball on X/Y axes for surge × sway.
- FFB **Bind** dialog **Set as Default**: pick a snap value (slider + label) and a hardware button that restores that slider while driving (saved on the input profile).
- Telemetry **Estimation tuning**: Speed/RPM min–max ranges and SurfaceRumble / Impact / RoadLoad scales (saved in settings).

### Fixed

- Install folder no longer stays "in use" after close: leave the app directory as CWD at startup, run helpers from `%TEMP%`, skip deferred pnputil cleanup on exit, and register `g920ffb.dll` from `%ProgramData%\G920Emulator\g920ffb\` so Steam/games do not lock the Desktop install folder. **Full clean restore** also deletes that `g920ffb` cache (and the whole `%ProgramData%\G920Emulator` tree when elevated).
- Telemetry RPM can reach configured **Max**: soft headroom uses √(remaining/band). Coast stays off-throttle only; aero scales with (1−throttle)² so WOT can pin the gear but lifting brings RPM down again. Tiny brake noise ignored under gas. Telemetry Debug Overlay shows gear-speed %.
- Telemetry RPM no longer jumps near Max: removed the late pin catch-up that forced a faster RPM tau above 97% gear speed.
- Telemetry: removed gear-cap ease pull and bounce-from-Max (both snapped RPM on short gears). Under strong throttle, impact dumps are skipped so √ headroom can finish the pin without a late yank. Debug overlay shows RPM as % of max.
- Telemetry tall gears can reach their configured max again: aero was keyed off global vmax (drag wall ~30 mph short in 5th). Aero is per gear cap, and throttle fades free drag while climbing so WOT can work up to each gear max.
- Telemetry idle RPM: no longer set `SessionPaused` when OEM FFB goes quiet (SimHub was zeroing the dash), and keep `EngineRpm` floored at the configured idle while the session is live.
- Telemetry RPM no longer snaps to 0 at the bottom of a taper in gear: stopping (speed &lt; 0.5) used to end the session; in-gear / short linger now holds idle instead.
- SimHub **Engine vibrations** (built-in): External Sims never advertise the RPM feedback capability on their own; the **G920 Emulator RPM** plugin patches that when **G920 Emulator (estimated)** is active, and applies the **Engine** force scale from this app.
- HidHide **Start** no longer reads the restore-point snapshot before you answer Yes/No; capture/apply use one batched CLI script (was four+ sequential HidHideCLI launches). Device hide scans once after the virtual G920 exists.
- HidHide restore on Stop/Exit no longer blocks for up to 90s: batched CLI, short timeouts (exit ~6s / stop ~12s), orphan helpers killed, unfinished restore retries on next launch.
- HidHide restore actually reapplies the saved config: no more false “success” without writing lists, incomplete diffs when live lists failed to load, or clearing the restore point before verification.
- HidHide auto-hide only cloaks devices from HidHide’s **Gaming devices only** list (`--dev-gaming`) — no more `--dev-all` system scan. Also unhides obvious non-gaming false positives left by older builds.
- Start detects an open **HidHide Client** (driver lock / 0x0005) and offers to close it before reading or applying config.
- Start after Stop no longer fails with **CLI returned no data**: orphan cleanup was leaving a cancel flag that aborted the next HidHide capture.
- Settings menu labels: **Apply HidHide on Start** and **Restore my HidHide on Stop** (both off = leave HidHide alone).
- Telemetry RPM max slider now matches what SimHub receives: full throttle reaches the configured max (gear no longer caps below it).
- Telemetry **Redline** is a separate absolute RPM (SimHub `EngineShiftRpm`), not a percent of max — adjustable next to idle–max.
- Telemetry no longer hard-clamps speed to the current gear cap (instant drop on downshift / N→gear).

## [0.2.6] - 2026-10-06

### Added

- App version in the window title, About, tray tip, and exported diagnostics (`Directory.Build.props`)
- **Check for GitHub updates** (Settings, on by default): banner when a newer published release exists. **Update** downloads `G920Emulator-{version}-win-x64.zip` to Downloads and opens that folder — unzip over your app folder yourself. **Later** hides that version until the next tag
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

- **SessionWatch** crash watchdog — OEM/SDK pins still restore on Stop/close; after a hard crash they clear on the next emulator launch

### Changed

- Docs / diagnostics HOW-TO: leave status-bar **Debug** off for normal play (especially Forza); use only for short captures
- README: prominent post-install step — turn **test signing off** and **re-enable Secure Boot** once WinUHid is installed

### Fixed

- **Forza FFB silent:** games that re-download full DIEP_ALL (`0x3FF`) every frame (Forza / some Steam Input paths) never armed Constant Force — only Unbound-style `0x100` param streams did. Updates on an existing effect handle now arm CF/periodics; Steam Aux publishes the full mix when no game Torque channel is live.
- **Steam unload harden:** `g920ffb` PIN now falls back to a permanent `LoadLibrary` ref if pin fails, and holds a COM lock for process lifetime (crash stamp `0x6AC32A35` was the pre-pin Oct 4 build; no `steam.exe` faults after the pinned builds).
- **OEM FFB polarity:** Constant / ramp / periodic forces were published in DI device sense while springs used app sense (`+` = right); after `FfbBridge`’s base negation, CF pushed into the turn (Unbound’s Invert Constant Force was compensating). Non-condition effects now convert DI→app; conditions no longer take `DIEFFECT` direction; Unbound/Heat seed drops the CF invert workaround.
- **Debug FFB log stall:** with Debug on, Forza’s per-frame OEM downloads (~600 lines/s) opened/wrote/closed the log on the game thread and could freeze game input while the emulator UI stayed live — log is now rate-limited, keeps the file open, and rotates at 4 MB
- **Start after Stop:** OEM/SDK restore always runs even if WinUHid stop is slow; Start waits longer for teardown before recreating the virtual G920

## [0.2.4] - 2026-10-05

### Added

- **Dependencies → Windows test signing** as its own step (Enabled / Disabled / Reboot required / Off OK) with Enable/Disable actions and a Secure Boot warning
- **Forza-friendly WinUHid install:** enable test signing only for install → install driver → turn test signing off automatically (WinUHid usually keeps working; FH6 can launch)
- **Uninstall WinUHid** removes the driver **and** turns off Windows test signing, then offers a reboot
- **Dependencies → Full clean restore…** — optional nuclear wipe of app system leftovers (OEM/SDK, SDK cache, WinUHid, test signing, etc.)
- Session-scoped OEM / Logitech SDK pins on Start bridge (restored on Stop/close / next launch); Dependencies status card (Active / Idle / Needs restore)
- Docs / README: Forza Horizon + Secure Boot guidance (Secure Boot can be re-enabled after install)

### Changed

- Dependencies UI: numbered setup (test signing → WinUHid → HidHide); OEM/SDK is a session status card like G HUB guard
- Main window dependency chips show test signing instead of OEM/SDK
- Test signing shows **Off (OK)** when WinUHid is installed and test mode is off — not permanently required

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

- Fanatec (pinned FFB) wheel buttons frozen while steering still worked — overlay buttons/hat from the exclusive FFB handle
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

[Unreleased]: https://github.com/camborambo/G920Emulator/compare/v0.2.6...HEAD
[0.2.6]: https://github.com/camborambo/G920Emulator/compare/v0.2.5...v0.2.6
[0.2.5]: https://github.com/camborambo/G920Emulator/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/camborambo/G920Emulator/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/camborambo/G920Emulator/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/camborambo/G920Emulator/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/camborambo/G920Emulator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/camborambo/G920Emulator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/camborambo/G920Emulator/releases/tag/v0.1.0
