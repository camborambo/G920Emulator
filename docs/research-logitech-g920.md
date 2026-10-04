# Research: Logitech G920, G HUB, and FFB

Notes from probing how G HUB exposes a real G920 and how NFS Unbound talks FFB to our virtual device (2026-10).

Raw probe dump also lives under `artifacts/lghub-probe/` (local; not required at runtime).

## How G HUB exposes a real G920

1. Package `g920` depends on `driver_usb`, `driver_hid_di_ffb`, `g920_dfu`.
2. `driver_hid_di_ffb` → `hid_driver_manager.exe` installs **`logi_joy_hid.inf`** into the driver store.
3. That INF binds:
   - `HID\VID_046D&PID_C262&Col01` → upper filters `logi_generic_hid_filter` + **`logi_joy_hid_filter`**
   - Friendly name: **Logitech G HUB G920 Driving Force Racing Wheel USB (HID)**
   - OEM registry via `G920Inst.AddReg` (`OEMData=43 00 08 10 12 00 00 00`, stock FFB CLSID `{62B43F0E-E7DB-4329-8C13-A966D84A289F}`)

## FFB ingress failure (HID++ writes = 0)

When Col01 is owned by **`logi_joy_hid_filter`**, Logitech’s HID++ FFB COM server talks to the filter via private IOCTLs. Those never become VHF `WriteReport` events, so emulator host-write counters stay at **0** even while Unbound “sees” a G920.

**Fix (current):**

- Advertise Hardware ID with **`&REV_9601`** so PnP does **not** match `logi_joy_hid.inf`.
- Do **not** `pnputil /install` `logi_joy_hid` for the virtual device.
- On Start, clean leftover no-REV Col01 nodes from older builds when needed.
- Point OEMForceFeedback at our **`g920ffb.dll`** instead of Logitech’s CLSID.

Logitech’s `hidpp_forcefeedback` may still load but does not deliver usable WriteReports to WinUHid (`HidD_SetOutputReport` → `ERROR_NOT_SUPPORTED`).

**FFB path now:** OEMForceFeedback CLSID → `g920ffb.dll` (`IDirectInputEffectDriver`) → shared memory `Local\G920Emulator.FfbTorque` → bridge → physical base.

| Change | Purpose |
|--------|---------|
| Hardware ID → `HID\VID_046D&PID_C262&REV_9601` | Avoid Logitech filter auto-bind |
| Do not install `logi_joy_hid` on the virtual device | Filter swallows FFB |
| GetFeature / SetFeature / WriteReport hooks | Capture any residual host FFB paths |
| Host writes + OEM effect diagnostics | Distinguish “no traffic” vs “unparsed traffic” |

## Unbound / recognition

- Games that use DirectInput OEM (`OEMName` / `OEMData` / `OEMForceFeedback`) still resolve VID/PID → G920 profile without the Logitech filter.
- G HUB tile / “Logitech G HUB G920 … (HID)” friendly name may be absent (Microsoft HID-compliant name). Expected and preferable for FFB.
- G HUB showing G920 **Inactive** is expected for a WinUHid device — “Active” needs the real USB + HID++ handshake. Not required for DI FFB.

## Gear reverse button (per game)

Official **G920 + Driving Force Shifter** (Logitech LGS docs): gears **1–6 = buttons 13–18**, **Reverse = button 19**.

Games do **not** all use that reverse index in their native G920 profiles:

| Game | Gears 1–6 | Reverse | Emulator setting |
|------|-----------|---------|------------------|
| Most titles (e.g. **NFS Heat**) | 13–18 | **19** | Default — leave Bind Gear R on **19** |
| **NFS Unbound** | 13–18 | **12** | Bind Gear R → select **12 — NFS Unbound** |

G920 Emulator defaults to **19**. For Unbound, open **Gear R** binding and set **G920 reverse button** to **12** (saved on the profile; no bridge restart).

## Effect probe

`g920ffb.dll` publishes shared-memory v3 fields `TypesSeen` / `TypesPlaying` (bitmasks of DI effect type IDs). The emulator **FFB debug** UI shows human names. Full log: `%TEMP%\g920ffb-effects.log`.

### NFS Unbound (2026-10-04)

At menu/load Unbound downloaded:

| Type | Handles | Notes |
|------|---------|--------|
| **ConstantForce** | 1 | mag=5000 at download |
| **Sine** | 2, 6 | rumble; handle 6 mag=10000 |
| **Damper** | 3 | coeff=0 at download |
| **Spring** | 4, 5 | centering; handle 5 coeff=10000 |

Unbound is not spring+rumble only — it also allocates ConstantForce and Damper. Universal native mixing of all DI types is the correct approach.

## Related

- [force-feedback.md](force-feedback.md)
- [architecture.md](architecture.md)
