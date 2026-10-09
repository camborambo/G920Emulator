# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Debug session logs **FFB_TUNE** lines to `g920emulator-bridge-health.log` when effect gains / output feel are applied (values + shared-memory readback) so exports can prove slider changes reached `g920ffb`.
- Telemetry **Max gears** in the Gearing card (1–10): UDP `MaxGears` → SimHub `CarSettings_MaxGears`, shows that many gear ratio / top-speed rows (paddles can use 7–10 for speed sim; H-shifter still 1–6). Gear tops use the [Blocklayer](https://www.blocklayer.com/rpm-gear) formula MPH = (tire × ShiftAt) / (336 × gear × diff) for all gears including 7–10; absolute speed ceiling raised to 1600 km/h so tall overdrive is not clipped through AbsoluteRpmMax. After updating, **Remove registration** → restart SimHub → **Register with SimHub** again if the packet layout changed.
- Telemetry **Arcade buttons → Sequential shifter** (opt-in): bind **Gear up** / **Gear down** / **Gear reset** on the input profile for sequential/paddle boxes that do not map to H-pattern gears. Pattern is **R → 1 → Max gears**; Reset jumps to **1**. When enabled, H-pattern and bumper-paddle gear inference are ignored.
- **Custom bindings** after Gear 6: Binding Wizard with **Bind Button** + optional **Bind FN** popups, plus a **Toggle** checkbox (hold vs latch). If FN is bound, the button requires that key held. Remove with **X**.
- Axis→button on standard Assign and custom Binding Wizard: capturing an axis for a button target converts automatically (**Activate on Axis** threshold + live meter). Checkbox label is **Invert**.
- **Axis range** (start/end) for axis→axis bindings: dual-handle slider remaps the usable throw. Separate from **Activate on Axis** (single % for axis→button).
- Tabbed **Settings** modal (General / Overlays / HidHide) from the gear button; tray menu is Restore / Settings… / Exit.
- HidHide apply modes: **Off** (default), **Hide all (except emulator)**, **Hide bound devices only**, plus **Save & restore on Stop** (same snapshot behavior as before).

### Changed

- Telemetry **Downshift settle** (Speed dynamics): after a downshift, speed bleeds toward that gear’s max instead of snapping. Separate from Coast / Brake; 0 = off. (Restores the old Gear settle path that had been hard-pinned.)
- FFB label clarity: **Force deadzone** (was Deadband; not Fanatec DEA), **Slew rate** (Simucube-style), **DI chatter** (was DI epsilon), **Damper velocity** / **Damper deadzone** (was Damp vel / Damp dead). Bind ids unchanged.
- **Damper velocity** max capped at **200%** (was 400%) to match Master / effect gains; legacy values above 200% clamp down.
- FFB **Raw** / Default is always exact game mix (100% gains, no feel/shaping) and cannot be overwritten — Save with tweaks prompts **Save As…**; Default switches to Raw. Tuned presets are user-created only.
- Renamed Output feel **Soft start** → **Boot ease-in**, now an on/off toggle: when on, mute forces for ~5 s when FFB first comes online so the base does not kick (was a ms fade slider). Moved under Force Feedback → **Advanced Settings**.
- Renamed Force Feedback → **Advanced mix** → **Advanced Settings**.
- Checkboxes use a **toggle switch** style app-wide (Settings, Force Feedback, bind dialogs); same on/off behavior.
- **Soft steering catch-up** moved from Settings → Debug Test to Force Feedback → **Advanced Settings** (with Invert FFB). Saved with the FFB profile (default off).
- **Invert FFB** moved under Force Feedback → **Advanced Settings** (with Invert Constant Force / damper scales).
- Replaced Settings checkboxes **Apply HidHide on Start** / **Restore my HidHide on Stop** with the HidHide tab radios + restore checkbox (`hidHideApplyMode` in `settings.json`; legacy `autoApplyHidHideConfigOnStart` migrates).

### Fixed

- **Peak soft** slider: **0 = off**, slide up for more peak compression (was inverted: 100% = off, lower = stronger).
- **Spike cap:** limit is a per-second |Δtorque| cap (like Slew), not a raw per-call clamp. Slider goes to **0 = off** (was stuck at 5% minimum with “off” only at 100%). Legacy 100% migrates to 0. On the ~500 Hz OEM path, lowering the slider no longer turns into hard stair-steps that feel more rugged on DD bases.
- **HidHide Hide bound devices only:** each **Start** syncs the hide list to the active profile — hides bound devices (match by DirectInput instance/product GUID and VID/PID) and **unhides** other gaming devices left hidden from a previous full-rig / Hide-all session. Wheel-only profiles no longer leave pedals/shifters cloaked after you switch profiles (or manually unhide them in HidHide Client).
- **Install folder still “in use” by Steam:** never re-register `g920ffb.dll` COM InprocServer32 to the install folder when the ProgramData cache copy fails; rewrite stale Desktop/install paths (HKCU/HKLM × 32/64-bit) to `%ProgramData%\G920Emulator\g920ffb\`; update the cache via temp+Replace. Diagnostics `oem-registry.txt` shows install vs ProgramData. If the folder was already locked from an older load, close Steam once after updating.
- **HOST_STALE / mid-session disconnect:** virtual G920 uses **interrupt-push** WinUHid input (no ReadReport pull mode) so `SubmitInputReport` is not gated on a pending host read. Status + Debug log `HOST_STALE` / 2s heartbeats (`hostReadAgeMs`, `notReadyDelta`). **No mid-session Col01 auto-recover** for HOST_STALE or hard submit-fail (log + Stop/Start only; Settings opt-in later). Start/Stop orphan Col01 cleanup unchanged. Validated on Fanatec (idle + ~30 min live Unbound).
- On bridge Start, disable Device Manager power-saving (“Allow the computer to turn off this device…”) for **WinUHid / VHF** nodes so Windows cannot sleep the virtual G920 while the app stays Running. Best-effort (needs elevation).
- Telemetry synth, UDP send, and game-process probe run on a **side thread** (same idea as Simucube FFB apply). When Telemetry is off, the input loop skips that work entirely so SimHub I/O cannot stall virtual G920 reports.
- Telemetry-on path no longer contends with HID/FFB on `BridgeService`’s main lock or double-reads OEM shared memory; process probe interval is 5s. (Freeze A/B: issue tracked with Telemetry enabled.)
- Telemetry-on further lightened after Fanatec `HOST_STALE` (game stops reading Col01 while FFB continues): input thread enqueues at **SendHz** (not every ~2 ms), caches arcade-bind presence, and sheds samples when `hostReadAge ≥ 250 ms`.
- Telemetry/OEM hot path: no per-frame `CombinedTypeTorqueDi` / effect-table string allocs (reuse buffers, throttle FFB diag text ~10 Hz), skip game-process probe while OEM is playing, avoid per-packet Engine-scale file writes and status-string rebuilds, ASCII gear packet fast-path, Telemetry UI meters only while that tab/overlay is visible.
- Debug bridge-health log records `steer`/`thr`/`brk`, FFB cache age, and `STALE_INPUT` when the virtual report stops changing (Debug session only).
- Settings → **Debug Test** tab: Enable only reveals knobs; defaults match the last stable release (Exclusive, locked SetParameters, blocking rim reads). **Default** restores that path. Sub-options are for A/B only; re-attaches FFB when Enable/coop/dual-handle changes. Debug capture records effective + stored Debug Test flags.
- Debug Test → **Dual-handle input** (opt-in): standalone Exclusive FFB joystick while InputHub keeps NonExclusive Poll (unpinned). Fixes the Fanatec case where sharing Exclusive on InputHub + a second Poll handle still flatlined game input; FFB forces stay Exclusive.
- Debug bridge-health: `hostReadAgeMs` / `notReady` / `HOST_STALE` when mapped pedals move but the game stops reading the virtual G920 (distinguishes emulator soft-freeze from game DI/host stall).
- Input profiles with unknown binding targets (e.g. saved by a newer build) no longer crash on launch: invalid rows are skipped and a **Profile needs attention** dialog asks you to rebind and Save.

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
- Telemetry UDP send rate slider **1-60 Hz** (60 is SimHub's External Sim max).

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
