# Force feedback

## Goal

Forward game-authored DirectInput force-feedback from the virtual G920 to a physical wheel base. **Validated on Fanatec Podium DD2** (NFS Heat / Unbound) and **Simucube** (NFS Unbound); also designed for Simagic, Moza, Logitech, and other DirectInput FFB bases. **Raw** leaves magnitudes unshaped; optional **effect gains**, **output feel**, and **torque shaping** sliders (saved on FFB profiles) let you adjust the mix without baking feel into the driver.

## FFB profiles (separate from input)

Force-feedback presets live in `%AppData%\G920Emulator\ffb-profiles\` (not in the install zip). They are **independent of input binding profiles**.

| Preset | Meaning |
|--------|---------|
| **Raw** (default) | Exact game mix — master/effect gains 100%, all feel / torque shaping / advanced mix options off. Cannot be deleted. |
| **Need For Speed Unbound / Heat** | Desktop-era known-good mix for Heat/Unbound: CF 200%, Spring 40%, Damper 150%, damper vel ×2 / deadband ×⅓, light torque shaping (deadband 0.004, slew 40, DI ε 12). Seeded once; editable/deletable. |
| Your Save / Save As… | Capture current master gain, invert, per-effect gains, output feel, advanced mix options, and torque shaping into a named JSON |

**UI:** under Force feedback, pick the profile in the dropdown (its own row), then use **Save** / **Save As…** / **Delete** on the row below. Click any **% / value** label beside a slider to type an exact number (Enter to apply, Esc to cancel).

Each input profile stores an `ffbProfileName` link. Changing the FFB dropdown loads that preset into the sliders; saving the input profile also quiet-saves the linked FFB file.

Built-in presets are **Raw** and **Need For Speed Unbound / Heat**. Earlier short seeds (**NFS Unbound**, **Classic**) are removed on startup if they still match the original seeded values; a profile you edited under those names is kept.

## Why not Logitech HID++?

A real G920 with G HUB installed binds `logi_joy_hid_filter` on Col01. Logitech’s FFB stack talks to that filter with **private IOCTLs** (HID++), which never appear as normal HID output reports on a WinUHid virtual device. Result: host write counters stay at 0 even when the game “sees” a G920.

We deliberately advertise **`&REV_9601`** on the hardware ID so that INF does not bind. Games still recognize VID/PID via DirectInput OEM registry.

See [research-logitech-g920.md](research-logitech-g920.md).

## Active path: OEM EffectDriver

```
Game → DirectInput → g920ffb.dll (IDirectInputEffectDriver)
     → shared memory Local\G920Emulator.FfbTorque.v6
     → BridgeService → optional feel / torque shaping → FfbBridge → physical base (DI constant force)
