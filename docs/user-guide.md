# User guide

## Requirements

- Windows 10/11
- [WinUHid](driver-install.md) (bundled installer in the app)
- [HidHide](https://github.com/nefarius/HidHide) (**required**)
- [Logitech Steering Wheel SDK + OEM FFB](driver-install.md#logitech-steering-wheel-sdk--oem-ffb-session-scoped) (bundled; pinned only while the bridge runs for NFS Heat / Unbound-style games, then restored)
- A physical DirectInput controller / wheel / pedals / shifter to bind
- Optional: DirectInput FFB wheel base for force feedback

## Launch

1. Download **`G920Emulator-win-x64.zip`** from [Releases](https://github.com/camborambo/G920Emulator/releases).
2. Extract → open the **`G920Emulator`** folder → run **`G920Emulator.exe`**. Only one instance can run; launching again restores the existing window (including from the system tray).

(From a source checkout: `.\publish.ps1`, then `dist\G920Emulator\G920Emulator.exe`.)

Approve UAC when prompted (WinUHid requires elevation to open the device).

## First-time setup

1. Open **Dependencies** (header **Settings** → **Manage dependencies**, or the **Fix** banner if something is missing).
2. **Install WinUHid** → Recheck until installed/ready.
3. Install HidHide if missing → configure it yourself in **HidHide Client** (or optional **Configure HidHide** in Dependencies). The app does **not** change HidHide when you Start bridge.
4. Create a profile name → **Save** (stored in `%AppData%\G920Emulator\profiles`, so updates do not wipe binds).

Details and troubleshooting: [driver-install.md](driver-install.md).

## Tabs

- **Input** — detected devices, G920 bindings, shifter mode, and live virtual G920 preview (including buttons).
- **Force Feedback** — FFB profile, output device, gains, and feel controls (full workspace).

Shared chrome: Input / Force Feedback tabs, input profile, **Start** (toggles to **Stop** while running), and **Settings**. Settings (including **Minimize to system tray**, **Debug Overlay**, **Effect Changes Overlay**, and **Check for GitHub updates**) save immediately in `settings.json`. Live meters sit on the Input tab. A warning strip appears if required pieces are missing. With update checks on, a banner appears when GitHub has a newer published release. **Update** saves the zip to Downloads (then opens that folder); unzip it over your G920 Emulator folder like a first install. **Later** skips that version.

## Detected devices

On the **Input** tab:

- **Refresh** re-enumerates DirectInput devices and restores any you hid.
- Each device has an **×** to hide it from this list (local UI preference).

**Start** is on the right, left of Profile, with a divider between them. It uses a play icon; it becomes **Stop** (square icon, red) while running.

## Bindings

On the **Input** tab, click a G920 control in **G920 bindings** to **Assign** (capture dialog).

| Target kind | Capture |
|-------------|---------|
| Axis (Steering, Throttle, Brake, Clutch) | Move an axis → shown as `(axis)` |
| Button | Press a button, or deflect an axis → `(axis→btn)` / `(axis→btn·center)` with threshold |
| Directional pad (hat) | Move a POV hat / D-pad |
| D-pad Up / Down / Left / Right | Press a button (or axis→button) — for pads with no POV hat; diagonals work when two directions are held |

Tips:

- **Invert** and **deadzone** / **threshold** are per binding.
- **Clear all** wipes every binding in the current profile.
- Axis→button is for mapping pedals/triggers onto digital G920 buttons.
- Binding changes apply **live** — you do **not** need to Stop and Start the bridge after rebinding. The running bridge remaps every frame from the current profile.

### H-pattern gears

Assign **Gear R** and **Gear 1–6**. Gears 1–6 always map to G920 buttons **13–18** (official Driving Force Shifter).

**Gear R** output button is selectable in the Assign Gear R dialog (saved on the profile):

| Setting | G920 button | Use when |
|---------|-------------|----------|
| **19** (default) | Official G920 / LGS reverse | **NFS Heat** and most titles |
| **12** | Unbound native reverse | **NFS Unbound** only (required for reverse to work there) |

If reverse works in Heat but not Unbound (or the reverse), change this setting — gears 1–6 stay on 13–18 either way.

Gears are always exclusive H-pattern: only one gear bit is on at a time. If more than one gear is pressed, the first match (R→1→6) is kept.

## Force feedback

On the **Force Feedback** tab:

1. Select **FFB output device** (your physical base — not DualSense).
2. Pick an **FFB profile** in the dropdown (default **Raw** = exact game mix, the only built-in). Use **Save As…** to make your own per-game presets.
3. Adjust sliders as needed, then use the FFB profile icons (Save / Save As / Reset to Raw defaults / Delete):
   - **Master** + **Invert FFB** — **Bind** on Master assigns hardware buttons that step overall gain while you drive (1% per tap, 5% if you hold; saved on the input profile). Green fill means a − or + is already assigned; open it to **Clear**.
   - **Effect gains** — Constant, Spring, Damper, Friction, Inertia, Periodic, Ramp, Custom (0% mutes that DI type). **Bind** on every FFB slider (gains, feel, shaping, centering, advanced mix) assigns hardware buttons that step that slider while you drive (small tap / faster hold; saved on the input profile, not sent to the virtual G920). With **Settings → Effect Changes Overlay** on, those binds flash the category (e.g. Effect gains), slider name, and value at the top of the screen for a moment.
   - **Output feel** — Smoothing (ms), Peak soft, Soft start (all off on Raw)
   - **Advanced mix** — Invert Constant Force, damper velocity / deadband scales (all off on Raw)
   - **Torque shaping** — Deadband, Slew, Spike cap, DI epsilon (all off on Raw; optional ShapeGameTorque path)
   - **Centering** — **Force center spring** checkbox plus Strength / Range / Deadzone, for games that never center the wheel (off on Raw)
   - Hover any FFB row (label, slider or value) for a tooltip explaining what it does and what 0% / off means.
4. **Start bridge** attaches FFB automatically.
5. Optional: expand **FFB debug** for test pulses and live OEM counters, or turn on **Settings → Debug Overlay** for a topmost window with live G920 inputs plus the same FFB diagnostics while you are in-game. Use status-bar **Debug** only for short diagnostic captures (see [Getting help](#getting-help--diagnostics)) — leave it off for normal play.

Slider ranges and probing tips: [force-feedback.md](force-feedback.md).

## Start bridge

1. Confirm WinUHid + HidHide ready.
2. Bindings update the live meters on the right.
3. Click **Start** (the button becomes **Stop** while the bridge is running).
4. Launch the game and select the Logitech G920 / wheel device.

You can change bindings (and tweak deadzone/invert) while the bridge is running; they take effect immediately. Restart the bridge only when you change something that attaches at Start (for example the **FFB output device**) or after driver/dependency changes.

If **joy.cpl still lists the G920 but buttons stop updating** in-game, the virtual node can be orphaned or a physical pad may have changed instance id. The bridge re-enumerates input every ~1.5s, remaps by product id, and recreates the WinUHid device only after about 1 s of hard submit failures (recreating it drops the game's force feedback effects). `ERROR_NOT_READY` from WinUHid just means Windows hasn't asked for the next report yet; it is normal and not counted as a failure. Prefer **Stop → Start** if the status bar shows a recover failure.

If the **app itself freezes** while alt-tabbing or dragging FFB sliders, use a build that keeps DirectInput work off the UI thread (device refresh, FFB reattach, and FFB debug no longer poll the exclusive wheel on the UI). Stop → Start recovers a stuck session.

If the **game** freezes or loses the wheel mid-session while the emulator UI stays live, check whether status-bar **Debug** was left on — Forza-class titles stream OEM updates every frame, and older builds could stall the game on log I/O. Leave Debug off for racing; use it only for a short capture. Current builds rate-limit that log, but everyday play should still keep Debug off.

Stop ends the virtual device and FFB apply loop. If Stop is slow (WinUHid teardown), wait a few seconds before Start again — Start waits for the previous virtual G920 to finish tearing down.

## Profiles

Input bindings and force-feedback presets are stored **separately**, both outside the install folder so updates never overwrite them:

```
%AppData%\G920Emulator\profiles\Default.json        (input bindings — Save / Save As)
%AppData%\G920Emulator\ffb-profiles\Raw.json        (exact game mix — cannot delete)
%AppData%\G920Emulator\settings.json
```

There is **no** `profiles\` or `ffb-profiles\` directory next to `G920Emulator.exe` in the zip. On first run the app creates AppData, seeds an empty **Default** input profile, and seeds the built-in **Raw** FFB profile.

**Migration (once, never overwrites AppData):** copies **input** `profiles\*.json` and `settings.json` from next-to-exe leftovers and from `%AppData%\N4Sunbound`. Next-to-exe `ffb-profiles\` are **not** migrated — re-Save FFB presets, or rely on legacy inline FFB fields on an input JSON becoming a named FFB profile when loaded.

Each input profile links to an FFB profile name. Saving an input profile also saves the linked FFB preset’s current master / effect gains / feel / torque shaping.

Bindings store both the DirectInput instance GUID and a stable **product** GUID. If Windows reassigns the instance id after a replug, Refresh devices remaps the profile to the same hardware when possible.

For USB-stick installs, create an empty `portable.txt` beside the exe to keep `profiles\`, `ffb-profiles\`, and `settings.json` next to the app instead (created at runtime; not shipped).

| Action | Behavior |
|--------|----------|
| Save (input) | Write bindings under AppData `profiles\` and quiet-save the linked FFB profile |
| Save As… (input) | New input profile name in AppData `profiles\` (also quiet-saves linked FFB) |
| Delete (input) | Remove that input profile JSON |
| FFB Save / Save As… | Write master / effect gains / feel / torque shaping under AppData `ffb-profiles\` (Raw cannot be deleted) |
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

Forza Horizon 6 refuses to launch while Windows **test signing** is on. It exits at the splash with Steam code **100** — likely an integrity / anti-cheat check. OEM/SDK session pins are not the cause.

**Install WinUHid** only needs test signing for the install step, then turns it off again — so FH6 and the emulator can coexist. Secure Boot can be re-enabled in UEFI/BIOS after install (with test signing off). If test signing is still on: **Dependencies → Disable test signing** → reboot (keep WinUHid installed). Details: [driver-install.md](driver-install.md#forza-horizon-6-and-test-signing).

## Getting help / diagnostics

Leave status-bar **Debug** **off** during normal play. It turns on OEM / HID++ file logging inside the game process; on Forza and similar titles that can add enough I/O to freeze the game or drop the virtual G920 while the emulator UI stays responsive. Current builds rate-limit and keep the log file open, but Debug is still for short captures only — not full races.

1. Click **Debug** (status bar, bottom-right) — clears prior session logs in `%TEMP%` and starts OEM / HID++ file logging.
2. Reproduce briefly (Start bridge, launch the game, hit a wall, etc.). Prefer a short run over a long session with Debug left on.
3. Click **Stop debug**, then **Export log…**, save the zip, and attach it to a [GitHub issue](https://github.com/camborambo/G920Emulator/issues) with a short description (wheel, game, what failed). Export before starting Debug again, or those logs are wiped.

The zip includes:

- `HOW-TO-SEND.txt` — how the capture was meant to be taken
- `summary.txt` — machine name, deps, running wheel/SimHub/Steam processes, `g920ffb.dll` stamp, live bridge/FFB attach, emulator CPU/RAM snapshot
- `devices.txt` — every DirectInput game device (including virtual G920 and FFB flag)
- `ffb-snapshot.txt` — OEM shared-memory mix + active gains at export time
- `hidhide.txt` — cloak / app whitelist / hidden devices via HidHideCLI
- `oem-registry.txt` — G920 OEMForceFeedback CLSID path
- `game-ffb-analysis.txt` — Unbound race signature / Vibration hint from the OEM log
- `logs\g920ffb-effects.log` — game OEM calls (`SESSION` / `CALL` / `EFFECT` / `MIX`) when Debug was used
- `logs\g920emulator-perf.log` — 10 s snapshots of emulator CPU/RAM plus the OEM game (and Steam aux) process; high game load is expected, watch emulator `cpu1` / `hint=`
- Copies of AppData (or portable) `profiles\`, `ffb-profiles\`, and `settings.json`

**Fanatec DD2** (Heat / Unbound) and **Simucube** (Unbound) are validated FFB targets. For comparisons: **Debug** → race briefly with wall hits → **Stop debug** → **Export log…** on each PC with the same build.

## Validation checklist

- [ ] Devices appear after Refresh
- [ ] Live meters respond to bindings
- [ ] Virtual G920 appears in `joy.cpl` while bridge is running
- [ ] Game sees G920 (and not double input from the physical pad)
- [ ] FFB moves the physical base when the game applies force

