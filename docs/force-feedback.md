# Force feedback

## Goal

Forward game-authored DirectInput force-feedback from the virtual G920 to a physical wheel base (Fanatec, Simucube, Simagic, Moza, Logitech, generic DI FFB) **without reshaping** magnitudes (no soft-cap / slew). Device gain in the UI is a user multiplier only.

## Why not Logitech HID++?

A real G920 with G HUB installed binds `logi_joy_hid_filter` on Col01. Logitech’s FFB stack talks to that filter with **private IOCTLs** (HID++), which never appear as normal HID output reports on a WinUHid virtual device. Result: host write counters stay at 0 even when the game “sees” a G920.

We deliberately advertise **`&REV_9601`** on the hardware ID so that INF does not bind. Games still recognize VID/PID via DirectInput OEM registry.

See [research-logitech-g920.md](research-logitech-g920.md).

## Active path: OEM EffectDriver

```
Game → DirectInput → g920ffb.dll (IDirectInputEffectDriver)
     → shared memory Local\G920Emulator.FfbTorque
     → BridgeService → FfbBridge → physical base (DI constant force)
```

| Piece | Detail |
|-------|--------|
| COM CLSID | `{A920FFB0-E7DB-4329-8C13-A966D84A289F}` |
| DLL | `g920ffb.dll` next to `G920Emulator.exe` (built from `native/g920ffb`) |
| Registration | `G920OemRegistration.EnsureRegistered()` on Start |
| Shared memory | Magic `G9FF`, version 3: torque, playing, steering in/out, type bitmasks |

### Effect types mixed natively

Constant, Ramp, Square, Sine, Triangle, Sawtooth up/down, Spring, Damper, Inertia, Friction (and Custom reserved). Condition effects (spring/damper/…) use **physical rim angle** fed from the FFB output device (not the virtual/DualSense steer).

Downloads with `DIEP_START` or type-specific params auto-start continuous CF / conditions / non-zero periodics so titles that never call `StartEffect` still feel force.

### Physical apply

`FfbBridge` collapses the mixed torque to one DirectInput **constant force** on the selected base, sharing the `InputHub` joystick acquire (avoids dual exclusive acquire failures on Fanatec and similar).

## FFB debug UI

Under **Force feedback**, check **FFB debug** to show:

- Attach FFB (normally Start bridge attaches)
- Left / Center / Right / Pulse / Release test + test torque slider
- Diagnostics: OEM status, effects seen/playing, host/HID++ counters, apply counts

Off by default so everyday use stays uncluttered.

## Probing which effects a game uses

1. Start bridge with an FFB output device selected.
2. Enable **FFB debug** and launch the game.
3. Watch **OEM effects seen / playing**, or open:

```
%TEMP%\g920ffb-effects.log
```

Each `DownloadEffect` logs type, handle, flags, and a type-specific “extra” (magnitude or coefficient).

### NFS Unbound (2026-10-04)

At menu/load Unbound downloaded: **ConstantForce**, **Sine**, **Damper**, **Spring** (centering coeff 10000 on one spring slot). Universal mixing is required; spring+rumble alone is incomplete.

## Latency notes

Emulator-added delay is roughly a few milliseconds from ~500 Hz OEM mix + bridge loops. There is no intentional force shaping delay. Physical USB/driver latency usually dominates feel.

## Scope / non-goals

- Works for games that drive a **G920 via DirectInput OEM effects**.
- Pure XInput rumble-only titles, proprietary SDKs, or FFB that only exists inside the Logitech HID++ filter path are out of scope for this OEM bridge.
