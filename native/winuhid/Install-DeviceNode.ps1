# Manual fallback: create the Root\WinUHid device node and bind WinUHidDriver.inf.
# Run elevated (right-click PowerShell → Run as administrator):
#   cd <path-to>\SteeringWheelEmulator\winuhid
#   .\Install-DeviceNode.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf = Join-Path $here 'WinUHidDriver.inf'
if (-not (Test-Path -LiteralPath $inf)) {
    throw "WinUHidDriver.inf not found next to this script: $here"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator.'
}

Write-Host "INF: $inf"
& pnputil.exe /add-driver $inf /install

if (-not ('WinUHidDevNode' -as [type])) {
    # PowerShell Add-Type uses an older C# compiler - keep this C# 5 compatible.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinUHidDevNode
{
  [StructLayout(LayoutKind.Sequential)]
  public struct SP_DEVINFO_DATA
  {
    public int cbSize;
    public Guid ClassGuid;
    public int DevInst;
    public IntPtr Reserved;
  }

  [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

  [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool SetupDiCreateDeviceInfo(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid,
    string DeviceDescription, IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);

  [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool SetupDiSetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
    uint Property, byte[] PropertyBuffer, int PropertyBufferSize);

  [DllImport("setupapi.dll", SetLastError = true)]
  static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

  [DllImport("setupapi.dll", SetLastError = true)]
  static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

  [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string HardwareId, string FullInfPath,
    uint InstallFlags, out bool bRebootRequired);

  const int DICD_GENERATE_ID = 0x00000001;
  const uint SPDRP_HARDWAREID = 0x00000001;
  const uint DIF_REGISTERDEVICE = 0x00000019;
  const uint INSTALLFLAG_FORCE = 0x00000001;
  const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

  static bool IsAlreadyExists(int err)
  {
    return ((uint)err) == 0xE000020B;
  }

  public static string Create(string infPath)
  {
    Guid classGuid = new Guid("4d36e97d-e325-11ce-bfc1-08002be10318");
    IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
    if (set == IntPtr.Zero || set == new IntPtr(-1))
      return "SetupDiCreateDeviceInfoList failed: " + Marshal.GetLastWin32Error();

    try
    {
      SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
      data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
      if (!SetupDiCreateDeviceInfo(set, "WinUHid", ref classGuid, "WinUHid", IntPtr.Zero, DICD_GENERATE_ID, ref data))
      {
        int err = Marshal.GetLastWin32Error();
        if (!IsAlreadyExists(err))
          return "SetupDiCreateDeviceInfo failed: " + err;
      }
      else
      {
        byte[] bytes = Encoding.Unicode.GetBytes("Root\\WinUHid\0\0");
        if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, bytes, bytes.Length))
          return "SetupDiSetDeviceRegistryProperty failed: " + Marshal.GetLastWin32Error();
        if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
        {
          int err = Marshal.GetLastWin32Error();
          if (!IsAlreadyExists(err))
            return "DIF_REGISTERDEVICE failed: " + err;
        }
      }

      bool reboot;
      if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, "Root\\WinUHid", infPath,
            INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out reboot))
        return "UpdateDriverForPlugAndPlayDevices failed: " + Marshal.GetLastWin32Error();

      if (reboot)
        return "OK (reboot may be required)";
      return "OK";
    }
    finally
    {
      SetupDiDestroyDeviceInfoList(set);
    }
  }
}
'@
}

$result = [WinUHidDevNode]::Create($inf)
Write-Host "SetupAPI: $result"
if ($result -notmatch '^OK') { throw $result }

& pnputil.exe /add-driver $inf /install | Out-Host
try { & pnputil.exe /scan-devices | Out-Host } catch {}
Write-Host ''
Write-Host 'Done. In Steering Wheel Emulator click Recheck. If it still fails, reboot once and Recheck again.'
