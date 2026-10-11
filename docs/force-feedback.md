# Force feedback

## Goal

Forward game-authored DirectInput force-feedback from the virtual wheel (**Logitech G920** or **Fanatec DD1**) to a physical wheel base via shared **`emuffb.dll`**. **Validated on Fanatec Podium DD2** (NFS Heat / Unbound) and **Simucube** (NFS Unbound); also designed for Simagic, Moza, Logitech, and other DirectInput FFB bases. **Raw** leaves magnitudes unshaped; optional **effect gains**, **output feel**, and **torque shaping** sliders (saved on FFB profiles) let you adjust the mix without baking feel into the driver.

## FFB profiles (separate from input)

Force-feedback presets live in `%AppData%\SteeringWheelEmulator\ffb-profiles\` (not in the install zip). They are **independent of input binding profiles**.

| Preset | Meaning |
|--------|---------|
| **Raw** (default) | Exact game mix - master/effect gains 100%, all feel / torque shaping / Advanced Settings options off. Cannot be deleted or overwritten; **Default** always switches here. Use Save As… for tuned presets. |
| **Need For Speed Unbound / Heat** | Desktop-era known-good mix for Heat/Unbound: CF 200%, Spring 40%, Damper 150%, Damper velocity ×2 / Damper deadzone ×⅓, light torque shaping (Force deadzone 0.004, Slew rate 40, DI chatter 12). Seeded once; editable/deletable. |
| Your Save / Save As… | Capture current master gain, invert, per-effect gains, output feel, Advanced Settings options, and torque shaping into a named JSON |

**UI:** under Force feedback, pick the profile in the dropdown (its own row), then use **Save** / **Save As…** / **Delete** on the row below. Click any **% / value** label beside a slider to type an exact number (Enter to apply, Esc to cancel).

Each input profile stores an `ffbProfileName` link. Changing the FFB dropdown loads that preset into the sliders; saving the input profile also quiet-saves the linked FFB file.

Built-in presets are **Raw** and **Need For Speed Unbound / Heat**. Earlier short seeds (**NFS Unbound**, **Classic**) are removed on startup if they still match the original seeded values; a profile you edited under those names is kept.

## Why not Logitech HID++?

A real G920 with G HUB installed binds `logi_joy_hid_filter` on Col01. Logitech’s FFB stack talks to that filter with **private IOCTLs** (HID++), which never appear as normal HID output reports on a WinUHid virtual device. Result: host write counters stay at 0 even when the game “sees” a G920.

We deliberately advertise **`&REV_9601`** on the hardware ID so that INF does not bind. Games still recognize VID/PID via DirectInput OEM registry.

See [research-logitech-g920.md](research-logitech-g920.md).

## Active path: OEM EffectDriver

```
Game → DirectInput → emuffb.dll (IDirectInputEffectDriver)
     → shared memory Local\G920Emulator.FfbTorque.v7
     → BridgeService → optional feel / torque shaping → FfbBridge → physical base (DI constant force)
