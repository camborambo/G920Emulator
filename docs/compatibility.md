# Compatibility

Devices and games exercised during development of this project. This is **not** an exhaustive support matrix — unlisted titles may still work if they accept a DirectInput G920 with OEM FFB.

Update this page when you validate a new setup.

## Input devices tested

| Device | Role | Notes |
|--------|------|--------|
| DualSense (PS5) | Steering / buttons / axes source | Bound into virtual G920; no DualSense-side FFB |
| Fanatec Podium Wheel Base DD2 | FFB output (+ can be used as DI input) | Constant-force apply via shared InputHub acquire |
| Simucube | FFB output (+ can be used as DI input) | Validated with **NFS Unbound** (spring + race CF / Triangle / Damper). DI apply runs on a side thread so slower `SetParameters` does not stall input. |

## Wheel bases (FFB output)

Same DirectInput constant-force path for all of these:

| Device family | Status |
|---------------|--------|
| Fanatec (Podium / DD) | **Tested** (Podium Wheel Base DD2) |
| Simucube | **Tested** (NFS Unbound) |
| Simagic | Designed for |
| Moza | Designed for |
| Thrustmaster / Logitech / generic DI FFB | Designed for |

## Games tested / compatible

| Game | Status | Input | FFB | Notes |
|------|--------|-------|-----|--------|
| Need for Speed Heat | **Tested** | Virtual G920 | OEM path used | Gears 1–6 = buttons 13–18. **Reverse: default button 19** (LGS / Driving Force Shifter). |
| Need for Speed Unbound | **Tested** | Virtual G920 | OEM path (multi-instance mixer + SHM v7) on Fanatec DD2 and Simucube | ConstantForce, Spring, Sine, Damper, Triangle. Gears 1–6 = 13–18. **Reverse: Bind Gear R → button 12**. Centering spring needs physical rim angle. **Controller Vibration must be On** (Accessibility → Controls) or race rumble magnitudes stay 0. |
| Forza Horizon 6 | **WIP** | Virtual G920 | OEM path (DIEP_ALL arming fix) | Force feedback works in early testing; feel and broader validation still ongoing. Requires **test signing off** to launch (see below). Leave status-bar **Debug** off during normal play. |

### Forza Horizon series (test signing)

Forza Horizon games (confirmed on FH6) **do not launch while Windows test signing is on**. They exit at the splash screen (Steam exit code 100) — likely an integrity / anti-cheat check. OEM/SDK session pins are not the cause.

Test signing is only required to *install* the test-signed WinUHid driver. **Install WinUHid** turns test signing on temporarily, installs the driver, then turns it **off** again. After the final reboot, FH6 can launch and WinUHid usually keeps working. Secure Boot only needs to be off during that install — you can turn Secure Boot back on afterward. If test signing was left on: **Disable test signing** → reboot (no need to uninstall WinUHid).

### Gear R quick reference

| Game | Bind Gear R → G920 reverse button |
|------|-----------------------------------|
| NFS Heat (and most G920 titles) | **19** (default) |
| NFS Unbound | **12** |

See [user guide — H-pattern gears](user-guide.md#h-pattern-gears).

## Not validated yet

Add rows above when confirmed. Candidates often requested:

- Other EA / Codemasters titles with Logitech wheel profiles
- iRacing / ACC / AMS2 (typically stronger native wheel support; G920 path may still work)

## How to add a result

When you test something new, note:

1. Physical input device(s) and FFB base
2. Game + whether the virtual G920 appeared
3. Whether FFB moved the base
4. Optional: click **Debug**, reproduce briefly, **Stop debug** → **Export log…** (or expand **FFB debug** for live counters). Leave Debug off for normal play.

See [force-feedback.md](force-feedback.md) and [user-guide.md](user-guide.md).
