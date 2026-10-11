# Profiles (repo reference only - not shipped)

This folder is kept in the repo for developers. **It is not copied into `dist\`, the release zip, or the install folder.**

## Runtime location (what users actually get)

On first run the app creates:

```
%AppData%\SteeringWheelEmulator\profiles\              ← input bindings (Default.json + Save / Save As)
%AppData%\SteeringWheelEmulator\ffb-profiles\          ← FFB presets (Raw.json + Save / Save As)
%AppData%\SteeringWheelEmulator\telemetry-profiles\    ← SimHub simulation presets (Save / Save As)
%AppData%\SteeringWheelEmulator\settings.json
```

Older installs under `%AppData%\G920Emulator\` are migrated into the new folder on first launch after the product rename.

`default.json` in this repo folder is a **legacy sample** and does **not** match the current runtime Default shape (`ffbProfileName: "Raw"`, full control list). Prefer the live file under AppData after first launch.

Optional USB-stick mode: create `portable.txt` beside `SteeringWheelEmulator.exe` to store **`profiles\`**, **`ffb-profiles\`**, and **`settings.json`** next to the exe instead (runtime-created; still not shipped in the zip).

See the [user guide](../docs/user-guide.md#profiles) and [README](../README.md#profiles-where-your-binds--ffb-live).
