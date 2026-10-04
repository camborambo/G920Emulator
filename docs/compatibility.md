# Compatibility

Devices and games exercised during development of this project. This is **not** an exhaustive support matrix — unlisted titles may still work if they accept a DirectInput G920 with OEM FFB.

Update this page when you validate a new setup.

## Input devices tested

| Device | Role | Notes |
|--------|------|--------|
| DualSense (PS5) | Steering / buttons / axes source | Bound into virtual G920; no DualSense-side FFB |
| Fanatec Clubsport DD2 | FFB output (+ can be used as DI input) | Constant-force apply via shared InputHub acquire |

## Wheel bases expected to work (FFB output)

These use the same DirectInput constant-force path as the DD2. Listed as **designed for**, not yet logged as fully validated in this table:

| Device family | Status |
|---------------|--------|
| Fanatec (DD / Clubsport) | Tested (DD2) |
| Simucube | Designed for (DI FFConst-style updates) |
| Simagic | Designed for |
| Moza | Designed for |
| Thrustmaster / Logitech / generic DI FFB | Designed for |

## Games tested

| Game | Input | FFB | Notes |
|------|-------|-----|--------|
| Need for Speed Unbound | Virtual G920 recognized | OEM effects downloaded | Probed: ConstantForce, Spring, Sine, Damper. Gears R=12, 1–6=13–18. Centering spring needs physical rim angle. |

## Not validated yet

Add rows above when confirmed. Candidates often requested:

- Forza Horizon series (DirectInput wheel path, if G920 is selected)
- Other EA / Codemasters titles with Logitech wheel profiles
- iRacing / ACC / AMS2 (typically stronger native wheel support; G920 path may still work)

## How to add a result

When you test something new, note:

1. Physical input device(s) and FFB base
2. Game + whether the virtual G920 appeared
3. Whether FFB moved the base
4. Optional: enable **FFB debug** and record OEM effects seen, or attach `%TEMP%\g920ffb-effects.log`

See [force-feedback.md](force-feedback.md) and [user-guide.md](user-guide.md).
