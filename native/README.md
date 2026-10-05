# Native dependencies

| Path | Purpose |
|------|---------|
| `winuhid\` | Bundled WinUHid runtime + INF (publish copies into `dist\G920Emulator\winuhid\` and `WinUHid.dll` beside the EXE) |
| `g920ffb\` | OEM DirectInput EffectDriver sources → `g920ffb\bin\g920ffb.dll` |
| `logisdk\` | Bundled Logitech Steering Wheel SDK runtimes (publish copies into `dist\G920Emulator\logisdk\`) |

Upstream WinUHid: https://github.com/cgutman/WinUHid

Rebuild helpers from the repo root: `tools\build-g920ffb.ps1`, `tools\build-winuhid.ps1`. Full publish: `.\publish.ps1`.
