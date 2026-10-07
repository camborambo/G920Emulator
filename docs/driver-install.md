# WinUHid driver install (required for games)

G920 Emulator creates a virtual Logitech G920 (`VID 046D` / `PID C262`) using **[WinUHid](https://github.com/cgutman/WinUHid)**.

Without the driver, the app still runs in **preview mode** so you can map controls, but games will not see a G920.

## Install (bundled — no download)

WinUHid files ship inside the app under `winuhid\`.

1. Launch G920 Emulator (**approve the UAC prompt** — WinUHid only allows Administrators to open the device)
2. Open **Dependencies…** → **Install WinUHid**
3. Click **Install WinUHid** and approve any additional UAC prompts
4. **Forza-friendly flow:** Install enables test signing only if needed, installs the driver, then **turns test signing back off**. You may need:
   - **Reboot #1** if test signing was not live yet → open the app → click **Install WinUHid** once more to finish
   - **Reboot #2** after the finish pass (leaves test signing off)
5. After the final reboot, click **Recheck** — WinUHid should show Installed and test signing **Off (OK)**. Do **not** Install again (a duplicate Install can break detection)
6. **Start bridge**, then confirm in `joy.cpl` that a G920-class device appears

If Recheck says access denied, the driver is installed but the app is not elevated — close it and relaunch so Windows can show the UAC prompt.

### Uninstall WinUHid

Dependencies → **Uninstall WinUHid** (also on the Install WinUHid window). Stop the bridge first. This removes the `Root\WinUHid` device node and the published driver package, and **turns off Windows test signing**. Reinstall WinUHid when you need the virtual G920 again.

### “WinUHid device not found” after install

Some Windows 10 builds do not support `pnputil /add-device`. The app installer uses a SetupAPI fallback that works without it. If Recheck still says the device is missing:

1. Confirm test signing can be enabled (Secure Boot off), then click **Install WinUHid** again and approve UAC
2. Manual fallback (Admin PowerShell):

```powershell
cd <path-to>\G920Emulator\winuhid
.\Install-DeviceNode.ps1
```

3. Recheck in the app. Log file if needed: `%TEMP%\g920emulator-winuhid-install.log`

### Secure Boot

The bundled driver is **test-signed**. Enabling test signing (needed only for the WinUHid *install*) fails while Secure Boot is on — **disable Secure Boot in UEFI/BIOS**, boot Windows, then run Install WinUHid. Admin CMD alone cannot override Secure Boot.

**After WinUHid is installed and test signing is off**, you can **turn Secure Boot back on** in UEFI/BIOS. WinUHid and the emulator keep working; Forza Horizon can still launch. Secure Boot only needs to stay off during the install / test-signing step.

### Forza Horizon 6 and test signing

Forza Horizon 6 will not start while Windows boots in **test mode** — Steam shows exit code **100** at the splash (integrity / anti-cheat), not because of OEM/SDK pins.

**Good news:** test signing is only required to *install* WinUHid. The Forza-friendly Install flow turns test signing **off** again afterward. WinUHid (UMDF) usually keeps working with test signing off, so you can use the emulator and launch FH6 on the same boot. You can also re-enable Secure Boot once that install is finished.

If test signing was left on: **Dependencies → Disable test signing** (or Uninstall WinUHid), then **reboot**. You do **not** need to remove WinUHid just to play Forza.

## HidHide (required)

Games may read both your real pad/wheel and the virtual G920. HidHide is **required** so the game only sees the emulated G920.

**G920 Emulator does not change HidHide on launch or Start bridge.** You configure it.

1. Install HidHide ([releases](https://github.com/nefarius/HidHide/releases/latest)), reboot if prompted
2. Open **HidHide Client** (or Dependencies → **Open HidHide Client** / optional **Configure HidHide** helper) and set up what you want, typically:
   - **Inverse** off (normal whitelist mode)
   - **Cloak** on
   - Whitelist `G920Emulator.exe` so this app can still see your pad for binding/FFB
   - Hide your physical pad / wheel from games; keep the virtual G920 visible
3. Recheck in Dependencies if you use that window

## Logitech Steering Wheel SDK + OEM FFB (session-scoped)

Games built on the Logitech Steering Wheel SDK (NFS Heat and similar) find the SDK through
`HKLM\SOFTWARE\Classes\CLSID\{63BD165D-1584-4E75-AB56-08330350545F}\ServerBinary`. The OEM FFB path for the virtual G920 points at our `g920ffb.dll`.

These registry pins are **not** a permanent Dependencies install. They apply only while the bridge is running:

| Event | What happens |
|-------|----------------|
| **Start bridge** | Cache SDK DLLs under `%ProgramData%\G920Emulator\LogitechSDK\` if needed, pin SDK + OEM FFB CLSID |
| **Stop bridge / close app** | Restore previous registry (or clear our pins) |
| **App crash / Task Manager kill** | Pins stay until the next emulator launch (auto-recover) |
| **Next launch** | Auto-recovers any leftover dirty state |

Dependencies shows **OEM / Logitech SDK registration** as Idle / Active / Needs restore, with **Restore system registration** if something was left dirty. Session pins are for NFS Heat / Unbound-style games; they are **not** what stops Forza Horizon 6 from launching.

### Full clean restore (optional)

Nuclear option if you want every app leftover removed: **Dependencies → Full clean restore…** (stop the bridge first). That also turns off test signing and removes OEM/SDK pins, ProgramData caches (`LogitechSDK` + `g920ffb`), DirectInput leftovers, orphan virtual G920 nodes, older hidpp rename, and WinUHid. **Not required for FH6** — prefer **Uninstall WinUHid**.

Full clean does **not** change Secure Boot, HidHide, or your profiles under `%AppData%\G920Emulator`. If you disabled Secure Boot only for the WinUHid install, you can re-enable it afterward (with WinUHid installed and test signing off).

The SDK (`LogitechSteeringWheel.dll` 8.81, x64 + x86) is bundled in `logisdk\`. No G HUB or Logitech Gaming Software needed.

## After installing or uninstalling Logitech G HUB

G HUB's installer rewrites the DirectInput OEM entry for `VID_046D` / `PID_C262` (FFB CLSID) and repoints the Logitech SDK key at its own SDK; its uninstaller deletes the SDK key. It can also bind `logi_joy_hid` to the virtual wheel.

**The G HUB guard runs only while the bridge is running** (Dependencies shows it as **ACTIVE** then). Every 2 seconds it checks the OEM identity, FFB CLSID, `g920ffb.dll` COM registration and Logitech SDK registration, and rewrites anything G HUB changed or deleted. It also removes `logi_joy_hid` from the virtual Col01. It never `pnputil /restart-device`s the virtual wheel (that orphans WinUHid Col01). If a game still fails:

1. Quit G HUB completely (if present)
2. Dependencies → **Repair G HUB leftovers** (does not leave OEM/SDK pins while idle)
3. Check **HidHide** yourself if games still see your pad (app does not change it automatically)
4. **Start bridge**, confirm device name looks like a G920 / wheel in `joy.cpl`, then launch the game
5. Reboot once if Device Manager still shows Logitech-bound G920 nodes

You do not need G HUB for this app.

## Force feedback notes

- Select your physical FFB wheel under **Force feedback → FFB output device**
- Games drive FFB through DirectInput OEM into **`g920ffb.dll`**, which publishes torque over shared memory; the bridge applies it to your base (not Logitech HID++ WriteReports)
- **Start bridge** applies session OEM/SDK pins and attaches FFB; **Stop** restores system registration; use status-bar **Debug** only for short OEM log captures (leave it off for normal play — see [force-feedback.md](force-feedback.md#status-bar-debug-oem-file-log)), and **FFB debug** for on-screen test controls
- Full detail: [force-feedback.md](force-feedback.md)
- Exclusive cooperative level may require running G920 Emulator elevated on some setups

## Validation checklist

- [ ] Devices appear in G920 Emulator after Refresh
- [ ] Bindings update live meters (steering, pedals, gear R/1–6)
- [ ] With WinUHid installed, virtual G920 shows in `joy.cpl`
- [ ] Game sees G920
- [ ] FFB moves the physical wheel when the game applies force
