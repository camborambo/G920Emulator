$ErrorActionPreference = 'Continue'
$cliCandidates = @(
  'C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe',
  'C:\Program Files\Nefarius Software Solutions\HidHide\HidHideCLI.exe'
)
$cli = $cliCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
"CLI=$cli"
if ($cli) {
  "=== cloak ==="; & $cli --cloak-state 2>&1
  "=== inv ==="; & $cli --inv-state 2>&1
  "=== apps ==="; & $cli --app-list 2>&1
  "=== devs ==="; & $cli --dev-list 2>&1
}

"=== PnP ==="
Get-PnpDevice -PresentOnly -EA SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'G920|Wireless Controller|DualSense' -or $_.InstanceId -match 'C262|054C' } |
  ForEach-Object { "$($_.Status) | $($_.FriendlyName) | $($_.InstanceId)" }

$diProj = Join-Path $env:TEMP 'G920ProbeDI3'
New-Item -ItemType Directory -Force -Path $diProj | Out-Null
@'
using System;
using System.Threading;
using SharpDX.DirectInput;
class P {
  static void Main() {
    using var di = new DirectInput();
    foreach (var i in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)) {
      Console.WriteLine("DEV\t" + i.Type + "\t" + i.InstanceName + "\t" + i.ProductGuid);
      try {
        using var j = new Joystick(di, i.InstanceGuid);
        j.Properties.BufferSize = 128;
        j.Acquire();
        Console.WriteLine("OK_ACQUIRE\t" + i.InstanceName + "\tbuttons=" + j.Capabilities.ButtonCount + "\taxes=" + j.Capabilities.AxeCount);
        for (int n = 0; n < 20; n++) {
          j.Poll();
          var s = j.GetCurrentState();
          for (int b = 0; b < Math.Min(s.Buttons.Length, j.Capabilities.ButtonCount); b++)
            if (s.Buttons[b]) Console.WriteLine("BTN\t" + b);
          Thread.Sleep(50);
        }
      } catch (Exception ex) {
        Console.WriteLine("FAIL_ACQUIRE\t" + i.InstanceName + "\t" + ex.Message);
      }
    }
  }
}
'@ | Set-Content (Join-Path $diProj 'Program.cs')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="SharpDX.DirectInput" Version="4.2.0" /></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $diProj 'Probe.csproj')
Push-Location $diProj
dotnet run -c Release 2>&1
Pop-Location
