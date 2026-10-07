# Bundled WinUHid package

These files ship with G920 Emulator. The in-app **Install WinUHid** button installs from this folder - no download.

| File | Purpose |
|------|---------|
| `WinUHid.dll` | User-mode SDK (required) |
| `WinUHidDriver.dll` | UMDF driver binary (required for games) |
| `WinUHidDriver.inf` | Driver install info |
| `WinUHidDriver.cat` | Catalog (test-signed) |
| `WinUHidCertificate.cer` | Test-signing certificate |

Rebuild driver (developers, needs WDK + VS WDK component):

```powershell
.\tools\build-winuhid.ps1
```
