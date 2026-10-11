# Bundled Logitech Steering Wheel SDK runtime

These files ship with Steering Wheel Emulator. **Dependencies → Install Logitech SDK** (or Start bridge with the G920 identity) copies them to
`C:\ProgramData\SteeringWheelEmulator\LogitechSDK\` and registers them at
`HKLM\SOFTWARE\Classes\CLSID\{63BD165D-1584-4E75-AB56-08330350545F}\ServerBinary` (64- and 32-bit views).

Games built on the Logitech Steering Wheel SDK (NFS Heat, etc.) load the DLL from that key. Without it they never
identify a Logitech wheel and treat the virtual G920 as a generic controller.

| File | Purpose |
|------|---------|
| `x64\LogitechSteeringWheel.dll` | SDK runtime for 64-bit games (v8.81.15) |
| `x86\LogitechSteeringWheel.dll` | SDK runtime for 32-bit games (v8.81.15) |

## Ownership

`LogitechSteeringWheel.dll` is © Logitech Inc. and is **not** covered by this project's license. It is the runtime
originally shipped with Logitech Gaming Software and is included unmodified (Logitech Authenticode signature intact)
only so the emulator works without Logitech software installed. If Logitech asks, these files will be removed.