```

| Piece | Detail |
|-------|--------|
| COM CLSID | `{A920FFB0-E7DB-4329-8C13-A966D84A289F}` |
| DLL | `emuffb.dll` next to `SteeringWheelEmulator.exe` (built from `native/emuffb`) |
| Registration | Session-scoped: `OemRegistrationSession.BeginSession()` on Start bridge; restored on Stop/Close (`EndSession`), or next launch after a crash (`RecoverIfDirty`) |
| Shared memory | Magic `G9FF`, version 7 (`…FfbTorque.v7`): game `Torque` + optional `AuxTorque` (Steam/overlay), playing, steering in/out, type bitmasks, per-type mix torque, per-type gains, OEM mix flags/scales. Each process mixes its own OEM instances; bridge sums game + aux. |
| Effect gains | Per-type sliders applied in `emuffb.dll` before mix; master gain applies on the physical base |
| Advanced Settings options | Optional inside `emuffb.dll`: Invert Constant Force, damper velocity scale, damper deadband scale (see below). Defaults = pass-through |
| Output feel / torque shaping | Optional, after Advanced Settings mix scales, in the emulator (see below). Defaults = pass-through |

### Effect types mixed natively

Constant, Ramp, Square, Sine, Triangle, Sawtooth up/down, Spring, Damper, Inertia, Friction (and Custom reserved). Condition effects (spring/damper/…) use **physical rim angle** fed from the FFB output device (not the virtual/DualSense steer).

Downloads with `DIEP_START`, Unbound-style param-only streams (`0x100`), or **subsequent** full updates on an existing handle (Forza-style `0x3FF` every frame) auto-start continuous CF / conditions / non-zero periodics so titles that never call `StartEffect` still feel force. The *first* full create alone stays quiet (Unbound boot placeholders). When Steam Input is the only OEM host, Aux publishes the full mix until a game process owns Torque.

Timing and shaping follow DirectInput semantics:

- Infinite-duration periodics run their phase off elapsed time (they vibrate, not hold a DC level).
- Envelopes (`DIEP_ENVELOPE`) apply attack to every effect; fade applies only to finite durations. The sign of the magnitude is preserved.
- `DIEFFECT.dwGain` defaults to 10000, and a game-sent 0 is honored.
- `StartEffect` with `DIES_SOLO` stops the other effects.
- Damper/inertia velocity is rim units per second, smoothed over ~15 ms; ±10000 corresponds to 3 rim units/s. Friction has a small velocity deadband so it doesn't chatter at rest.
- Spring (position conditions) evaluates against an EMA-smoothed rim metric (light τ when Interpolate is on). Always on — not gated on vehicle speed; constant/periodic forces ignore that metric. Spring torque itself is not separately filtered; sparse-CF grain can be softened via Output feel Interpolate / Gap fill / Device pace.
- If the driver thread stops publishing for more than 250 ms (game exited or crashed), the bridge sends zero torque instead of holding the last value.
- Multi-axis spring/damper downloads: the driver picks the strongest condition axis (largest \|coeff\|) so a zeroed first axis does not mute the wheel.
- **Polarity:** shared-memory torque is app convention (`+` = right). Spring/damper are evaluated from the physical rim in that space. Constant / ramp / periodic magnitudes are converted from DI device sense → app before publish (so CF resists the turn instead of amplifying it). Condition effects ignore `DIEFFECT` direction.

### Physical apply

`FfbBridge` collapses the mixed torque to one DirectInput **constant force** on the selected base, sharing the `InputHub` joystick acquire (avoids dual exclusive acquire failures on Fanatec and similar).

**Unified unpaced core (all bases):** Physical CF uses the release unpaced apply path by default (Fanatec, Simucube, Generic, …). Optional Output feel sliders soften sparse OEM streams when needed:

| Control | Default | Notes |
|---------|---------|--------|
| **Device pace** | **0 (off)** | How often the mixed force is sent to the wheel (ms). Try 2–5 if forces feel stepped. |
| **Interpolate** | **0 (off)** | Blend window (ms) on the mixed force for sparse updates. |
| **Gap fill** | **0 (off)** | Hold last force across update gaps when Interpolate &gt; 0. |

Each slider adds its effect independently when set above 0. Interpolate / Gap fill / Smoothing / Peak soft / torque shaping adjust the mixed force; Device pace rates how often that mix is sent to the base. Apply-path EMA stays off. `FFB_TUNE` logs `devicePace` / `interpolate` / `gapFill` / `shmRecon`. Settings → **Debug Test** → **CF pacing on Fanatec** still forces a 3 ms pace when Device pace feel is off (legacy A/B). Offline: `tools/IdleSmoothReplay`.

## How games author FFB (Forza vs NFS, G920 vs DD)

Games do **not** send one universal force stream. They pick an effect mix (and update rate) from the **device class they think they are talking to**. With the emulator, that device is the toolbar **Emulated device** (default **virtual G920**, or **Fanatec DD1**) — your physical Fanatec / Simucube / etc. is only the playback base.

### Recommendation for Forza Horizon

Use **Emulated device → Fanatec DD1** so Forza detects a Fanatec PC Comp wheel. In Forza, select **any Fanatec wheel profile**, then **custom bind** axes/buttons to create your custom wheel profile. Step-by-step: [user guide — Forza Horizon](user-guide.md#forza-horizon--use-fanatec-dd1).

### Findings (Forza Horizon on virtual G920 — why DD1 is better)

Validated while chasing “grainy” idle / light-steer feel on **Simucube** with FH5/FH6 on the **G920** identity:

1. **Device profile matters more than the base brand.** Forza authors different DirectInput mixes for a Logitech G920 OEM wheel than for a Fanatec / high-end DD path. Presenting a G920 means you get the **G920-authored** mix, not a Fanatec-class one.
2. **G920 path → sparse Constant Force.** On the virtual G920, Forza’s road/tire feel is often a **low-rate CF stream** (tens of Hz, stepped magnitudes). On a high-bandwidth DD that faithfully plays every step, that reads as **grain / stair-steps**.
3. **Fanatec-class path → better match.** Presenting **Fanatec DD1** (`0EB7:0004`) lets Forza treat the wheel like Fanatec PC Comp — pick any Fanatec profile in-game and custom-bind. Native Fanatec captures also lean on spring / condition-heavy forces with a smoother envelope.
4. **NFS Unbound / Heat differ again.** Keep **Logitech G920** for those titles. Unbound tends to download a richer mix (Triangle / periodic + CF + damper, with Controllers → Vibration on).
5. **What the emulator can and cannot do.** We play whatever the game downloads for the virtual identity. Switching **Emulated device** changes which class Forza sees; Output feel sliders only reshape the mix after the fact.

### Practical takeaway

| Symptom | Likely cause | What to try |
|---------|--------------|-------------|
| Forza wheel detection / binds feel wrong on G920 | Wrong device class | **Emulated device → Fanatec DD1**; in Forza pick any Fanatec profile → custom bind |
| Grainy idle / light steer in Forza on a DD while on G920 | Sparse G920 CF from the game | Switch to Fanatec DD1; or Output feel (Device pace / Interpolate / Gap fill) as a fallback |
| NFS Unbound weak rumble | Controller Vibration off / spring-only menus | Accessibility → Vibration On; check in-race MIX in Debug export |

Leave status-bar **Debug** off for normal Forza play (per-frame OEM downloads); use short captures when comparing mixes.

## Sliders (saved on the FFB profile)

All of these are **user optional**. On **Raw**, everything below is at the “off / 100%” defaults so the game mix is unchanged.

### Master

| Control | Default | Notes |
|---------|---------|--------|
| **Master** | 100% (0-200%) | Multiplier on the physical constant-force apply |

### Effect gains

Scale each DirectInput effect type **in the mixer** before summing (0% mutes that type; up to **200%**). On the Force Feedback tab, **Bind** on any slider (Master, effect gains including Custom, output feel, torque shaping, centering, Advanced Settings) opens a dialog to assign hardware buttons for **Lower (−)**, **Raise (+)**, and **Set as Default** while you drive (stored on the **input** profile; not virtual G920 controls). Set as Default includes a slider for the snap value; pressing that bind jumps the control back to it. A filled green **Bind** button means that slider already has a −, +, or default bind; **Clear** on each row removes it. Gain sliders step 1% per tap and 5% if you hold; other sliders use a matching small / faster step in their own units.

| Slider | Typical use |
|--------|-------------|
| **Constant** | Road feel / tire load |
| **Spring** | Arcade return-to-center |
| **Damper** | Motion damping |
| **Friction** | Static friction |
| **Inertia** | Acceleration-related condition |
| **Periodic** | Sine / square / triangle / sawtooth rumble |
| **Ramp** | Ramp force |
| **Custom** | Downloaded custom-force effects |

### Advanced Settings

UI: Force Feedback → **Advanced Settings**. Boot ease-in / Invert FFB / soft steering catch-up apply on the emulator side; the CF/damper/coefficient rows below are applied inside `emuffb.dll` while evaluating effects (before shared-memory torque is published).

| Control | Default | NFS Unbound / Heat | Notes |
|---------|---------|--------------------|--------|
| **Boot ease-in** | off | off | When on, mute game torque for ~5 s after FFB first comes online so the base does not kick/shake |
| **Invert FFB** | off | off | Flips whole final torque on the base (not steering input) |
| **Soft steering catch-up** | off | off | Limits per-frame virtual steering jumps after a brief DI/USB stall |
| **Invert Constant Force** | off | off | Extra CF flip only - driver already converts DI CF → app polarity (`+` = right). Independent of Invert FFB |
| **Damper velocity** | 100% (25–200%) | **200%** | Scales rim velocity before damper/inertia condition eval (not Fanatec NDP/DPR) |
| **Damper deadzone** | 100% | **~33%** | Scales damper deadband before eval |
| **Spring coeff** | 100% (0–200%) | 100% | Scale spring coefficients only (saturation unchanged) — stronger near center sooner |
| **Friction coeff** | 100% (0–200%) | 100% | Scale friction coefficients; higher can feel gritty/backlashing |

### Output feel

Applied **after** Advanced Settings mix scales (emulator side):

| Slider | Range | Off | Notes |
|--------|-------|-----|--------|
| **Smoothing** | 0-40 ms | **0** | Low-pass time constant. Try ~8-15 on some DD bases if FFB feels harsh |
| **Device pace** | 0-34 ms | **0** | Send period for the mixed force; 0 = every update (`cfPacePeriodMs`) |
| **Interpolate** | 0-100 ms | **0** | Mix blend window (`reconstructionMs`) |
| **Gap fill** | 0-200 ms | **0** | Hold across gaps when Interpolate &gt; 0 (`idleGapHoldMs`) |
| **Peak soft** | 0-100% | **0** (off) | Soft-knee for strong peaks. 0 = off; slide up for more compression (maps to knee at 100%→50% \|torque\|) |

FFB debug Left / Right / Center / Pulse skip this path.

### Torque shaping (ShapeGameTorque)

Optional Force deadzone / Slew rate / Spike cap / DI chatter controls (same idea as the Desktop fork’s always-on shaper, but **user-tunable and off by default**):

| Slider | Range | Off | Notes |
|--------|-------|-----|--------|
| **Force deadzone** | 0-0.05 | **0** | Ignore \|torque\| below this (not a steering-angle deadzone like Fanatec DEA). Try ~0.004 for chatter |
| **Slew rate** | 0-200 /s | **0** (unlimited) | Max \|torque\| change per second (same idea as Simucube Slew Rate Limit); zero target ramps down 2.5× faster. Heavy slew can mute crash rumble - keep low or off for Raw |
| **Spike cap** | 0-100% | **0** (off) | Max \|Δtorque\| per second toward target (~5-100 /s when on). Lower = softer; **0 = off**. Independent of game Hz. (Legacy 100% = off migrates to 0; was a raw per-call clamp that felt more rugged when lowered on the 500 Hz path.) |
| **DI chatter** | 0-64 | **0** | Skip physical DI updates when \|Δmagnitude\| is below this (0…10000 scale). Helps Fanatec grind from ±1 chatter; try ~12 |

### Centering (Force center spring)

For games that never center the wheel. When **Force center spring** is checked, the emulator adds a spring computed from the **physical rim angle** to the game's torque. It's added after smoothing and shaping so it never lags, and it's scaled by Master. It stays on even in menus and pause, when no game is sending feedback. It's pre-compensated for **Invert FFB**, so it always pulls toward center. A small built-in damping keeps direct-drive bases from oscillating.

| Control | Range | Default | Effect |
|---|---|---|---|
| **Force center spring** | on/off | **off** | Enables the emulator spring |
| **Strength** | 5-100% | 30% | Maximum centering torque |
| **Range** | 5-50% | 25% | Rim offset (fraction of rotation to one side) where the spring reaches full strength. Lower = stiffer near center |
| **Deadzone** | 0-5% | off | Free zone around center with no spring |

Leave it off for games that already send a DI Spring (most sims); stacking both makes centering heavier. Use the **Spring** effect gain to adjust the game's own spring instead.

## FFB debug UI

Under **Force Feedback**, expand **FFB debug** to show:

- Attach FFB (normally Start bridge attaches)
- Left / Center / Right / Pulse / Release test + test torque slider
- Diagnostics: OEM MIX (cf / periodic / spring / damper / other) plus each DirectInput type as seen or playing, host/HID++ counters, apply counts, rim angle
Off by default so everyday use stays uncluttered. File logging (OEM effects + HID++ ingress) is gated by the status-bar **Debug** button, not this checkbox.

## Status-bar Debug (OEM file log)

**Leave Debug off for normal racing.** It enables `%TEMP%\emuffb-effects.log` (and HID++ ingress logging) from inside the game process. Titles that re-download effects every frame (Forza Horizon, some Steam Input paths) can generate hundreds of lines per second; older builds opened/closed the file on every write and could freeze game input while the emulator UI stayed live. Current `emuffb.dll` rate-limits stream lines, keeps the file open, and rotates at 4 MB - still use Debug only for short diagnostic captures, then **Stop debug**. Emulator CPU/RAM is sampled every 10 seconds into `%TEMP%\steeringwheel-emulator-perf.log` (and once in `summary.txt` at export), including the OEM game process when `emuffb.dll` is loaded. That file is not written from the game. High game CPU/GPU is expected; the `hint=` line is about **emulator** load. GPU is not sampled.

**FFB debug** (the expander) is separate: live counters and test pulses with no file I/O on the game thread. **Settings → FFB Debug Overlay** shows the same live G920 inputs and FFB diagnostics in a topmost window you can drag over the game.

## Probing which effects a game uses

1. Start bridge with an FFB output device selected.
2. Click **Debug** (status bar) so OEM file logging is on, then launch the game.
3. Optional: expand **FFB debug** to watch live OEM effects seen / playing.
4. Reproduce briefly, then **Stop debug** and **Export log…** (preferred for support zips). Do not leave Debug on for a full race. Starting **Debug** again clears the previous `%TEMP%` OEM / HID++ log files.

While a Debug session is active (and until the next Start clears them), you can also open:

```
%TEMP%\emuffb-effects.log
```

Each `DownloadEffect` logs type, handle, flags, and a type-specific “extra” (CF magnitude, spring offset, damper coeff, periodic magnitude). Parameter-only streaming updates are rate-limited per effect (see **Reading the OEM log** below). Spring downloads also emit `SPRING_DETAIL` lines.

When a game opens the virtual wheel's FFB, the log gets a `SESSION emuffb loaded pid=… exe=…` line. If no SESSION line appears for a game, it did not load the OEM driver, so its forces never reached the emulator. The log rotates to `emuffb-effects.log.old` past 4 MB.

### NFS Unbound

At menu/load Unbound downloads **ConstantForce**, **Sine**, **Damper**, **Spring**, and in-race often **Triangle** (and streams `0x100` every frame). Universal mixing is required.

Unbound can construct **multiple** OEM driver instances; older single-instance mixers published torque=0 while `effects.log` still grew. Current `emuffb.dll` mixes all live instances and uses SHM **v7** (`Torque` + `AuxTorque`) so Steam/idle helpers cannot overwrite the game channel.

For a stronger tire-follow / lighter arcade center on DD bases, raise **Constant** and lower **Spring** in the effect gains, then **Save As…** your own FFB profile. Default runtime path stays **Raw**.

**Arcade auto-center (centering spring)**

Heat / Unbound pull the wheel back to center like an arcade cabinet. In DirectInput that is the **Spring** effect (mixed with ConstantForce, Damper, Sine, Triangle, Square - nothing is muted by default).

1. Select your **physical FFB base** under Force feedback (not DualSense). Spring uses that rim angle.
2. Keep **Spring** (and other) effect gains above 0% unless you intend to mute a type.
3. Do **not** rely on hardware `DIPROP_AUTOCENTER` during gameplay - it is left off so it cannot fight the OEM mix. FFB debug **Center** is a manual software return-to-center test only.
4. OEM log (`%TEMP%\emuffb-effects.log`) should list Spring / Damper / CF / periodic while driving. FFB debug and FFB Debug Overlay show **Rim (spring)** plus the same MIX groups and per-type seen/playing list.

### Reading the OEM log

- `EFFECT` lines are what the game sent. Each effect handle is logged when its value changes (at most every 100 ms), when it goes to or from zero, and as a 1 s heartbeat.
- `CALL` lines record the game's other driver calls: `DeviceID`, `SendForceFeedbackCommand` (RESET / STOPALL / PAUSE / CONTINUE / ACTUATORSON / ACTUATORSOFF), `SetGain` (on change), `StopEffect`, `DestroyEffect`, `Escape`, and `GetForceFeedbackState` (when the reported state changes). The driver reports the safety switch as on, so games see a wheel that can play force feedback.
- `MIX` lines (1 s) show what each type contributed to the output torque: `cf`, `periodic`, `spring`, `damper`, `other`, `total`, with `playing` as a DI type bitmask.
- **Target device is always the virtual G920.** HidHide must hide every other FFB joystick from the game (physical base, vJoy, pads). The emulator stays whitelisted so it can still read those devices for binding and for applying torque to the base you pick.
- Shared memory `Local\G920Emulator.FfbTorque.v7`: the **game** publishes `Torque`; Steam/overlay may only layer `AuxTorque` (logged `SESSION HOST` / `AUX PUBLISH`). The bridge applies the sum.
- **NFS Unbound:** Accessibility → Controls → **Controller Vibration = On**. Confirmed root cause when Vibration is Off: race still creates Triangle/CF/Damper, but all streamed magnitudes stay 0 (spring-only `MIX`). In-race with Vibration On: `GetEffectStatus` → `DestroyEffect` (boot Sine) → Triangle → non-zero `cf` / `periodic` / damper on `MIX`.
- **vJoy may stay installed** (Joystick Gremlin / remappers). Keep feeder apps on the HidHide whitelist. The vJoy device itself must be **hidden from the game** when it advertises FFB, or Unbound can send forces there instead of the virtual G920. Seeing vJoy in the emulator device list does **not** mean the game sees it (this app is whitelisted). Auto-apply only hides devices on HidHide’s **Gaming devices only** list (`--dev-gaming`); if vJoy is missing there, hide it manually in HidHide Client.
- If `MIX` only shows `spring` and there are no non-zero CF/periodic samples while driving, the **game** is not streaming those effects to the virtual G920. Confirm HidHide hides the physical FFB base and any competing FFB devices from the game; keep in-game FFB on. **SimHub can stay running** - mixed rigs (Simucube base + Fanatec shifter + SimHub) are supported.

## Launch soft-start

Games often **download** effects at full magnitude (Sine/Square rumble, CF) during boot.

1. **Driver:** **Constant Force**, **Ramp** and **Periodics** (Sine/Triangle/…) do **not** arm on the initial full create (`0x3FF`). Unbound creates CF at ~5000 and Sine at 10000 as placeholders; arming them would hold a constant pull or full rumble. They arm on `DIEP_START`, `StartEffect`, or a later parameter-only stream (`0x100`) with a non-zero magnitude. Conditions (Spring/Damper) arm on create so arcade auto-center works.
2. **Emulator feel (optional):** **Advanced Settings → Boot ease-in**, plus Output feel (Smoothing / Peak soft) and torque shaping, default to **off** on **Raw**. Turn them on and Save / Save As an FFB profile if a direct-drive base feels too raw or boot hits are harsh.

## Latency notes

Input path targets ~500 Hz with adaptive pacing; physical DI torque apply runs on a **side thread** so a slow base `SetParameters` (common on Simucube) cannot stall virtual G920 axis submits. Extra latency only appears if you raise **Smoothing** or heavy **Slew rate**. Physical USB/driver latency usually dominates feel in-race.

## Scope / non-goals

- Works for games that drive a **G920 via DirectInput OEM effects**.
- Pure XInput rumble-only titles, proprietary SDKs, or FFB that only exists inside the Logitech HID++ filter path are out of scope for this OEM bridge.
