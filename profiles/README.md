# Profiles

Saved mappings live in this folder (next to `G920Emulator.exe`).

| File | Role |
|------|------|
| `default.json` | Starter profile shipped with the app; Save updates it (or create new names) |
| `*.json` you save | Your profiles from **Save** / **Save As** / **Import** |

App settings (`settings.json`) sit one level up, next to the exe.

If this folder is not writable (rare — e.g. Program Files without permission), the app falls back to `%AppData%\G920Emulator\profiles` and migrates older AppData saves here when it can.
