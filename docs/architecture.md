# Architecture

G920 Emulator bridges physical DirectInput devices onto a **virtual Logitech G920** (`VID 046D` / `PID C262`) that games see as a system HID device, and forwards game force-feedback to a physical wheel base.

## End-to-end flow

```mermaid
flowchart LR
  Phys[Physical_DI_devices] --> Hub[InputHub_Mapper]
  Hub --> Vhid[Virtual_G920_WinUHid]
  Vhid --> Game[Game]
  Game --> Oem[g920ffb_OEM_EffectDriver]
  Oem -->|SharedMem| Bridge[BridgeService]
  Hub -->|PhysicalSteer| Oem
  Bridge --> Base[Physical_FFB_base]
```

## Layers

| Layer | Project / binary | Role |
|-------|------------------|------|
| UI | `src/G920Emulator.App` | Profiles, bindings, dependencies, FFB device selection, live meters |
| Bridge | `src/G920Emulator.Core` | Poll loop (~500 Hz), mapping, OEM shared-memory I/O, FFB apply |
| Virtual HID | `src/G920Emulator.VirtualHid` | WinUHid device, G920 report descriptor, OEM registry / COM registration |
| OEM FFB driver | `native/g920ffb/g920ffb.dll` | `IDirectInputEffectDriver` loaded by DirectInput when games create effects |
| System driver | WinUHid (bundled) | Exposes the virtual HID device to Windows / games |
| Input isolation | HidHide | Hides physical pads/wheels from games so only the virtual G920 is seen |

## Input path (physical → game)

1. **InputHub** enumerates DirectInput joysticks and polls axes/buttons/hats.
2. **MapperEngine** applies the active JSON profile (`Binding` / `SourceRef`) to produce a `MappedG920State`. Profile updates from the UI are picked up on the next loop tick, so rebinding does not require restarting the bridge. The virtual D-pad can come from a POV hat binding, or from **D-pad Up/Down/Left/Right** button (or axis→button) bindings synthesized into an 8-way hat nibble.
3. **G920ReportBuilder** packs that state into a numbered HID input report matching a real G920 (report ID 1), including H-pattern gear buttons.
4. **VirtualG920Device** submits the report through WinUHid.
5. The game reads the virtual G920 like any other DirectInput / HID wheel.

Gear packing:

- Gears 1–6 = buttons **13–18** (LGS / Driving Force Shifter)
- Reverse = profile `GearReverseOutputButton` (default **19** LGS; **12** for NFS Unbound)

## Force-feedback path (game → physical base)

**Contract:** the game must see and target the **virtual G920 only**. Physical bases, vJoy, and pads are HidHide’d from the game. Which wheel base is attached on the emulator side is an output choice — it must not change what the game sends.

Games that support a Logitech G920 via DirectInput OEM do **not** rely on Logitech HID++ WriteReports for our virtual device. Instead:

1. On Start, the app registers OEM joystick identity and points `OEMForceFeedback` at **`g920ffb.dll`** (CLSID `{A920FFB0-E7DB-4329-8C13-A966D84A289F}`).
2. The game (and any helper that opens the same OEM device) downloads/starts DI effects (constant, spring, damper, sine, triangle, …) on that virtual G920.
3. `g920ffb.dll` mixes effects per process and publishes into `Local\G920Emulator.FfbTorque.v6`:
   - **Torque** — game process (primary)
   - **AuxTorque** — Steam/overlay only, layered under the game channel so helpers cannot wipe spring/road forces
4. **BridgeService** writes physical rim angle into that shared memory (required for spring/damper), reads **combined** torque, optionally applies **output feel** / **torque shaping** from the FFB profile, and **FfbBridge** applies it as a constant-force effect on the selected physical base (master gain + invert). Physical DI apply runs on a **side thread** so a slow base cannot stall virtual G920 axis submits.

**Validated FFB bases:** Fanatec Podium Wheel Base DD2 and Simucube (NFS Heat / Unbound). See [compatibility](compatibility.md).

Verbose OEM / HID++ file logging is off until the UI **Debug** session is active; after **Stop debug**, **Export log…** builds the support zip.

Details: [force-feedback.md](force-feedback.md).

## Identity and PnP

- Virtual hardware ID uses `HID\VID_046D&PID_C262&REV_9601` so Logitech G HUB’s `logi_joy_hid_filter` does **not** auto-bind (that filter swallows FFB into private IOCTLs).
- DirectInput still maps VID/PID to a G920 OEM profile via registry `OEMData` / `OEMName`.

Research notes: [research-logitech-g920.md](research-logitech-g920.md).

## Profiles

- **Input storage:** `%AppData%\G920Emulator\profiles\*.json` and `settings.json`.
- **FFB storage:** `%AppData%\G920Emulator\ffb-profiles\*.json` (seeded **Raw** only). Input profiles link via `ffbProfileName`.
- The publish zip / `dist\G920Emulator\` do **not** include `profiles\` or `ffb-profiles\`.
- **Starter:** on first run, `ProfileStore` creates AppData, an empty Default input profile, and the built-in FFB presets.
- **Migration:** older next-to-exe / `N4Sunbound` profiles are copied into AppData once (never overwriting existing AppData files); legacy inline FFB fields on input JSON can migrate into an FFB profile.
- **Identity:** each `SourceRef` stores `deviceId` (instance GUID) and `productId` (product GUID); `DeviceBindingResolver` remaps instance ids when the product is still attached.
- **Portable opt-in:** `portable.txt` beside the exe keeps saves next to the app (runtime-created folder only).
- UI: input Saved dropdown + Export / Import; FFB profile dropdown + Save / Save As / Delete under Force feedback.
