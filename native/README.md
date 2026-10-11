# Native dependencies

| Path | Purpose |
|------|---------|
| `winuhid\` | Bundled WinUHid runtime + INF (publish copies into `dist\G920Emulator\winuhid\` and `WinUHid.dll` beside the EXE) |
| `emuffb\` | OEM DirectInput EffectDriver sources → `emuffb\bin\emuffb.dll` (shared by G920 and Fanatec PC Comp identities) |
| `logisdk\` | Bundled Logitech Steering Wheel SDK runtimes (publish copies into `dist\G920Emulator\logisdk\`) |

Upstream WinUHid: https://github.com/cgutman/WinUHid

Rebuild helpers from the repo root: `tools\build-emuffb.ps1`, `tools\build-winuhid.ps1`. Full publish: `.\publish.ps1`.
