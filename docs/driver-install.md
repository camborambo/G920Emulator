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

**G920 Emulator does not change HidHide on launch or Start bridge.** You configure it.

1. Install HidHide ([releases](https://github.com/nefarius/HidHide/releases/latest)), reboot if prompted
2. Open **HidHide Client** (or Dependencies → **Open HidHide Client** / optional **Configure HidHide** helper) and set up what you want, typically:
   - **Inverse** off (normal whitelist mode)
   - **Cloak** on
   - Whitelist `G920Emulator.exe` so this app can still see your pad for binding/FFB
   - Hide your physical pad / wheel from games; keep the virtual G920 visible
3. Recheck in Dependencies if you use that window

## Logitech Steering Wheel SDK (required)

Games built on the Logitech Steering Wheel SDK (NFS Heat and similar) find the SDK through
`HKLM\SOFTWARE\Classes\CLSID\{63BD165D-1584-4E75-AB56-08330350545F}\ServerBinary`. If that key is missing or points at G HUB's SDK, the game never identifies a Logitech wheel: no wheel layout, wrong buttons, phantom D-pad input. DirectInput-only games and `joy.cpl` are unaffected, which is why the wheel can look fine in `joy.cpl` and still fail in-game.

The SDK (`LogitechSteeringWheel.dll` 8.81, x64 + x86) is bundled in `logisdk\`. No G HUB or Logitech Gaming Software needed.

1. Dependencies → **Install Logitech SDK** (Start bridge also installs it if missing)
2. The DLLs are copied to `C:\ProgramData\G920Emulator\LogitechSDK\` and registered for 64- and 32-bit games

## After installing or uninstalling Logitech G HUB

G HUB's installer rewrites the DirectInput OEM entry for `VID_046D` / `PID_C262` (FFB CLSID) and repoints the Logitech SDK key at its own SDK; its uninstaller deletes the SDK key. It can also bind `logi_joy_hid` to the virtual wheel.

**The G HUB guard runs whenever G920 Emulator is open** (Dependencies shows it as **ACTIVE**). Every 2 seconds it checks the OEM identity, FFB CLSID, `g920ffb.dll` COM registration and Logitech SDK registration, and rewrites anything G HUB changed or deleted. With the bridge running it also removes `logi_joy_hid` from the virtual Col01. It never `pnputil /restart-device`s the virtual wheel (that orphans WinUHid Col01). If a game still fails:

1. Quit G HUB completely (if present)
2. Dependencies → **Repair G HUB leftovers**
3. Check **HidHide** yourself if games still see your pad (app does not change it automatically)
4. **Start bridge**, confirm device name looks like a G920 / wheel in `joy.cpl`, then launch the game
5. Reboot once if Device Manager still shows Logitech-bound G920 nodes

You do not need G HUB for this app.

## Force feedback notes

- Select your physical FFB wheel under **Force feedback → FFB output device**
- Games drive FFB through DirectInput OEM into **`g920ffb.dll`**, which publishes torque over shared memory; the bridge applies it to your base (not Logitech HID++ WriteReports)
- **Start bridge** registers the OEM driver and attaches FFB; use status-bar **Debug** to capture OEM logs, and **FFB debug** for on-screen test controls
- Full detail: [force-feedback.md](force-feedback.md)
- Exclusive cooperative level may require running G920 Emulator elevated on some setups

## Validation checklist

- [ ] Devices appear in G920 Emulator after Refresh
- [ ] Bindings update live meters (steering, pedals, gear R/1–6)
- [ ] With WinUHid installed, virtual G920 shows in `joy.cpl`
- [ ] Game sees G920
- [ ] FFB moves the physical wheel when the game applies force
