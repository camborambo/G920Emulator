# Building

## Prerequisites

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 with C++ desktop workload (for `g920ffb.dll`) **or** Build Tools with MSBuild + MSVC
- Optional: WinUHid sources under `third_party/WinUHid` if rebuilding the driver package (`tools/build-winuhid.ps1`)

## Solution layout

```
G920Emulator.sln
src/G920Emulator.App          WPF UI
src/G920Emulator.Core         Input, mapper, bridge, FFB apply, AppData profiles
src/G920Emulator.VirtualHid   WinUHid P/Invoke, OEM registration
native/g920ffb                OEM DirectInput EffectDriver (C++)
native/winuhid                Bundled WinUHid runtime + INF (published into dist)
native/logisdk                Bundled Logitech Steering Wheel SDK runtimes
tools/build-g920ffb.ps1       MSBuild wrapper for g920ffb.dll
tools/build-winuhid.ps1       Rebuild WinUHid package into native/winuhid
publish.ps1                   Self-contained win-x64 publish → dist\G920Emulator + zip
CHANGELOG.md                  Version notes (copied into dist)
profiles/                     Repo reference only — runtime data is AppData
```

## Version (release)

The app version is **`Directory.Build.props`** (`<Version>`). Bump that for a release so the window title, About, tray tip, diagnostics zip, and assembly metadata stay in sync. Do not add a git hash to InformationalVersion.

Users who leave **Settings → Check for GitHub updates** on are notified at launch when GitHub has a newer **published** release. Tag the GitHub release `v` + that version (example: `v0.2.7`) and attach **`G920Emulator-win-x64.zip`** as a release asset so **Update** can download it. Drafts and prereleases are ignored (`/releases/latest`). The app does not replace their folder — they unzip the zip themselves.

## Publish (recommended)

From the repo root:

```powershell
.\publish.ps1
# or
.\publish.ps1 -OpenFolder
```

This:

1. Builds `native\g920ffb\bin\g920ffb.dll` via `tools\build-g920ffb.ps1`
2. `dotnet publish`s the app self-contained for `win-x64` into `dist\G920Emulator\`
3. Copies WinUHid package files, Logitech SDK runtimes, and `g920ffb.dll` beside the EXE
4. Removes any leftover `profiles\`, `ffb-profiles\`, `settings.json`, and `portable.txt` from `dist\` (user data is AppData-only at runtime)
5. Copies `CHANGELOG.md` and `README.md` into `dist\G920Emulator\`
6. Creates `dist\G920Emulator-win-x64.zip` with a single top-level folder:
   `G920Emulator-win-x64.zip` → `G920Emulator\` → app files (no user profiles)

Run:

```
dist\G920Emulator\G920Emulator.exe
```

- **`dist\Launch G920 Emulator.bat`** — starts the already-published EXE only
- **Repo-root `Launch G920 Emulator.bat`** — starts `dist\…` if present, otherwise runs `publish.ps1` then launches

Close a running emulator before republishing — files under `dist\` lock while the EXE is open.

## Dev run (UI only)

```powershell
dotnet run --project src/G920Emulator.App
```

Ensure `g920ffb.dll` is findable beside the output EXE (publish does this automatically). Without it, OEM FFB registration logs that the DLL was not found.

## Build OEM FFB DLL alone

```powershell
.\tools\build-g920ffb.ps1
```

Output: `native\g920ffb\bin\g920ffb.dll`.

## What ships in dist

| Path | Purpose |
|------|---------|
| `G920Emulator.exe` + managed deps | App |
| `g920ffb.dll` | DirectInput OEM EffectDriver |
| `WinUHid.dll` | Native WinUHid client library |
| `winuhid\` | Driver INF / package for in-app Install WinUHid |
| `logisdk\` | Logitech Steering Wheel SDK runtimes (x64 + x86) |
| `README.md` / `CHANGELOG.md` | Docs + version notes |

Does **not** ship: `profiles\`, `ffb-profiles\`, `settings.json`, or `portable.txt` (those are created under `%AppData%\G920Emulator\` on first run, unless the user opts into portable mode).

`dist\` is gitignored; publish locally or CI as needed.

## Related docs

- [architecture.md](architecture.md)
- [force-feedback.md](force-feedback.md)
- [driver-install.md](driver-install.md)
