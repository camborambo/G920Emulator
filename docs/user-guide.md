# User guide

## Requirements

- Windows 10/11
- [WinUHid](driver-install.md) (bundled installer in the app)
- [HidHide](https://github.com/nefarius/HidHide) (**required**)
- A physical DirectInput controller / wheel / pedals / shifter to bind
- Optional: DirectInput FFB wheel base for force feedback

## Launch

- Double-click **`Launch G920 Emulator.bat`**, or
- Run `dist\G920Emulator\G920Emulator.exe` after `.\publish.ps1`

Approve UAC when prompted (WinUHid requires elevation to open the device).

## First-time setup

1. Open **Dependencies** (banner **Manage dependencies…** or **Fix dependencies…**).
2. **Install WinUHid** → Recheck until installed/ready.
3. Install HidHide if missing → **Configure HidHide** (whitelists this app, cloaks, hides pads/wheels; keeps virtual G920 visible).
4. Create a profile name → **Save** (stored under `%AppData%\G920Emulator\profiles`).

Details and troubleshooting: [driver-install.md](driver-install.md).

## Detected devices

- **Refresh** re-enumerates DirectInput devices and restores any you hid with Remove.
- **Remove** only hides a device from this list (local UI preference).

Top-right toolbar is **Start bridge** / **Stop** only.

## Bindings

Click a G920 control in **G920 bindings** to open the capture dialog.

| Target kind | Capture |
|-------------|---------|
| Axis (Steering, Throttle, Brake, Clutch) | Move an axis → shown as `(axis)` |
| Button | Press a button, or deflect an axis → `(axis→btn)` / `(axis→btn·center)` with threshold |
| Hat | Move a POV hat |

Tips:

- **Invert** and **deadzone** / **threshold** are per binding.
- **Clear all** wipes every binding in the current profile.
- Axis→button is for mapping pedals/triggers onto digital G920 buttons.
- Binding changes apply **live** — you do **not** need to Stop and Start the bridge after rebinding. The running bridge remaps every frame from the current profile.

### H-pattern gears

Bind **Gear R** and **Gear 1–6**. Gears 1–6 always map to G920 buttons **13–18** (official Driving Force Shifter).

**Gear R** output button is selectable in the Bind Gear R dialog (saved on the profile):

| Setting | G920 button | Use when |
|---------|-------------|----------|
| **19** (default) | Official G920 / LGS reverse | **NFS Heat** and most titles |
| **12** | Unbound native reverse | **NFS Unbound** only (required for reverse to work there) |

If reverse works in Heat but not Unbound (or the reverse), change this setting — gears 1–6 stay on 13–18 either way.

**Shifter mode** (Force feedback panel):

- **Exclusive H-pattern** — only one gear bit at a time
- **Passthrough** — forwards overlapping sources as mapped

## Force feedback

1. Select **FFB output device** (your physical base).
2. Adjust **Gain** / **Invert FFB** if needed.
3. **Start bridge** attaches FFB automatically.
4. Optional: enable **FFB debug** for test pulses and OEM effect diagnostics.

See [force-feedback.md](force-feedback.md).

## Start bridge

1. Confirm WinUHid + HidHide ready.
2. Bindings update the live meters on the right.
3. Click **Start bridge**.
4. Launch the game and select the Logitech G920 / wheel device.

You can change bindings (and tweak deadzone/invert) while the bridge is running; they take effect immediately. Restart the bridge only when you change something that attaches at Start (for example the **FFB output device**) or after driver/dependency changes.

Stop ends the virtual device and FFB apply loop.

## Profiles

| Action | Behavior |
|--------|----------|
| Save | Write current profile under AppData |
| Save As… | New name |
| Delete | Remove saved profile |
| Export / Import | JSON file exchange |
| Saved dropdown | Switch among saved profiles |

## Validation checklist

- [ ] Devices appear after Refresh
- [ ] Live meters respond to bindings
- [ ] Virtual G920 appears in `joy.cpl` while bridge is running
- [ ] Game sees G920 (and not double input from the physical pad)
- [ ] FFB moves the physical base when the game applies force
