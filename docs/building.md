# Building

## Prerequisites

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 with C++ desktop workload (for `emuffb.dll`) **or** Build Tools with MSBuild + MSVC
- Optional: WinUHid sources under `third_party/WinUHid` if rebuilding the driver package (`tools/build-winuhid.ps1`)

## Solution layout

```
SteeringWheelEmulator.sln
src/SteeringWheelEmulator.App          WPF UI
src/SteeringWheelEmulator.Core         Input, mapper, bridge, FFB apply, AppData profiles
src/SteeringWheelEmulator.VirtualHid   WinUHid P/Invoke, OEM registration
src/SteeringWheelEmulator.SimHubPlugin SimHub RPM capability plugin (net48)
native/emuffb                OEM DirectInput EffectDriver (C++)
native/winuhid                Bundled WinUHid runtime + INF (published into dist)
native/logisdk                Bundled Logitech Steering Wheel SDK runtimes
tools/build-emuffb.ps1       MSBuild wrapper for emuffb.dll
tools/build-winuhid.ps1       Rebuild WinUHid package into native/winuhid
publish.ps1                   Self-contained win-x64 publish → dist\SteeringWheelEmulator + zip
CHANGELOG.md                  Version notes (copied into dist)
profiles/                     Repo reference only - runtime data is AppData
```

## Version (release)

The app version is **`Directory.Build.props`** (`<Version>`). Bump that for a release so the window title, About, tray tip, diagnostics zip, and assembly metadata stay in sync. Do not add a git hash to InformationalVersion.

Users who leave **Settings → Check for GitHub updates** on are notified at launch when GitHub has a newer **published** release. Tag the GitHub release `v` + that version (example: `v0.2.7`) and attach **`SteeringWheelEmulator-win-x64.zip`** as a release asset so **Update** can download it. Drafts and prereleases are ignored (`/releases/latest`). The app does not replace their folder - they unzip the zip themselves.

## Publish (recommended)

From the repo root:

```powershell
.\publish.ps1
# or
.\publish.ps1 -OpenFolder
```

This:

1. Builds `native\emuffb\bin\emuffb.dll` via `tools\build-emuffb.ps1`
2. `dotnet publish`s the app self-contained for `win-x64` into `dist\SteeringWheelEmulator\`
3. Copies WinUHid package files, Logitech SDK runtimes, and `emuffb.dll` beside the EXE
4. Removes any leftover `profiles\`, `ffb-profiles\`, `settings.json`, and `portable.txt` from `dist\` (user data is AppData-only at runtime)
5. Copies `CHANGELOG.md` and `README.md` into `dist\SteeringWheelEmulator\`
6. Creates `dist\SteeringWheelEmulator-win-x64.zip` with a single top-level folder:
   `SteeringWheelEmulator-win-x64.zip` → `SteeringWheelEmulator\` → app files (no user profiles)

Run:

```
dist\SteeringWheelEmulator\SteeringWheelEmulator.exe
```

- **`dist\Launch Steering Wheel Emulator.bat`** - starts the already-published EXE only
- **Repo-root `Launch Steering Wheel Emulator.bat`** - starts `dist\…` if present, otherwise runs `publish.ps1` then launches

Close a running emulator before republishing - files under `dist\` lock while the EXE is open.

## Dev run (UI only)

```powershell
dotnet run --project src/SteeringWheelEmulator.App
```

Ensure `emuffb.dll` is findable beside the output EXE (publish does this automatically). Without it, OEM FFB registration logs that the DLL was not found.

## Build OEM FFB DLL alone

```powershell
.\tools\build-emuffb.ps1
```

Output: `native\emuffb\bin\emuffb.dll`.

## What ships in dist

| Path | Purpose |
|------|---------|
| `SteeringWheelEmulator.exe` + managed deps | App |
| `emuffb.dll` | DirectInput OEM EffectDriver |
| `WinUHid.dll` | Native WinUHid client library |
| `winuhid\` | Driver INF / package for in-app Install WinUHid |
| `logisdk\` | Logitech Steering Wheel SDK runtimes (x64 + x86) |
| `README.md` / `CHANGELOG.md` | Docs + version notes |

Does **not** ship: `profiles\`, `ffb-profiles\`, `settings.json`, or `portable.txt` (those are created under `%AppData%\SteeringWheelEmulator\` on first run, unless the user opts into portable mode).

`dist\` is gitignored; publish locally or CI as needed.

## Related docs

- [architecture.md](architecture.md)
- [force-feedback.md](force-feedback.md)
- [driver-install.md](driver-install.md)
