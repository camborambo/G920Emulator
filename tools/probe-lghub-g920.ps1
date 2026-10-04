# Captures how Windows / G HUB present a G920 (046D:C262) vs our WinUHid VHF device.
# Run elevated AFTER G HUB is installed and the emulator bridge has been Started once.
param(
    [string]$OutDir = $(Join-Path $PSScriptRoot "..\artifacts\lghub-probe")
)

$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $OutDir "probe-$stamp.txt"

function W([string]$s) {
    $s | Tee-Object -FilePath $out -Append
}

W "=== G920 / G HUB probe $stamp ==="
W ""

W "=== G HUB install ==="
@(
    'C:\ProgramData\LGHUB',
    'C:\Program Files\LGHUB',
    'C:\Program Files\Logitech\LogiOptionsPlus'
) | ForEach-Object { W "$_ exists=$(Test-Path $_)" }

Get-Process | Where-Object { $_.ProcessName -match 'lghub|logitech' } |
    ForEach-Object { W ("proc {0} pid={1} path={2}" -f $_.Name, $_.Id, $_.Path) }

Get-Service | Where-Object { $_.Name -match 'Logi|lghub|LGHUB' } |
    ForEach-Object { W ("svc {0} {1} ({2})" -f $_.Name, $_.Status, $_.DisplayName) }

W ""
W "=== PnP nodes matching C262 / G920 / Logi joy / VHF ==="
Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match 'C262|VHF|LOGIJOY|LOGIHID' -or
    $_.FriendlyName -match 'G920|Driving Force|Logi|VHF|G HUB'
} | ForEach-Object {
    W ("{0} | {1} | {2}" -f $_.Status, $_.Class, $_.FriendlyName)
    W ("  InstanceId: {0}" -f $_.InstanceId)
    try {
        $p = Get-PnpDeviceProperty -InstanceId $_.InstanceId -ErrorAction SilentlyContinue
        foreach ($key in @(
            'DEVPKEY_Device_Driver',
            'DEVPKEY_Device_DriverDesc',
            'DEVPKEY_Device_DriverInfPath',
            'DEVPKEY_Device_DriverProvider',
            'DEVPKEY_Device_Service',
            'DEVPKEY_Device_EnumeratorName',
            'DEVPKEY_Device_BusReportedDeviceDesc',
            'DEVPKEY_Device_Manufacturer',
            'DEVPKEY_Device_HardwareIds',
            'DEVPKEY_Device_CompatibleIds',
            'DEVPKEY_Device_Parent'
        )) {
            $v = ($p | Where-Object { $_.KeyName -eq $key }).Data
            if ($null -ne $v) {
                if ($v -is [System.Array]) { $v = ($v -join ' | ') }
                W ("  {0}: {1}" -f $key, $v)
            }
        }
    } catch {}
    W ""
}

W "=== OEM joystick registry ==="
foreach ($root in @('HKCU', 'HKLM')) {
    $path = "$($root):\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262"
    if ($root -eq 'HKCU') {
        $path = "HKCU:\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262"
    } else {
        $path = "HKLM:\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262"
    }
    if (Test-Path $path) {
        W $path
        Get-ItemProperty $path | Format-List | Out-String | ForEach-Object { W $_.TrimEnd() }
    } else {
        W "missing $path"
    }
}

W ""
W "=== Driver store joy packages ==="
Get-ChildItem 'C:\Windows\System32\DriverStore\FileRepository' -Directory -Filter 'logi_joy*' -ErrorAction SilentlyContinue |
    ForEach-Object {
        W $_.FullName
        Get-ChildItem $_.FullName -Filter '*.inf' | ForEach-Object {
            W ("  INF {0}" -f $_.Name)
            Select-String -Path $_.FullName -Pattern 'C262|G920|Manufacturer|Service|DeviceDesc|HardwareId|Compat' |
                Select-Object -First 40 |
                ForEach-Object { W ("    {0}" -f $_.Line.Trim()) }
        }
    }

W ""
W "=== LGHUB depots (driver_hid_joystick / driver_usb) ==="
$depots = 'C:\ProgramData\LGHUB\depots'
if (Test-Path $depots) {
    Get-ChildItem $depots -Directory | ForEach-Object {
        $hid = Join-Path $_.FullName 'driver_hid_joystick'
        $usb = Join-Path $_.FullName 'driver_usb'
        if ((Test-Path $hid) -or (Test-Path $usb)) {
            W ("depot {0}" -f $_.Name)
            if (Test-Path $hid) {
                Get-ChildItem $hid -Recurse -Include *.inf,*.sys | ForEach-Object { W ("  {0}" -f $_.FullName) }
            }
            if (Test-Path $usb) {
                Get-ChildItem $usb -Recurse -Include *.inf,*.sys | ForEach-Object { W ("  {0}" -f $_.FullName) }
            }
        }
    }
} else {
    W "no $depots"
}

W ""
W "=== DirectInput snapshot ==="
$diProj = Join-Path $env:TEMP 'G920ProbeDI'
New-Item -ItemType Directory -Force -Path $diProj | Out-Null
@'
using System;
using SharpDX.DirectInput;
class P {
  static void Main() {
    using var di = new DirectInput();
    foreach (var i in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)) {
      Console.WriteLine(i.Type + "\t" + i.UsagePage + "/" + i.Usage + "\t" + i.InstanceName + "\t" + i.ProductName + "\t" + i.ProductGuid);
    }
  }
}
'@ | Set-Content (Join-Path $diProj 'Program.cs')
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="SharpDX.DirectInput" Version="4.2.0" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $diProj 'ProbeDI.csproj')
Push-Location $diProj
dotnet run -c Release 2>&1 | ForEach-Object { W $_ }
Pop-Location

W ""
W "Done. Wrote $out"
Write-Host "Wrote $out" -ForegroundColor Green
