# Profiles (repo only — not shipped)

This folder is kept in the repo for reference. **It is not copied into `dist\`, the release zip, or the install folder.**

At runtime the app creates user data here:

```
%AppData%\G920Emulator\profiles\
%AppData%\G920Emulator\settings.json
```

and seeds an empty **Default** profile if none exists. See the [user guide](../docs/user-guide.md#profiles) and [GitHub README](../README.md#profiles-where-your-binds-live).

Optional: create `portable.txt` beside `G920Emulator.exe` to store profiles next to the exe instead (created on first run; still not shipped in the zip).