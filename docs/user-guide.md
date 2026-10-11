# User guide

## Requirements

- Windows 10/11
- [WinUHid](driver-install.md) (bundled installer in the app)
- [HidHide](https://github.com/nefarius/HidHide) (**required**)
- [OEM FFB (`emuffb.dll`)](driver-install.md#logitech-steering-wheel-sdk--oem-ffb-session-scoped) (bundled; session-scoped). **G920** also pins the Logitech Steering Wheel SDK for Heat / Unbound-style titles; **Fanatec DD1** uses Fanatec OEM + `emuffb` only (no Logitech SDK pin, no Fanatec SDK)
- A physical DirectInput controller / wheel / pedals / shifter to bind
- Optional: DirectInput FFB wheel base for force feedback

## Launch

1. Download **`SteeringWheelEmulator-win-x64.zip`** from [Releases](https://github.com/camborambo/SteeringWheelEmulator/releases).
2. Extract → open the **`G920Emulator`** folder → run **`SteeringWheelEmulator.exe`**. Only one instance can run; launching again restores the existing window (including from the system tray).

(From a source checkout: `.\publish.ps1`, then `dist\SteeringWheelEmulator\SteeringWheelEmulator.exe`.)

Approve UAC when prompted (WinUHid requires elevation to open the device).

## Updating

The window title and status bar show the app version (for example **v0.2.7**). With **Settings → Check for GitHub updates** on (default), a banner appears when a newer **published** GitHub release exists.

1. Click **Update** - the zip downloads to **Downloads** and that folder opens.
2. Close Steering Wheel Emulator if it is running.
3. Unzip the new `G920Emulator` folder **over** the folder you already use (same place you put it the first time).
4. Run the new `SteeringWheelEmulator.exe`. Bindings stay in `%AppData%\SteeringWheelEmulator\` unless you use `portable.txt`.

If Windows says the install folder is **in use**, Steam often still has an old `emuffb.dll` loaded from that folder. OEM FFB is registered under `%ProgramData%\SteeringWheelEmulator\emuffb\` — **fully quit Steam once**, then delete or replace the folder. (Closing only the emulator is not enough if Steam already loaded the old path.)

**Later** hides that version until a newer tag. You can turn the check off in Settings. The app does not overwrite files itself.

## First-time setup

1. Open **Dependencies** (header **Settings** → **Manage dependencies**, or the **Fix** banner if something is missing).
2. **Install WinUHid** → Recheck until installed/ready.
3. Install HidHide if missing → configure it yourself in **HidHide Client** (or optional **Configure HidHide** in Dependencies). Or open **Settings → HidHide** and pick an apply mode:
   - **Off** (default) - **Start** never changes HidHide from Settings. If the active input profile has a saved **HidHide snapshot**, Start still restores that list.
   - **Hide all (except emulator)** - each **Start** whitelists the emulator and hides devices from HidHide's **Gaming devices only** list (virtual wheel stays visible).
   - **Hide bound devices only** - same whitelist/cloak, but each **Start** hides only devices used in your current bindings (and FFB source) and **unhides** other gaming devices left hidden from a previous profile (e.g. pedals after switching from a full-rig bind to wheel-only). Unbound pads stay visible for in-game binding.
   - **Save & restore on Stop** - with a mode other than Off, **Start** can save your current HidHide setup and put it back on **Stop**/**Exit** (status shows **Restoring HidHide…**; if it times out, the next launch finishes it).
   Close **HidHide Client** before Start (or let the app close it when prompted) - while that window is open the driver returns Access denied / 0x0005. Windows may also ask for admin permission.
4. Pick **Emulated device** (toolbar): **Logitech G920** or **Fanatec DD1** — only while Stopped. Create a profile name → **Save** (stored in `%AppData%\SteeringWheelEmulator\profiles`, so updates do not wipe binds). Save also captures a HidHide snapshot into the profile when possible; switching profiles restores it.

Details and troubleshooting: [driver-install.md](driver-install.md).

## Tabs

- **Input** - detected devices, bindings for the selected emulated identity, shifter mode, and live virtual-wheel preview (including buttons).
- **Telemetry** - SimHub UDP for games with no native telemetry (simulated speed/RPM plus FFB-derived rumble).

Shared chrome: Input / Force Feedback / Telemetry tabs, **Emulated device** combo, input profile, **Start** (toggles to **Stop** while running), and the gear **Settings** modal (tabs **General**, **Overlays**, **HidHide**). Settings save immediately in `settings.json` (minimize to tray, update checks, telemetry km/h, overlays, HidHide apply mode + save & restore). The tray icon menu is slim: **Restore**, **Settings…**, **Exit**. Telemetry **simulation** presets are separate profiles under `%AppData%\SteeringWheelEmulator\telemetry-profiles` (same Save / Save As / Default / Delete pattern as FFB profiles); host/port/rate stay in settings. Live meters sit on the Input tab. A warning strip appears if required pieces are missing. With update checks on, a banner appears when GitHub has a newer published release. **Update** saves the zip to Downloads (then opens that folder); unzip it over your Steering Wheel Emulator folder like a first install. **Later** skips that version.

## Emulated device

Toolbar combo (change only while the bridge is **Stopped**):

| Identity | VID:PID | When to use |
|----------|---------|-------------|
| **Logitech G920** (default) | `046D:C262` | Titles with a G920 / Logitech wheel profile (Heat, Unbound, Forza on G920 path, …) |
| **Fanatec DD1** | `0EB7:0004` | Titles / mixed-brand setups that expect Fanatec PC Compatibility mode |

Your **physical** Fanatec (or other) base stays in Detected devices for bind and FFB output — it is not removed because it shares a Fanatec VID. PC Comp uses shared **`emuffb.dll`** for game FFB (no Fanatec SDK, no Logitech SDK pin). Gear R defaults to button **12** on PC Comp profiles.

## Telemetry (SimHub)

Many arcade / console-port titles (Need for Speed Heat and Unbound included) have **no telemetry API**. This tab does not read the game process. It sends a UDP packet SimHub 9.11.5+ can consume as an **External Sim** named **Steering Wheel Emulator (simulated)**.

**Honest fields**

- Throttle, brake, clutch, steering - your mapped G920 controls
- Gear - H-pattern binds, bumper paddles, or (opt-in) Arcade **Sequential shifter** Up / Down / Reset binds
- `SurfaceRumble`, `Impact`, `RoadLoad`, and per-type FFB (`FfbConstant`, `FfbPeriodic`, …) - from the game's DirectInput mix on the virtual G920

**Simulated (not real game values)**

- Speed (MPH by default, or km/h) and RPM - arcade integration from pedals, gated when FFB is idle and no known game process is running
- G-force - SimHub standard `LocalSurgeMs2` / `LocalSwayMs2` / `LocalHeaveMs2` (m/s²). Vehicle-frame: surge **+ = throttle**, **− = brake**; sway **+ = left**. Live G-G circle uses the same signs (accel up, brake down).

**Setup**

1. SimHub **9.11.5 or newer**. Settings → Global → enable game definition authoring if the sim does not appear.
2. On the **Telemetry** tab, click **Register with SimHub** (writes `%LocalAppData%\SimHub\ExternalSims\Registrations\{id}.simlink` pointing at `simhub\G920Telemetry.simdef` next to `SteeringWheelEmulator.exe`, and installs **`SteeringWheelEmulator.SimHubPlugin.dll`** into the SimHub folder so built-in ShakeIt **Engine vibrations** gets the same RPM capability native games like Forza use). **Remove registration** deletes the link and disables/removes that plugin; restart SimHub so the tile and icon drop, then Register again after changing `simhub/logo.png`.
3. Restart SimHub, then activate **Steering Wheel Emulator (simulated)**. Match UDP **port** (default **20778**) and host **127.0.0.1**. Do not pick this sim for titles that already have a native SimHub plugin (Forza, and so on). Need for Speed Heat / Unbound are listed as detection processes only so SimHub can switch to this definition if those EXEs are running - they still have no real telemetry. Confirm **Steering Wheel Emulator RPM** is enabled under SimHub → Settings → Plugins.
4. Enable **Send telemetry while the bridge is running**, **Start** the bridge, launch the game.
5. Optional **Telemetry profile** tuning (Save / Save as / Default / Delete under `telemetry-profiles`):

   - **Gearing** - **Max gears** (1–10; UDP `MaxGears` → SimHub `CarSettings_MaxGears`; shows that many ratio rows), gear ratios, Diff / final drive, Tire diameter, and Redline (SimHub `EngineShiftRpm`).
   - **Live / Accel / Brake / Coast / Downshift settle** - MPH (and MPH/s) by default; enable **Settings → Telemetry km/h** for metric. Values are stored and sent to SimHub as **km/h** (rates as km/h/s). **Downshift settle** is how fast speed bleeds to a shorter gear’s max after a downshift (not the same as Coast or Brake).
   - **RPM** - follows speed within the current gear (upshift drops RPM). **Rev-limiter** / **Limiter rate** hard-cut when a gear is pinned at top speed.
   - **Arcade buttons** - optional **Handbrake** and **NOS / Turbo** binds on the input profile, with strength / boost on the telemetry profile. Enable **Sequential shifter** when you use a sequential box or paddles that are not H-pattern gears: bind **Gear up**, **Gear down**, and **Gear reset** (R → 1 → Max gears; Reset → 1).
   - **Engine / ShakeIt scales** - Engine (0-200%) for SimHub Engine vibrations force; SurfaceRumble / Impact / RoadLoad as before.

Unbound still needs **Controller Vibration On** or periodic/CF magnitudes stay 0 (same as FFB).

**ShakeIt / property picker (with Steering Wheel Emulator selected)**

| Effect | Use these properties |
|--------|----------------------|
| Speed | `SpeedKmh` or `SpeedMph` (we always send **km/h**; SimHub derives MPH) |
| RPM | `Rpms` / `MaxRpm` / `CarSettings_CurrentGearRedLineRPM` (from `EngineRpm` / `EngineMaxRpm` / `EngineShiftRpm`) |
| Engine vibrations (built-in ShakeIt) | Simulated **`Rpms`** / `MaxRpm` / `EngineStarted` are always sent. After **Register with SimHub**, the **Steering Wheel Emulator RPM** plugin enables the effect (Forza-style). Use the **Engine** scale under ShakeIt / FFB scales for force (0-200%); enable/curves stay in SimHub. Restart SimHub once after installing the plugin. |
| G-force | `AccelerationSurge` / `AccelerationSway` / `AccelerationHeave` (from Local*Ms2) |
| Road vibration / kerbs (built-in ShakeIt) | Uses standard **suspension velocity** + **tyre contact surface** (enabled when FFB rumble/impact is present) |
| Custom rumble / impact / load | Game raw data **`SurfaceRumble`**, **`Impact`**, **`RoadLoad`** (0..1) |

Watch the Telemetry tab Live meters - if rumble/impact/road load stay at 0%, the game is not sending those FFB types (vibration off / Steam Input / OEM silent). Speed and RPM should move with pedals whenever a session is active. After changing the definition, **Remove registration** → restart SimHub → **Register with SimHub** again (packet layout/signature changed).

## Detected devices

On the **Input** tab:

- **Refresh** re-enumerates DirectInput devices and restores any you hid.
- Each device has an **×** to hide it from this list (local UI preference).

**Start** is on the right, left of Profile, with a divider between them. It uses a play icon; it becomes **Stop** (square icon, red) while running.

## Bindings

On the **Input** tab, click a control in the bindings list to **Assign** (capture dialog). Labels follow the emulated identity (G920 or Fanatec PC Comp catalog).

| Target kind | Capture |
|-------------|---------|
| Axis (Steering, Throttle, Brake, Clutch) | Move an axis → shown as `(axis)` |
| Button | Press a button, or move an axis → `(axis→btn)` with **Activate on Axis** % (auto digital press) |
| Directional pad (hat) | Move a POV hat / D-pad |
| D-pad Up / Down / Left / Right | Press a button (or axis→button) - for pads with no POV hat; diagonals work when two directions are held |

Tips:

- **Invert** is per binding.
- **Axis range** (start/end) is for axis→axis only — remaps the usable throw.
- **Activate on Axis** is for axis→button only — the button presses when the axis reaches that %.
- **Clear all** wipes every standard and custom binding in the current profile.
- Axis→button is for mapping pedals/triggers onto digital G920 buttons.
- Binding changes apply **live** - you do **not** need to Stop and Start the bridge after rebinding. The running bridge remaps every frame from the current profile.

### Custom bindings (+ FN)

After **Gear 6**, use **Add Custom Binding**:

- Name the mapping and pick a **G920 target**.
- **Toggle** checkbox (optional modifier): on = press latches on/off; off = hold-to-press.
- **Bind Button** opens a listen popup for the input (required).
- **Bind FN** is optional; if set, the button only fires while that FN key is held.
- Axes become digital presses automatically; set **Activate on Axis** in the bind popup.

Custom rows appear under the standard layout. Click a row to edit; **X** removes it.

### H-pattern gears

Assign **Gear R** and **Gear 1-6**. Gears 1-6 always map to G920 buttons **13-18** (official Driving Force Shifter).

**Gear R** output button is selectable in the Assign Gear R dialog (saved on the profile):

| Setting | Reverse button | Use when |
|---------|----------------|----------|
| **19** (G920 default) | Official G920 / LGS reverse | **NFS Heat** and most G920 titles |
| **12** (Unbound / PC Comp default) | Unbound / Fanatec-style reverse | **NFS Unbound**; Fanatec DD1 profiles default here |

If reverse works in Heat but not Unbound (or the reverse), change this setting - gears 1-6 stay on 13-18 either way for the G920 identity.

Gears are always exclusive H-pattern: only one gear bit is on at a time. If more than one gear is pressed, the first match (R→1→6) is kept.

## Force feedback

On the **Force Feedback** tab:

1. Select **FFB output device** (your physical base - not DualSense).
2. Pick an **FFB profile** in the dropdown (default **Raw** = exact game mix, the only built-in). Use **Save As…** to make your own per-game presets.
3. Adjust sliders as needed, then use the FFB profile icons (Save / Save As / Default = Raw / Delete). **Raw** is always exact game mix and cannot be overwritten — use **Save As…** for your own presets:
   - **Master** - **Bind** assigns hardware buttons that step overall gain while you drive (1% per tap, 5% if you hold; saved on the input profile). **Set as Default** in the bind dialog picks a snap value and a button that jumps Master back to it. Green fill means −, +, or default is already assigned; open it to **Clear**. **Invert FFB**, **Soft steering catch-up**, and **Boot ease-in** are under **Advanced Settings**.
   - **Effect gains** - Constant, Spring, Damper, Friction, Inertia, Periodic, Ramp, Custom (0% mutes that DI type). **Bind** on every FFB slider (gains, feel, shaping, centering, Advanced Settings) assigns hardware − / + (small tap / faster hold) and optional **Set as Default** (slider + value + button) while you drive; saved on the input profile, not sent to the virtual G920. With **Settings → Effect Changes Overlay** on, those binds flash the category (e.g. Effect gains), slider name, and value at the top of the screen for a moment.
   - **Output feel** - Smoothing, **Device pace**, **Interpolate**, **Gap fill**, Peak soft (all off on Raw). Device pace / Interpolate / Gap fill help when a game’s G920 mix is sparse (e.g. Forza grain on a DD); see [force-feedback.md](force-feedback.md#how-games-author-ffb-forza-vs-nfs-g920-vs-dd).
   - **Advanced Settings** - Boot ease-in, Invert FFB, Soft steering catch-up, Invert Constant Force, Damper velocity / Damper deadzone, Spring coeff / Friction coeff (pass-through on Raw)
   - **Torque shaping** - Force deadzone, Slew rate, Spike cap, DI chatter (all off on Raw; optional ShapeGameTorque path)
   - **Centering** - **Force center spring** checkbox plus Strength / Range / Deadzone, for games that never center the wheel (off on Raw)
   - Hover any FFB row (label, slider or value) for a tooltip explaining what it does and what 0% / off means. **Bind** is available on Device pace, Interpolate, and Gap fill as well as the other sliders.
4. **Start bridge** attaches FFB automatically.
5. Optional: expand **FFB debug** for test pulses and live OEM counters, or turn on **Settings → FFB Debug Overlay** for a topmost window with live G920 inputs plus the same FFB diagnostics while you are in-game. Use **Settings → Telemetry Debug Overlay** for the live SimHub UDP packet. Use status-bar **Debug** only for short diagnostic captures (see [Getting help](#getting-help--diagnostics)) - leave it off for normal play.

Slider ranges and probing tips: [force-feedback.md](force-feedback.md).

## Start bridge

1. Confirm WinUHid + HidHide ready.
2. Bindings update the live meters on the right.
3. Click **Start** (the button becomes **Stop** while the bridge is running).
4. Launch the game and select the virtual wheel (G920 or Fanatec PC Comp, matching **Emulated device**).

You can change bindings (and tweak Invert, **Axis range**, or **Activate on Axis**) while the bridge is running; they take effect immediately. Restart the bridge only when you change something that attaches at Start (for example the **FFB output device**) or after driver/dependency changes.

If **joy.cpl still lists the G920 but buttons stop updating** in-game, the virtual node can be orphaned or a physical pad may have changed instance id. The bridge re-enumerates input every ~1.5s, remaps by product id, and recreates the WinUHid device only after about 1 s of hard submit failures (recreating it drops the game's force feedback effects). `ERROR_NOT_READY` from WinUHid just means Windows hasn't asked for the next report yet; it is normal and not counted as a failure. Prefer **Stop → Start** if the status bar shows a recover failure.

If the **app itself freezes** while alt-tabbing or dragging FFB sliders, use a build that keeps DirectInput work off the UI thread (device refresh, FFB reattach, and FFB debug no longer poll the exclusive wheel on the UI). Stop → Start recovers a stuck session.

If the **game** freezes or loses the wheel mid-session while the emulator UI stays live, check whether status-bar **Debug** was left on - Forza-class titles stream OEM updates every frame, and older builds could stall the game on log I/O. Leave Debug off for racing; use it only for a short capture. Current builds rate-limit that log, but everyday play should still keep Debug off.

Stop ends the virtual device and FFB apply loop. If Stop is slow (WinUHid teardown), wait a few seconds before Start again - Start waits for the previous virtual G920 to finish tearing down.

## Profiles

Input bindings and force-feedback presets are stored **separately**, both outside the install folder so updates never overwrite them:

```
%AppData%\SteeringWheelEmulator\profiles\Default.json        (input bindings - Save / Save As)
%AppData%\SteeringWheelEmulator\ffb-profiles\Raw.json        (exact game mix - cannot delete)
%AppData%\SteeringWheelEmulator\settings.json
```

There is **no** `profiles\` or `ffb-profiles\` directory next to `SteeringWheelEmulator.exe` in the zip. On first run the app creates AppData, seeds an empty **Default** input profile, and seeds the built-in **Raw** FFB profile.

**Migration (once, never overwrites AppData):** copies **input** `profiles\*.json` and `settings.json` from next-to-exe leftovers and from `%AppData%\N4Sunbound`. Next-to-exe `ffb-profiles\` are **not** migrated - re-Save FFB presets, or rely on legacy inline FFB fields on an input JSON becoming a named FFB profile when loaded.

Each input profile links to an FFB profile name. Saving an input profile also saves the linked FFB preset’s current master / effect gains / feel / torque shaping.

Bindings store both the DirectInput instance GUID and a stable **product** GUID. If Windows reassigns the instance id after a replug, Refresh devices remaps the profile to the same hardware when possible.

For USB-stick installs, create an empty `portable.txt` beside the exe to keep `profiles\`, `ffb-profiles\`, and `settings.json` next to the app instead (created at runtime; not shipped).

| Action | Behavior |
|--------|----------|
| Save (input) | Write bindings under AppData `profiles\` and quiet-save the linked FFB profile |
| Save As… (input) | New input profile name in AppData `profiles\` (also quiet-saves linked FFB) |
| Delete (input) | Remove that input profile JSON |
| FFB Save / Save As… | Write master / effect gains / feel / torque shaping under AppData `ffb-profiles\` (Raw cannot be deleted or overwritten; Default switches to Raw) |
| Export / Import | JSON file exchange for input profiles (Import also saves a copy under AppData `profiles\`) |
| Saved dropdowns | Switch among input or FFB profiles in their folders |

After **Refresh** devices, the status bar shows the **input** profiles directory (AppData or portable).

## Logitech G HUB

You do **not** need G HUB. Installing or uninstalling it can overwrite the OEM FFB CLSID and leave `logi_joy` filters.

While **Start bridge** is running, the app continuously re-applies OEM registration and the G920 friendly name. It does **not** restart the virtual PnP device (that used to orphan Col01 so games only saw DualSense / a pad layout).

If Heat shows a **controller / D-pad** layout or ghost presses:

1. Confirm the status line says the virtual G920 is active (not a Col01 warning).
2. On Start, the app strips Windows’ `hidgamepad` filter from the **virtual G920 only** (DualSense keeps it).
3. Steam → Heat / Unbound → Properties → Controller → **Disable Steam Input** (Steam can inject pads and also open the virtual G920 for FFB; OEM torque is accepted only from the game process, so Steam Input must be off for walls/rumble).
4. Fully quit Heat, keep the bridge running, launch Heat again.
5. Use **Dependencies → Repair G HUB leftovers** if OEM still points at Logitech.

## Forza Horizon 6 after using the emulator

Forza Horizon 6 refuses to launch while Windows **test signing** is on. It exits at the splash with Steam code **100** - likely an integrity / anti-cheat check. OEM/SDK session pins are not the cause.

**Install WinUHid** only needs test signing for the install step, then turns it off again - so FH6 and the emulator can coexist. Secure Boot can be re-enabled in UEFI/BIOS after install (with test signing off). If test signing is still on: **Dependencies → Disable test signing** → reboot (keep WinUHid installed). Details: [driver-install.md](driver-install.md#forza-horizon-6-and-test-signing).

## Getting help / diagnostics

Leave status-bar **Debug** **off** during normal play. It turns on OEM / HID++ file logging inside the game process; on Forza and similar titles that can add enough I/O to freeze the game or drop the virtual G920 while the emulator UI stays responsive. Current builds rate-limit and keep the log file open, but Debug is still for short captures only - not full races. **Settings → FFB Debug Overlay** and **Telemetry Debug Overlay** are separate (live meters, no extra game-thread log I/O).

1. Click **Debug** (status bar, bottom-right) - clears prior session logs in `%TEMP%` and starts OEM / HID++ file logging.
2. Reproduce briefly (Start bridge, launch the game, hit a wall, etc.). Prefer a short run over a long session with Debug left on.
3. Click **Stop debug**, then **Export log…**, save the zip, and attach it to a [GitHub issue](https://github.com/camborambo/SteeringWheelEmulator/issues) with a short description (wheel, game, what failed). Export before starting Debug again, or those logs are wiped.

The zip includes:

- `HOW-TO-SEND.txt` - how the capture was meant to be taken
- `summary.txt` - machine name, deps, running wheel/SimHub/Steam processes, `emuffb.dll` stamp, live bridge/FFB attach, emulator CPU/RAM snapshot
- `devices.txt` - every DirectInput game device (including virtual G920 and FFB flag)
- `ffb-snapshot.txt` - OEM shared-memory mix + active gains at export time
- `hidhide.txt` - cloak / app whitelist / hidden devices via HidHideCLI
- `oem-registry.txt` - emuffb COM InprocServer32 (ProgramData vs install folder) + OEMForceFeedback CLSID tree
- `game-ffb-analysis.txt` - Unbound race signature / Vibration hint from the OEM log
- `logs\emuffb-effects.log` - game OEM calls (`SESSION` / `CALL` / `EFFECT` / `MIX`) when Debug was used
- `logs\g920emulator-perf.log` - 10 s snapshots of emulator CPU/RAM plus the OEM game (and Steam aux) process; high game load is expected, watch emulator `cpu1` / `hint=`
- Copies of AppData (or portable) `profiles\`, `ffb-profiles\`, and `settings.json`

**Fanatec DD2** (Heat / Unbound) and **Simucube** (Unbound) are validated FFB targets. For comparisons: **Debug** → race briefly with wall hits → **Stop debug** → **Export log…** on each PC with the same build.

## Validation checklist

- [ ] Devices appear after Refresh
- [ ] Live meters respond to bindings
- [ ] Virtual G920 appears in `joy.cpl` while bridge is running
- [ ] Game sees G920 (and not double input from the physical pad)
- [ ] FFB moves the physical base when the game applies force

