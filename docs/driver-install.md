# WinUHid driver install (required for games)

G920 Emulator creates a virtual Logitech G920 (`VID 046D` / `PID C262`) using **[WinUHid](https://github.com/cgutman/WinUHid)**.

Without the driver, the app still runs in **preview mode** so you can map controls, but games will not see a G920.

## Install (bundled — no download)

WinUHid files ship inside the app under `winuhid\`.

1. Launch G920 Emulator (**approve the UAC prompt** — WinUHid only allows Administrators to open the device)
2. Open **Dependencies…** → **Install WinUHid**
3. Click **Install WinUHid** and approve any additional UAC prompts
4. If prompted to reboot (test signing), reboot, then click **Install WinUHid** once more
5. Click **Recheck** until the driver is responding
6. **Start bridge**, then confirm in `joy.cpl` that a G920-class device appears

If Recheck says access denied, the driver is installed but the app is not elevated — close it and relaunch so Windows can show the UAC prompt.

### “WinUHid device not found” after install

Some Windows 10 builds do not support `pnputil /add-device`. The app installer uses a SetupAPI fallback that works without it. If Recheck still says the device is missing:

1. Confirm **Test signing: ON** (and reboot after first enabling it)
2. Click **Install WinUHid** again and approve UAC
3. Manual fallback (Admin PowerShell):

```powershell
cd <path-to>\G920Emulator\winuhid
.\Install-DeviceNode.ps1
```

4. Recheck in the app. Log file if needed: `%TEMP%\g920emulator-winuhid-install.log`

### Secure Boot

The bundled driver is **test-signed**. If `bcdedit` reports that the value is protected by Secure Boot policy, **disable Secure Boot in UEFI/BIOS**, boot Windows, then run Install WinUHid again. Running Admin CMD alone will not work while Secure Boot is on.

## HidHide (required)

Games may read both your real pad/wheel and the virtual G920. HidHide is **required** so the game only sees the emulated G920.

1. In G920 Emulator open **Dependencies…**
2. If needed, click **Download HidHide…**, install the MSI, reboot if prompted, then **Recheck**
3. Click **Configure HidHide** (one button). This only **adds** what the app needs — it does not remove your existing HidHide apps or hidden devices:
   - Turns **inverse off** (normal whitelist mode)
   - Enables **cloak**
   - Whitelists `G920Emulator.exe`
   - Hides newly detected pads **and wheel bases** so games do not get double input (keeps the virtual G920 visible; skips devices already hidden). The emulator stays whitelisted so it can still read them for binding/FFB.
4. Accept UAC if prompted, and relaunch if the app asks
5. Optional: open **HidHide Client** to hide extra devices or review the list

## Force feedback notes

- Select your physical FFB wheel under **Force feedback → FFB output device**
- Games drive FFB through DirectInput OEM into **`g920ffb.dll`**, which publishes torque over shared memory; the bridge applies it to your base (not Logitech HID++ WriteReports)
- **Start bridge** registers the OEM driver and attaches FFB; use **FFB debug** only when testing or probing effect types
- Full detail: [force-feedback.md](force-feedback.md)
- Exclusive cooperative level may require running G920 Emulator elevated on some setups

## Validation checklist

- [ ] Devices appear in G920 Emulator after Refresh
- [ ] Bindings update live meters (steering, pedals, gear R/1–6)
- [ ] With WinUHid installed, virtual G920 shows in `joy.cpl`
- [ ] Game sees G920
- [ ] FFB moves the physical wheel when the game applies force