```

| Piece | Detail |
|-------|--------|
| COM CLSID | `{A920FFB0-E7DB-4329-8C13-A966D84A289F}` |
| DLL | `g920ffb.dll` next to `G920Emulator.exe` (built from `native/g920ffb`) |
| Registration | Session-scoped: `OemRegistrationSession.BeginSession()` on Start bridge; restored on Stop/Close (`EndSession`), or next launch after a crash (`RecoverIfDirty`) |
| Shared memory | Magic `G9FF`, version 6 (`…FfbTorque.v6`): game `Torque` + optional `AuxTorque` (Steam/overlay), playing, steering in/out, type bitmasks, per-type gains, OEM mix flags/scales. Each process mixes its own OEM instances; bridge sums game + aux. |
| Effect gains | Per-type sliders applied in `g920ffb.dll` before mix; master gain applies on the physical base |
| Advanced mix options | Optional inside `g920ffb.dll`: Invert Constant Force, damper velocity scale, damper deadband scale (see below). Defaults = pass-through |
| Output feel / torque shaping | Optional, after the advanced mix, in the emulator (see below). Defaults = pass-through |

### Effect types mixed natively

Constant, Ramp, Square, Sine, Triangle, Sawtooth up/down, Spring, Damper, Inertia, Friction (and Custom reserved). Condition effects (spring/damper/…) use **physical rim angle** fed from the FFB output device (not the virtual/DualSense steer).

Downloads with `DIEP_START`, Unbound-style param-only streams (`0x100`), or **subsequent** full updates on an existing handle (Forza-style `0x3FF` every frame) auto-start continuous CF / conditions / non-zero periodics so titles that never call `StartEffect` still feel force. The *first* full create alone stays quiet (Unbound boot placeholders). When Steam Input is the only OEM host, Aux publishes the full mix until a game process owns Torque.

Timing and shaping follow DirectInput semantics:

- Infinite-duration periodics run their phase off elapsed time (they vibrate, not hold a DC level).
- Envelopes (`DIEP_ENVELOPE`) apply attack to every effect; fade applies only to finite durations. The sign of the magnitude is preserved.
- `DIEFFECT.dwGain` defaults to 10000, and a game-sent 0 is honored.
- `StartEffect` with `DIES_SOLO` stops the other effects.
- Damper/inertia velocity is rim units per second, smoothed over ~15 ms; ±10000 corresponds to 3 rim units/s. Friction has a small velocity deadband so it doesn't chatter at rest.
- If the driver thread stops publishing for more than 250 ms (game exited or crashed), the bridge sends zero torque instead of holding the last value.
- Multi-axis spring/damper downloads: the driver picks the strongest condition axis (largest \|coeff\|) so a zeroed first axis does not mute the wheel.
- **Polarity:** shared-memory torque is app convention (`+` = right). Spring/damper are evaluated from the physical rim in that space. Constant / ramp / periodic magnitudes are converted from DI device sense → app before publish (so CF resists the turn instead of amplifying it). Condition effects ignore `DIEFFECT` direction.

### Physical apply

`FfbBridge` collapses the mixed torque to one DirectInput **constant force** on the selected base, sharing the `InputHub` joystick acquire (avoids dual exclusive acquire failures on Fanatec and similar).

## Sliders (saved on the FFB profile)

All of these are **user optional**. On **Raw**, everything below is at the “off / 100%” defaults so the game mix is unchanged.

### Master + Invert

| Control | Default | Notes |
|---------|---------|--------|
| **Master** | 100% (0–200%) | Multiplier on the physical constant-force apply |
| **Invert FFB** | off | Flips torque direction for bases with opposite sense |

### Effect gains

Scale each DirectInput effect type **in the mixer** before summing (0% mutes that type; up to **200%**):

| Slider | Typical use |
|--------|-------------|
| **Constant** | Road feel / tire load |
| **Spring** | Arcade return-to-center |
| **Damper** | Motion damping |
| **Friction** | Static friction |
| **Inertia** | Acceleration-related condition |
| **Periodic** | Sine / square / triangle / sawtooth rumble |
| **Ramp** | Ramp force |

### Advanced mix (inside `g920ffb.dll`)

Applied while evaluating effects, before the shared-memory torque is published. Independent of **Invert FFB** (which flips the whole final torque on the base).

| Control | Default | NFS Unbound / Heat | Notes |
|---------|---------|--------------------|--------|
| **Invert Constant Force** | off | off | Extra CF flip only — driver already converts DI CF → app polarity (`+` = right) |
| **Damp vel** | 100% | **200%** | Scales rim velocity before damper/inertia condition eval |
| **Damp dead** | 100% | **~33%** | Scales damper deadband before eval |

### Output feel

Applied **after** the advanced mix (emulator side):

| Slider | Range | Off | Notes |
|--------|-------|-----|--------|
| **Smoothing** | 0–40 ms | **0** | Low-pass time constant. Try ~8–15 on some DD bases if FFB feels harsh |
| **Peak soft** | 50–100% | **100%** | Soft-knee for strong peaks; 100% = no compression |
| **Soft start** | 0–2000 ms | **0** | One-shot ease-in when FFB first appears (does not re-arm every frame) |

FFB debug Left / Right / Center / Pulse skip this path.

### Torque shaping (ShapeGameTorque)

Optional deadband / slew / spike / DI chatter controls (same idea as the Desktop fork’s always-on shaper, but **user-tunable and off by default**):

| Slider | Range | Off | Notes |
|--------|-------|-----|--------|
| **Deadband** | 0–0.05 | **0** | Ignore \|torque\| below this. Try ~0.004 for chatter |
| **Slew** | 0–200 /s | **0** (unlimited) | Max \|torque\| change per second; zero target ramps down 2.5× faster. Heavy slew can mute crash rumble — keep low or off for Raw |
| **Spike cap** | 5–100% | **100%** | Max single-frame step toward target; 100% = allow full steps |
| **DI epsilon** | 0–64 | **0** | Skip physical DI updates when \|Δmagnitude\| is below this (0…10000 scale). Helps Fanatec grind from ±1 chatter; try ~12 |

### Centering (Force center spring)

For games that never center the wheel. When **Force center spring** is checked, the emulator adds a spring computed from the **physical rim angle** to the game's torque. It's added after smoothing and shaping so it never lags, and it's scaled by Master. It stays on even in menus and pause, when no game is sending feedback. It's pre-compensated for **Invert FFB**, so it always pulls toward center. A small built-in damping keeps direct-drive bases from oscillating.

| Control | Range | Default | Effect |
|---|---|---|---|
| **Force center spring** | on/off | **off** | Enables the emulator spring |
| **Strength** | 5–100% | 30% | Maximum centering torque |
| **Range** | 5–50% | 25% | Rim offset (fraction of rotation to one side) where the spring reaches full strength. Lower = stiffer near center |
| **Deadzone** | 0–5% | off | Free zone around center with no spring |

Leave it off for games that already send a DI Spring (most sims); stacking both makes centering heavier. Use the **Spring** effect gain to adjust the game's own spring instead.

## FFB debug UI

Under **Force feedback**, check **FFB debug** to show:

- Attach FFB (normally Start bridge attaches)
- Left / Center / Right / Pulse / Release test + test torque slider
- Diagnostics: OEM status, effects seen/playing, host/HID++ counters, apply counts, rim angle
Off by default so everyday use stays uncluttered. File logging (OEM effects + HID++ ingress) is gated by the status-bar **Debug** button, not this checkbox.

## Status-bar Debug (OEM file log)

**Leave Debug off for normal racing.** It enables `%TEMP%\g920ffb-effects.log` (and HID++ ingress logging) from inside the game process. Titles that re-download effects every frame (Forza Horizon, some Steam Input paths) can generate hundreds of lines per second; older builds opened/closed the file on every write and could freeze game input while the emulator UI stayed live. Current `g920ffb.dll` rate-limits stream lines, keeps the file open, and rotates at 4 MB — still use Debug only for short diagnostic captures, then **Stop debug**.

**FFB debug** (the checkbox) is separate: live counters and test pulses with no file I/O on the game thread.

## Probing which effects a game uses

1. Start bridge with an FFB output device selected.
2. Click **Debug** (status bar) so OEM file logging is on, then launch the game.
3. Optional: enable **FFB debug** to watch live OEM effects seen / playing.
4. Reproduce briefly, then **Stop debug** and **Export log…** (preferred for support zips). Do not leave Debug on for a full race. Starting **Debug** again clears the previous `%TEMP%` OEM / HID++ log files.

While a Debug session is active (and until the next Start clears them), you can also open:

```
%TEMP%\g920ffb-effects.log
```

Each `DownloadEffect` logs type, handle, flags, and a type-specific “extra” (CF magnitude, spring offset, damper coeff, periodic magnitude). Parameter-only streaming updates are rate-limited per effect (see **Reading the OEM log** below). Spring downloads also emit `SPRING_DETAIL` lines.

When a game opens the virtual wheel's FFB, the log gets a `SESSION g920ffb loaded pid=… exe=…` line. If no SESSION line appears for a game, it did not load the OEM driver, so its forces never reached the emulator. The log rotates to `g920ffb-effects.log.old` past 4 MB.

### NFS Unbound

At menu/load Unbound downloads **ConstantForce**, **Sine**, **Damper**, **Spring**, and in-race often **Triangle** (and streams `0x100` every frame). Universal mixing is required.

Unbound can construct **multiple** OEM driver instances; older single-instance mixers published torque=0 while `effects.log` still grew. Current `g920ffb.dll` mixes all live instances and uses SHM **v6** (`Torque` + `AuxTorque`) so Steam/idle helpers cannot overwrite the game channel.

For a stronger tire-follow / lighter arcade center on DD bases, raise **Constant** and lower **Spring** in the effect gains, then **Save As…** your own FFB profile. Default runtime path stays **Raw**.

**Arcade auto-center (centering spring)**

Heat / Unbound pull the wheel back to center like an arcade cabinet. In DirectInput that is the **Spring** effect (mixed with ConstantForce, Damper, Sine, Triangle, Square — nothing is muted by default).

1. Select your **physical FFB base** under Force feedback (not DualSense). Spring uses that rim angle.
2. Keep **Spring** (and other) effect gains above 0% unless you intend to mute a type.
3. Do **not** rely on hardware `DIPROP_AUTOCENTER` during gameplay — it is left off so it cannot fight the OEM mix. FFB debug **Center** is a manual software return-to-center test only.
4. OEM log (`%TEMP%\g920ffb-effects.log`) should list Spring / Damper / CF / periodic while driving. FFB debug shows **Rim (spring)** and **OEM effects playing**.

### Reading the OEM log

- `EFFECT` lines are what the game sent. Each effect handle is logged when its value changes (at most every 100 ms), when it goes to or from zero, and as a 1 s heartbeat.
- `CALL` lines record the game's other driver calls: `DeviceID`, `SendForceFeedbackCommand` (RESET / STOPALL / PAUSE / CONTINUE / ACTUATORSON / ACTUATORSOFF), `SetGain` (on change), `StopEffect`, `DestroyEffect`, `Escape`, and `GetForceFeedbackState` (when the reported state changes). The driver reports the safety switch as on, so games see a wheel that can play force feedback.
- `MIX` lines (1 s) show what each type contributed to the output torque: `cf`, `periodic`, `spring`, `damper`, `other`, `total`, with `playing` as a DI type bitmask.
- **Target device is always the virtual G920.** HidHide must hide every other FFB joystick from the game (physical base, vJoy, pads). The emulator stays whitelisted so it can still read those devices for binding and for applying torque to the base you pick.
- Shared memory `Local\G920Emulator.FfbTorque.v6`: the **game** publishes `Torque`; Steam/overlay may only layer `AuxTorque` (logged `SESSION HOST` / `AUX PUBLISH`). The bridge applies the sum.
- **NFS Unbound:** Accessibility → Controls → **Controller Vibration = On**. Confirmed root cause when Vibration is Off: race still creates Triangle/CF/Damper, but all streamed magnitudes stay 0 (spring-only `MIX`). In-race with Vibration On: `GetEffectStatus` → `DestroyEffect` (boot Sine) → Triangle → non-zero `cf` / `periodic` / damper on `MIX`.
- **vJoy may stay installed** (Joystick Gremlin / remappers). Keep feeder apps on the HidHide whitelist. The vJoy device itself must be **hidden from the game** when it advertises FFB, or Unbound can send forces there instead of the virtual G920. Seeing vJoy in the emulator device list does **not** mean the game sees it (this app is whitelisted). Check `hidhide.txt` for `VID_1234&PID_BEAD`, or use **Dependencies → Configure HidHide** (now also picks up vJoy from `--dev-all`).
- If `MIX` only shows `spring` and there are no non-zero CF/periodic samples while driving, the **game** is not streaming those effects to the virtual G920. Confirm HidHide hides the physical FFB base and any competing FFB devices from the game; keep in-game FFB on. **SimHub can stay running** — mixed rigs (Simucube base + Fanatec shifter + SimHub) are supported.

## Launch soft-start

Games often **download** effects at full magnitude (Sine/Square rumble, CF) during boot.

1. **Driver:** **Constant Force**, **Ramp** and **Periodics** (Sine/Triangle/…) do **not** arm on the initial full create (`0x3FF`). Unbound creates CF at ~5000 and Sine at 10000 as placeholders; arming them would hold a constant pull or full rumble. They arm on `DIEP_START`, `StartEffect`, or a later parameter-only stream (`0x100`) with a non-zero magnitude. Conditions (Spring/Damper) arm on create so arcade auto-center works.
2. **Emulator feel sliders (optional):** Soft start / Smoothing / Peak soft / torque shaping default to **off** on **Raw**. Raise them and Save / Save As an FFB profile if a direct-drive base feels too raw or boot hits are harsh.

## Latency notes

Input path targets ~500 Hz with adaptive pacing; physical DI torque apply runs on a **side thread** so a slow base `SetParameters` (common on Simucube) cannot stall virtual G920 axis submits. Extra latency only appears if you raise **Smoothing** or heavy **Slew**. Physical USB/driver latency usually dominates feel in-race.

## Scope / non-goals

- Works for games that drive a **G920 via DirectInput OEM effects**.
- Pure XInput rumble-only titles, proprietary SDKs, or FFB that only exists inside the Logitech HID++ filter path are out of scope for this OEM bridge.
