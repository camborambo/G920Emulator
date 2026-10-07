#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Undoes G920 Emulator system registry changes that can break Forza Horizon 5/6
  and other titles that load the Logitech Steering Wheel SDK / G920 OEM FFB.

  Close G920Emulator.exe first (its G HUB guard re-applies the pin every ~2s).

.DESCRIPTION
  1) Removes our OEMForceFeedback CLSID override for VID_046D&PID_C262
     (and restores Logitech stock CLSID if you pass -RestoreLogitechOemClsid).
  2) Unregisters our g920ffb.dll COM server.
  3) Removes the LogitechSteeringWheel.dll ServerBinary pin under
     CLSID {63BD165D-1584-4E75-AB56-08330350545F} (64- and 32-bit views).

  Does NOT uninstall WinUHid/HidHide or delete %ProgramData%\G920Emulator\LogitechSDK
  (files can stay; only the registry pin is removed).
#>
[CmdletBinding()]
param(
    [switch] $RestoreLogitechOemClsid,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$ourFfb = '{A920FFB0-E7DB-4329-8C13-A966D84A289F}'
$logiFfb = '{62B43F0E-E7DB-4329-8C13-A966D84A289F}'
$sdkClsid = '{63BD165D-1584-4E75-AB56-08330350545F}'
$oemFfRel = 'System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C262\OEMForceFeedback'

$running = Get-Process -Name G920Emulator -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning "G920Emulator.exe is running (PID $($running.Id -join ',')). Stop it first or the guard will re-pin within seconds."
    throw "Close G920 Emulator, then re-run this script."
}

function Remove-ClsidInproc([string] $clsid) {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        $path = "SOFTWARE\Classes\CLSID\$clsid"
        if ($WhatIf) { Write-Host "WhatIf: delete HKLM($view)\$path"; continue }
        try { $base.DeleteSubKeyTree($path, $false) } catch { }
    }
    $hkcu = "HKCU:\Software\Classes\CLSID\$clsid"
    if (Test-Path $hkcu) {
        if ($WhatIf) { Write-Host "WhatIf: delete $hkcu" }
        else { Remove-Item $hkcu -Recurse -Force }
    }
}

function Clear-OemFfbClsid {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            if ($hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser -and $view -eq [Microsoft.Win32.RegistryView]::Registry32) { continue }
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            $key = $base.OpenSubKey($oemFfRel, $true)
            if (-not $key) { continue }
            $cur = $key.GetValue('CLSID')
            Write-Host "OEM CLSID before ($hive/$view): $cur"
            if ($WhatIf) { continue }
            if ($RestoreLogitechOemClsid) {
                $key.SetValue('CLSID', $logiFfb, [Microsoft.Win32.RegistryValueKind]::String)
                Write-Host "  -> restored Logitech $logiFfb"
            }
            else {
                try { $key.DeleteValue('CLSID') } catch { }
                Write-Host "  -> removed CLSID value"
            }
        }
    }
}

function Clear-SdkServerBinary {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        $path = "SOFTWARE\Classes\CLSID\$sdkClsid\ServerBinary"
        $key = $base.OpenSubKey($path, $true)
        if ($key) {
            Write-Host "SDK ServerBinary ($view): $($key.GetValue(''))"
            if (-not $WhatIf) {
                $parent = $base.OpenSubKey("SOFTWARE\Classes\CLSID\$sdkClsid", $true)
                try { $parent.DeleteSubKeyTree('ServerBinary', $false) } catch { }
                Write-Host "  -> removed ServerBinary"
            }
        }
        else {
            Write-Host "SDK ServerBinary ($view): (already absent)"
        }
    }
}

Write-Host '=== Restoring system Logitech registration (G920 Emulator undo) ==='
Clear-OemFfbClsid
Write-Host 'Unregistering g920ffb COM server...'
Remove-ClsidInproc $ourFfb
Write-Host 'Removing Logitech SDK ServerBinary pin...'
Clear-SdkServerBinary
Write-Host ''
Write-Host 'Done. Launch Forza Horizon 5/6 to verify.'
Write-Host 'If it works: the SDK/OEM pin from G920 Emulator was the cause.'
Write-Host 'Re-opening G920 Emulator will re-apply pins while the app (or bridge) runs - keep it closed for Forza until we ship a fix.'
