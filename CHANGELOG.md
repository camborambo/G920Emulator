# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.3] - 2026-10-04

### Added

- FFB profile **Need For Speed Unbound / Heat** (Desktop-era mix: CF/Spring/Damper gains, Invert Constant Force, damper velocity/deadband scales, light torque shaping)
- OEM mix options on FFB profiles: Invert Constant Force, damper velocity scale, damper deadband scale (applied in `g920ffb.dll`)
- Click FFB % / value labels to type exact numbers

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

[Unreleased]: https://github.com/camborambo/G920Emulator/compare/v0.2.2...HEAD
[0.2.2]: https://github.com/camborambo/G920Emulator/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/camborambo/G920Emulator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/camborambo/G920Emulator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/camborambo/G920Emulator/releases/tag/v0.1.0
