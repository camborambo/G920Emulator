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
src/G920Emulator.Core         Input, mapper, bridge, FFB apply
src/G920Emulator.VirtualHid   WinUHid P/Invoke, OEM registration
native/g920ffb                OEM DirectInput EffectDriver (C++)
native/winuhid                Bundled WinUHid runtime + INF (published into dist)
tools/build-g920ffb.ps1       MSBuild wrapper for g920ffb.dll
tools/build-winuhid.ps1       Rebuild WinUHid package into native/winuhid
publish.ps1                   Self-contained win-x64 publish → dist\G920Emulator
```

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
3. Copies WinUHid package files and `g920ffb.dll` beside the EXE

Run:

```
dist\G920Emulator\G920Emulator.exe
```

or double-click **`Launch G920 Emulator.bat`** (publishes on first run if missing).

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

`dist\` is gitignored; publish locally or CI as needed.

## Related docs

- [architecture.md](architecture.md)
- [force-feedback.md](force-feedback.md)
- [driver-install.md](driver-install.md)
