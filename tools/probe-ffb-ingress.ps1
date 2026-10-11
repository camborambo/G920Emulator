# Probe virtual G920 FFB ingress path while the emulator + game are running.
$ErrorActionPreference = 'Continue'
Write-Host "=== Processes ==="
Get-Process SteeringWheelEmulator,G920Emulator,NeedForSpeedUnbound,NFSUnbound -ErrorAction SilentlyContinue |
  Format-Table Id, ProcessName, StartTime -AutoSize

Write-Host "`n=== C262 PnP nodes ==="
$nodes = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'C262' }
if (-not $nodes) { Write-Host 'NO C262 PRESENT'; exit 1 }
$nodes | Format-Table Status, Class, FriendlyName, InstanceId -AutoSize

foreach ($n in $nodes) {
  Write-Host "`n--- Drivers for $($n.InstanceId) ---"
  pnputil /enum-devices /instanceid "$($n.InstanceId)" /drivers 2>&1 | Out-String | Write-Host
}

Write-Host "`n=== OEMForceFeedback ==="
foreach ($root in @('HKCU','HKLM')) {
  $p = "$root`:\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback"
  if (Test-Path $p) {
    $v = Get-ItemProperty $p
    Write-Host "$p CLSID=$($v.CLSID)"
  } else { Write-Host "MISSING $p" }
}

Write-Host "`n=== HID++ FFB CLSID server ==="
$clsid = '{62B43F0E-E7DB-4329-8C13-A966D84A289F}'
$ip = "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
if (Test-Path $ip) { Write-Host (Get-ItemProperty $ip).'(default)' } else { Write-Host 'CLSID missing' }

Write-Host "`n=== HID paths (SetupAPI via Get-PnpDevice + hid) ==="
Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
  Where-Object { $_.DeviceID -match 'VID_046D&PID_C262' } |
  Select-Object Name, DeviceID, Status, Service |
  Format-List

Write-Host "`nDone. In the app FFB panel note: Host writes / HID++ writes / Path."
