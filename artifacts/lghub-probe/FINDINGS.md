# G HUB / Unbound G920 detection probe (2026-10-03)

Canonical copy for the repo: [docs/research-logitech-g920.md](../../docs/research-logitech-g920.md).



## How G HUB exposes a real G920



1. Package `g920` depends on `driver_usb`, `driver_hid_di_ffb`, `g920_dfu`.

2. `driver_hid_di_ffb` → `hid_driver_manager.exe` installs **`logi_joy_hid.inf`** into the driver store.

3. That INF binds:

   - `HID\VID_046D&PID_C262&Col01` → upper filters `logi_generic_hid_filter` + **`logi_joy_hid_filter`**

   - Friendly name: **Logitech G HUB G920 Driving Force Racing Wheel USB (HID)**

   - OEM registry via `G920Inst.AddReg` (`OEMData=43 00 08 10 12 00 00 00`, FFB CLSID `{62B43F0E-E7DB-4329-8C13-A966D84A289F}`)



## FFB ingress failure (HID++ writes = 0)



When Col01 is owned by **`logi_joy_hid_filter`**, Logitech’s HID++ FFB COM server talks to the filter via private IOCTLs. Those never become VHF `WriteReport` events, so our emulator counters stay at **0** even while Unbound “sees” a G920.



**Fix (current):** advertise Hardware ID with **`&REV_9601`** so PnP does **not** match `logi_joy_hid.inf`. Logitech’s `hidpp_forcefeedback` still loads but does **not** deliver WriteReports to VHF (`HidD_SetOutputReport` → ERROR_NOT_SUPPORTED).  

**FFB path now:** OEMForceFeedback CLSID → our **`g920ffb.dll`** (`IDirectInputEffectDriver`) → shared memory `Local\G920Emulator.FfbTorque` → bridge.



| Change | Purpose |

|--------|---------|

| Hardware ID → `HID\VID_046D&PID_C262&REV_9601` | Avoid Logitech filter auto-bind |

| Do **not** `pnputil /install` logi_joy_hid | Filter swallows FFB |

| `TryRemoveLogitechCol01` on Start | Clean leftover no-REV Col01 from older builds |

| GetFeature \| SetFeature \| WriteReport | Capture all host FFB paths |

| Host writes counter in FFB diagnostics | Distinguish “no traffic” vs “unparsed traffic” |



## Unbound / recognition



- Games that use DirectInput OEM (`OEMName` / `OEMData` / `OEMForceFeedback`) still resolve VID/PID → G920 profile without the Logitech filter.

- G HUB tile / “Logitech G HUB G920 … (HID)” friendly name may be absent (Microsoft HID-compliant name). That is expected and preferable for FFB.



## G HUB tile shows G920 “Inactive”



Expected for a WinUHid/VHF device — “Active” needs the real USB + HID++ handshake. Not required for DI FFB.



## Effect probe (which FFB types a game uses)



`g920ffb.dll` publishes shared-memory v3 fields `TypesSeen` / `TypesPlaying` (bitmasks of DI effect type IDs). The emulator UI shows human names (Constant, Spring, Square/Sine/…, Damper, …). A full log is also written to `%TEMP%\g920ffb-effects.log` on each DownloadEffect.



### Unbound probe (2026-10-04)

`g920ffb-effects.log` at menu/load downloaded these DirectInput slots:

| Type | Handles | Notes |
|------|---------|--------|
| **ConstantForce** | 1 | mag=5000 (slot exists; may stay idle or update later) |
| **Sine** | 2, 6 | rumble; handle 6 mag=10000 |
| **Damper** | 3 | coeff=0 at download |
| **Spring** | 4, 5 | centering; handle 5 coeff=10000 |

So Unbound is not spring+rumble only — it also allocates **ConstantForce** and **Damper**. Universal native mixing of all DI types remains the right approach.


