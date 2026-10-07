using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace G920Emulator.Core.Setup;

public sealed class WinUHidSetupStatus
{
    public bool TestSigningEnabled { get; init; }
    public bool DllPresent { get; init; }
    public bool DriverResponding { get; init; }
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class WinUHidSetupService
{
    public const string GitHubRepoUrl = "https://github.com/cgutman/WinUHid";
    public const string GitHubActionsUrl = "https://github.com/cgutman/WinUHid/actions";
    public const string ForkWithInstallerUrl = "https://github.com/lurebat/WinUHid";
    public const string ManualTestSigningCommand = "bcdedit /set testsigning on";

    public string AppDirectory => AppContext.BaseDirectory;
    public string LocalDllPath => Path.Combine(AppDirectory, "WinUHid.dll");

    /// <summary>
    /// True when a Root\WinUHid / ROOT\WINUHID PnP node exists (even if the user-mode probe fails).
    /// </summary>
    public bool IsDeviceNodePresent()
    {
        if (RegistryKeyExists(@"SYSTEM\CurrentControlSet\Enum\ROOT\WINUHID") ||
            RegistryKeyExists(@"SYSTEM\CurrentControlSet\Enum\ROOT\WinUHid"))
            return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-devices /deviceid Root\\WinUHid",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            if (string.IsNullOrWhiteSpace(output) || output.Contains("No devices were found", StringComparison.OrdinalIgnoreCase))
                return false;
            return output.Contains("WINUHID", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("WinUHid", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool RegistryKeyExists(string relativeHkLmPath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(relativeHkLmPath);
            return key is not null;
        }
        catch { return false; }
    }

    public WinUHidSetupStatus GetStatus(Func<bool> probeDriver)
    {
        var testSigning = IsTestSigningEnabled();
        var dll = File.Exists(LocalDllPath);
        var driver = false;
        var detail = "";
        try
        {
            driver = probeDriver();
        }
        catch (Exception ex)
        {
            detail = ex.Message;
        }

        var parts = new List<string>
        {
            testSigning ? "Test signing: ON" : "Test signing: OFF (needed for test-signed driver)",
            dll ? "WinUHid.dll: found next to app" : "WinUHid.dll: missing",
            driver ? "Driver: responding" : "Driver: not available",
        };

        return new WinUHidSetupStatus
        {
            TestSigningEnabled = testSigning,
            DllPresent = dll,
            DriverResponding = driver,
            Summary = string.Join(" · ", parts),
            Detail = detail,
        };
    }

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsTestSigningEnabled()
    {
        // Prefer live Code Integrity flags - works without admin and reflects the
        // running boot config. Non-elevated bcdedit often fails with Access Denied
        // and produced false "OFF" readings after the user had already enabled it.
        if (TryQueryCodeIntegrityTestSigning(out var enabled))
            return enabled;

        return TryQueryBcdeditTestSigning();
    }

    /// <summary>
    /// Live boot state vs BCD next-boot config. Useful when test signing was staged but
    /// the machine has not rebooted yet (CI still OFF, bcdedit already Yes).
    /// </summary>
    public (bool LiveEnabled, bool? BcdConfigured) QueryTestSigningDetail()
    {
        var live = false;
        if (!TryQueryCodeIntegrityTestSigning(out live))
            live = TryQueryBcdeditTestSigning();

        bool? bcd = null;
        try
        {
            // Best-effort: non-elevated bcdedit may fail; leave null then.
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
                Arguments = "/enum {current}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                if (!output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = raw.Trim();
                        if (!line.StartsWith("testsigning", StringComparison.OrdinalIgnoreCase))
                            continue;
                        bcd = line.Contains("Yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return (live, bcd);
    }

    private static bool TryQueryCodeIntegrityTestSigning(out bool enabled)
    {
        enabled = false;
        try
        {
            var info = new SystemCodeIntegrityInformation
            {
                Length = (uint)Marshal.SizeOf<SystemCodeIntegrityInformation>(),
            };
            var status = NtQuerySystemInformation(
                SystemCodeIntegrityInformationClass,
                ref info,
                Marshal.SizeOf(info),
                out _);
            if (status != 0)
                return false;

            enabled = (info.CodeIntegrityOptions & CodeIntegrityOptionTestSign) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryQueryBcdeditTestSigning()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
                Arguments = "/enum {current}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (!line.StartsWith("testsigning", StringComparison.OrdinalIgnoreCase))
                    continue;
                return line.Contains("Yes", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private const int SystemCodeIntegrityInformationClass = 0x67;
    private const uint CodeIntegrityOptionTestSign = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCodeIntegrityInformation
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        ref SystemCodeIntegrityInformation systemInformation,
        int systemInformationLength,
        out int returnLength);

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public void OpenAppDirectory() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{AppDirectory}\"",
            UseShellExecute = true,
        });

    public string InstallDllFromFolder(string folder)
    {
        var source = FindFile(folder, "WinUHid.dll")
                     ?? throw new FileNotFoundException("WinUHid.dll not found in the selected folder (searched recursively).");
        Directory.CreateDirectory(AppDirectory);

        var dest = Path.GetFullPath(LocalDllPath);
        var src = Path.GetFullPath(source);

        // Already next to the EXE (common when bundled) - do not overwrite a loaded DLL.
        if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            return dest;

        try
        {
            File.Copy(src, dest, overwrite: true);
        }
        catch (IOException) when (File.Exists(dest))
        {
            // App keeps WinUHid.dll loaded via P/Invoke; copy is unnecessary if present.
        }

        var pdb = Path.ChangeExtension(src, ".pdb");
        if (File.Exists(pdb))
        {
            try
            {
                File.Copy(pdb, Path.ChangeExtension(dest, ".pdb"), overwrite: true);
            }
            catch (IOException)
            {
                // ignore locked pdb
            }
        }

        return dest;
    }

    public PackageInfo? DetectPackage(string folder)
    {
        var dll = FindFile(folder, "WinUHid.dll");
        var inf = FindFile(folder, "WinUHidDriver.inf");
        var driverDll = FindFile(folder, "WinUHidDriver.dll");
        var cat = FindFile(folder, "WinUHidDriver.cat");
        var cer = FindFile(folder, "WinUHidCertificate.cer")
                  ?? FindFile(folder, "*.cer");

        if (dll is null && inf is null)
            return null;

        return new PackageInfo(folder, dll, inf, driverDll, cat, cer);
    }

    public string EnableTestSigningElevated()
    {
        // PowerShell mis-parses `bcdedit /set ...` unless called as & bcdedit.exe
        var script = """
$ErrorActionPreference = 'Continue'
$log = Join-Path $env:TEMP 'g920emulator-testsigning.log'
function Write-Log([string]$msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
  Add-Content -LiteralPath $log -Value $line
  Write-Host $line
}
try {
  Set-Content -LiteralPath $log -Value 'G920 Emulator test-signing log' -Encoding UTF8
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Not running elevated. Approve the UAC prompt.'
  }
  $bcd = Join-Path $env:SystemRoot 'System32\bcdedit.exe'
  if (-not (Test-Path -LiteralPath $bcd)) { throw "bcdedit.exe not found at $bcd" }
  Write-Log "Running: $bcd /set testsigning on"
  & $bcd /set testsigning on
  $code = $LASTEXITCODE
  Write-Log "bcdedit exit code: $code"
  if ($code -ne 0) {
    Write-Log 'Retrying with explicit {current}...'
    & $bcd /set '{current}' testsigning on
    $code = $LASTEXITCODE
    Write-Log "bcdedit {current} exit code: $code"
  }
  if ($code -ne 0) {
    throw "bcdedit failed (exit code $code). If the log mentions Secure Boot, disable Secure Boot in UEFI/BIOS first, then retry."
  }
  Write-Log 'SUCCESS: test signing enabled. Reboot required.'
  exit 0
} catch {
  Write-Log $_.Exception.Message
  Write-Host ''
  Write-Host $_.Exception.Message -ForegroundColor Yellow
  Write-Host ''
  Write-Host 'Press Enter to close...'
  [void][Console]::ReadLine()
  exit 1
}
""";

        var result = RunElevatedScript(script);
        if (!result.Success)
        {
            var logTail = ReadLogTail(Path.Combine(Path.GetTempPath(), "g920emulator-testsigning.log"));
            var detail = string.IsNullOrWhiteSpace(logTail) ? result.Message : logTail;
            if (detail.Contains("Secure Boot", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Secure Boot is blocking test signing (needed only to install WinUHid).\n\n" +
                    "Do this:\n" +
                    "1. Restart PC → enter UEFI/BIOS (often Del, F2, or F10 at boot)\n" +
                    "2. Disable Secure Boot (Security / Boot menu)\n" +
                    "3. Save & exit, boot into Windows\n" +
                    "4. Install WinUHid (enables test signing, installs driver, turns test signing off)\n" +
                    "5. Reboot when prompted, then Recheck - do not Install again\n" +
                    "6. Optional: re-enable Secure Boot in UEFI/BIOS (WinUHid and Forza keep working)\n\n" +
                    "Admin CMD alone cannot override Secure Boot.\n\n" +
                    detail);
            }

            throw new InvalidOperationException(
                "Could not enable test signing automatically.\n\n" +
                "Do this manually:\n" +
                "1. Start menu → type cmd → right-click Command Prompt → Run as administrator\n" +
                "2. Run:\n   bcdedit /set testsigning on\n" +
                "3. If it says protected by Secure Boot: disable Secure Boot in UEFI/BIOS, then retry\n" +
                "4. Reboot Windows\n" +
                "5. Return here and click Recheck status\n\n" +
                detail);
        }

        return "Test signing enabled. Reboot Windows, then reopen G920 Emulator and continue setup.";
    }

    /// <summary>
    /// Turns Windows test signing off (needed to launch Forza Horizon 6 after using WinUHid).
    /// Reboot required before the change takes effect.
    /// </summary>
    public string DisableTestSigningElevated()
    {
        var script = """
$ErrorActionPreference = 'Continue'
$log = Join-Path $env:TEMP 'g920emulator-testsigning-off.log'
function Write-Log([string]$msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
  Add-Content -LiteralPath $log -Value $line
  Write-Host $line
}
try {
  Set-Content -LiteralPath $log -Value 'G920 Emulator test-signing OFF log' -Encoding UTF8
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Not running elevated. Approve the UAC prompt.'
  }
  $bcd = Join-Path $env:SystemRoot 'System32\bcdedit.exe'
  if (-not (Test-Path -LiteralPath $bcd)) { throw "bcdedit.exe not found at $bcd" }
  Write-Log "Running: $bcd /set '{current}' testsigning off"
  & $bcd /set '{current}' testsigning off
  $code = $LASTEXITCODE
  Write-Log "bcdedit exit code: $code"
  if ($code -ne 0) {
    Write-Log 'Retrying without {current}...'
    & $bcd /set testsigning off
    $code = $LASTEXITCODE
    Write-Log "bcdedit exit code: $code"
  }
  if ($code -ne 0) {
    throw "bcdedit failed (exit code $code)."
  }
  Write-Log 'SUCCESS: test signing disabled. Reboot required.'
  exit 0
} catch {
  Write-Log $_.Exception.Message
  Write-Host $_.Exception.Message
  exit 1
}
""";

        var result = RunElevatedScript(script);
        if (!result.Success)
        {
            var logTail = ReadLogTail(Path.Combine(Path.GetTempPath(), "g920emulator-testsigning-off.log"));
            var detail = string.IsNullOrWhiteSpace(logTail) ? result.Message : logTail;
            throw new InvalidOperationException(
                "Could not turn off Windows test signing.\n\n" +
                "Do this manually in Admin Command Prompt:\n" +
                "  bcdedit /set testsigning off\n" +
                "Then reboot Windows.\n\n" +
                detail);
        }

        return "Test signing turned off. Reboot Windows, then launch Forza Horizon 6. " +
               "The virtual G920 will not work until you Install WinUHid again (that turns test signing back on).";
    }

    public void InstallPackageElevated(PackageInfo package)
    {
        if (package.UserDllPath is not null)
            InstallDllFromFolder(Path.GetDirectoryName(package.UserDllPath)!);

        if (package.InfPath is null || package.DriverDllPath is null)
            throw new InvalidOperationException(
                "Driver package incomplete. Need WinUHidDriver.inf and WinUHidDriver.dll (from a WinUHid build/CI artifact).");

        var result = RunElevatedScript(BuildInstallScript(package));
        if (!result.Success)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "g920emulator-winuhid-install.log");
            var logTail = ReadLogTail(logPath, maxChars: 2500);
            var detail = string.IsNullOrWhiteSpace(logTail)
                ? (string.IsNullOrWhiteSpace(result.Message) ? $"Exit code {result.ExitCode}." : result.Message)
                : logTail;

            throw new InvalidOperationException(
                "WinUHid driver install did not finish.\n\n" +
                "Checklist:\n" +
                "1. Approve UAC when prompted\n" +
                "2. Test signing ON (and reboot after enabling it)\n" +
                "3. If Secure Boot blocks test signing, disable Secure Boot in BIOS, then retry\n" +
                "4. Click Install WinUHid again\n\n" +
                $"Log: {logPath}\n\n" +
                detail);
        }
    }

    public void UninstallPackageElevated()
    {
        var result = RunElevatedScript(BuildUninstallScript());
        if (!result.Success)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "g920emulator-winuhid-uninstall.log");
            var logTail = ReadLogTail(logPath, maxChars: 2500);
            var detail = string.IsNullOrWhiteSpace(logTail)
                ? (string.IsNullOrWhiteSpace(result.Message) ? $"Exit code {result.ExitCode}." : result.Message)
                : logTail;

            throw new InvalidOperationException(
                "WinUHid uninstall did not finish.\n\n" +
                "Stop the bridge, approve UAC, then try again.\n\n" +
                $"Log: {logPath}\n\n" +
                detail);
        }
    }

    private static string BuildUninstallScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'g920emulator-winuhid-uninstall.log'");
        sb.AppendLine("Set-Content -LiteralPath $log -Value ('G920 Emulator WinUHid uninstall log ' + (Get-Date -Format o)) -Encoding UTF8");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -LiteralPath $log -Value $msg; Write-Host $msg }");
        sb.AppendLine("try {");
        sb.AppendLine("  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()");
        sb.AppendLine("  $principal = New-Object Security.Principal.WindowsPrincipal($identity)");
        sb.AppendLine("  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }");
        sb.AppendLine("  Write-Log 'Removing Root\\WinUHid / WinUHid PnP devices…'");
        sb.AppendLine("  $ids = @()");
        sb.AppendLine("  Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {");
        sb.AppendLine("    $_.InstanceId -like '*WinUHid*' -or $_.InstanceId -like '*WINUHID*' -or ($_.HardwareID -join ' ') -like '*WinUHid*'");
        sb.AppendLine("  } | ForEach-Object { $ids += $_.InstanceId }");
        sb.AppendLine("  foreach ($id in ($ids | Select-Object -Unique)) {");
        sb.AppendLine("    Write-Log ('pnputil /remove-device ' + $id)");
        sb.AppendLine("    & pnputil.exe /remove-device $id /subtree /force 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("    try { Remove-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    & pnputil.exe /remove-device 'ROOT\\WINUHID\\0000' /subtree /force 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("    & pnputil.exe /remove-device 'Root\\WinUHid' /subtree /force 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("  } catch { Write-Log \"pnputil remove-device: $($_.Exception.Message)\" }");
        sb.AppendLine("  Write-Log 'Enumerating published drivers for WinUHid…'");
        sb.AppendLine("  $enum = & pnputil.exe /enum-drivers 2>&1 | Out-String");
        sb.AppendLine("  $blocks = $enum -split '(?=Published Name:)'");
        sb.AppendLine("  foreach ($block in $blocks) {");
        sb.AppendLine("    if ($block -notmatch 'WinUHid' -and $block -notmatch 'winuhid') { continue }");
        sb.AppendLine("    if ($block -match 'Published Name:\\s*(\\S+)') {");
        sb.AppendLine("      $oem = $Matches[1]");
        sb.AppendLine("      Write-Log \"Deleting driver package $oem\"");
        sb.AppendLine("      & pnputil.exe /delete-driver $oem /uninstall /force 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  Write-Log 'SUCCESS: WinUHid uninstall finished (test signing unchanged).'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Log $_.Exception.Message");
        sb.AppendLine("  Write-Host $_.Exception.Message");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildInstallScript(PackageInfo package)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'g920emulator-winuhid-install.log'");
        sb.AppendLine("Set-Content -LiteralPath $log -Value ('G920 Emulator WinUHid install log ' + (Get-Date -Format o)) -Encoding UTF8");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -LiteralPath $log -Value $msg; Write-Host $msg }");
        sb.AppendLine("try {");
        sb.AppendLine("  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()");
        sb.AppendLine("  $principal = New-Object Security.Principal.WindowsPrincipal($identity)");
        sb.AppendLine("  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }");
        sb.AppendLine("  Write-Log (\"Windows: \" + [System.Environment]::OSVersion.VersionString)");
        sb.AppendLine("  Write-Log (\"pnputil: \" + (Get-Command pnputil.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source))");

        if (package.CertificatePath is not null)
        {
            sb.AppendLine($"  $cer = '{Escape(package.CertificatePath)}'");
            sb.AppendLine("  $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)");
            sb.AppendLine("  foreach ($storeName in @('Root','TrustedPublisher')) {");
            sb.AppendLine("    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')");
            sb.AppendLine("    $store.Open('ReadWrite')");
            sb.AppendLine("    try {");
            sb.AppendLine("      if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) { $store.Add($cert) }");
            sb.AppendLine("      Write-Log \"Certificate present in $storeName\"");
            sb.AppendLine("    } finally { $store.Close() }");
            sb.AppendLine("  }");
        }
        else
        {
            sb.AppendLine("  Write-Log 'No certificate file found; skipping cert install.'");
        }

        sb.AppendLine($"  $inf = '{Escape(package.InfPath!)}'");
        sb.AppendLine("  if (-not (Test-Path -LiteralPath $inf)) { throw \"INF not found: $inf\" }");
        sb.AppendLine("  Write-Log \"Registering driver package: $inf\"");
        sb.AppendLine("  & pnputil.exe /add-driver $inf /install 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("  Write-Log \"pnputil /add-driver exit: $LASTEXITCODE\"");

        // Always ensure the Root\WinUHid node exists (works on Win10 builds without /add-device).
        sb.AppendLine(GetWinUHidDeviceHelperScript());
        sb.AppendLine("  Install-WinUHidDeviceNode -InfPath $inf");
        sb.AppendLine("  Write-Log 'Refreshing driver binding...'");
        sb.AppendLine("  & pnputil.exe /add-driver $inf /install 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("  try { & pnputil.exe /scan-devices 2>&1 | ForEach-Object { Write-Log \"$_\" } } catch { Write-Log \"scan-devices: $($_.Exception.Message)\" }");
        sb.AppendLine("  Start-Sleep -Seconds 2");
        sb.AppendLine("  Remove-DuplicateWinUHidEnumerators");
        sb.AppendLine("  try {");
        sb.AppendLine("    Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {");
        sb.AppendLine("      $_.InstanceId -like '*WinUHid*' -or $_.InstanceId -like '*WINUHID*'");
        sb.AppendLine("    } | ForEach-Object {");
        sb.AppendLine("      Write-Log (\"PNP: \" + $_.InstanceId + ' status=' + $_.Status)");
        sb.AppendLine("      if ($_.Status -ne 'OK') {");
        sb.AppendLine("        try { Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch { Write-Log \"Enable-PnpDevice: $($_.Exception.Message)\" }");
        sb.AppendLine("  if (-not (Test-WinUHidDevicePresent)) {");
        sb.AppendLine("    throw \"WinUHid device node still missing after SetupAPI create. See log: $log\"");
        sb.AppendLine("  }");
        sb.AppendLine("  try { & pnputil.exe /restart-device 'ROOT\\WINUHID\\0000' 2>&1 | ForEach-Object { Write-Log \"$_\" } } catch {}");
        sb.AppendLine("  Write-Log 'SUCCESS: WinUHid device node ready.'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Log $_.Exception.Message");
        sb.AppendLine("  Write-Host $_.Exception.Message");
        sb.AppendLine("  Write-Host 'Press Enter to close...'");
        sb.AppendLine("  [void][Console]::ReadLine()");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// PowerShell helpers that create Root\WinUHid on any supported Windows 10/11 build.
    /// Prefer SetupAPI (always available); optionally use pnputil /add-device or devcon when present.
    /// </summary>
    private static string GetWinUHidDeviceHelperScript()
    {
        return """
  function Test-WinUHidDevicePresent {
    try {
      $enum = & pnputil.exe /enum-devices /deviceid 'Root\WinUHid' 2>$null | Out-String
      if ($enum -and $enum -notmatch 'PNPUTIL \[' -and ($enum -match 'Root\\WinUHid' -or $enum -match 'Instance ID:.*WinUHid')) { return $true }
    } catch {}
    try {
      $pnp = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        $_.InstanceId -like '*WINUHID*' -or $_.InstanceId -like '*WinUHid*'
      }
      if ($pnp) { return $true }
    } catch {}
    try {
      if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\WinUHid') { return $true }
    } catch {}
    return $false
  }

  function Remove-DuplicateWinUHidEnumerators {
    # SetupAPI DICD_GENERATE_ID can create ROOT\SYSTEM\#### clones alongside ROOT\WINUHID\0000.
    # Two enumerators make WinUHidGetDriverInterfaceVersion return error 2 (device not found).
    try {
      $nodes = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        ($_.InstanceId -like '*WINUHID*' -or $_.InstanceId -like '*WinUHid*' -or ($_.HardwareID -join ' ') -like '*WinUHid*') -and
        $_.InstanceId -notlike 'ROOT\WINUHID\*'
      }
      foreach ($n in $nodes) {
        Write-Log ('Removing duplicate WinUHid enumerator: ' + $n.InstanceId)
        & pnputil.exe /remove-device $n.InstanceId /subtree /force 2>&1 | ForEach-Object { Write-Log "$_" }
      }
    } catch { Write-Log "Remove-DuplicateWinUHidEnumerators: $($_.Exception.Message)" }
  }

  function Install-WinUHidDeviceNode {
    param([Parameter(Mandatory=$true)][string]$InfPath)

    if (Test-WinUHidDevicePresent) {
      Write-Log 'WinUHid device node already present - binding INF only (skip create to avoid duplicates).'
      & pnputil.exe /add-driver $InfPath /install 2>&1 | ForEach-Object { Write-Log "$_" }
      if (-not ('WinUHidDevNode' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinUHidDevNodeBind {
  [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string HardwareId, string FullInfPath,
    uint InstallFlags, out bool bRebootRequired);
}
'@
      }
      try {
        $reboot = $false
        [void][WinUHidDevNodeBind]::UpdateDriverForPlugAndPlayDevices([IntPtr]::Zero, 'Root\WinUHid', $InfPath, 5, [ref]$reboot)
        Write-Log 'UpdateDriverForPlugAndPlayDevices (existing node) done.'
      } catch { Write-Log "UpdateDriver existing: $($_.Exception.Message)" }
      Remove-DuplicateWinUHidEnumerators
      try { & pnputil.exe /restart-device 'ROOT\WINUHID\0000' 2>&1 | ForEach-Object { Write-Log "$_" } } catch {}
      return
    }

    $help = (& pnputil.exe /? 2>&1 | Out-String)
    if ($help -match '/add-device') {
      Write-Log 'Trying pnputil /add-device Root\WinUHid'
      & pnputil.exe /add-device 'Root\WinUHid' 2>&1 | ForEach-Object { Write-Log "$_" }
    }

    if (-not (Test-WinUHidDevicePresent)) {
      $devconPath = $null
      $devconCmd = Get-Command devcon.exe -ErrorAction SilentlyContinue
      if ($devconCmd) { $devconPath = $devconCmd.Source }
      if (-not $devconPath) {
        foreach ($pattern in @(
          'C:\Program Files (x86)\Windows Kits\10\Tools\*\x64\devcon.exe',
          'C:\Program Files\Windows Kits\10\Tools\*\x64\devcon.exe'
        )) {
          $found = Get-Item $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
          if ($found) { $devconPath = $found.FullName; break }
        }
      }
      if ($devconPath) {
        Write-Log "Trying devcon: $devconPath"
        & $devconPath install $InfPath 'Root\WinUHid' 2>&1 | ForEach-Object { Write-Log "$_" }
      }
    }

    # Only create via SetupAPI when no Root\WinUHid node exists yet.
    # Calling Create when one already exists spawns ROOT\SYSTEM\#### duplicates and breaks the user-mode probe.
    if (Test-WinUHidDevicePresent) {
      Write-Log 'Device present after pnputil/devcon - skip SetupAPI create.'
      Remove-DuplicateWinUHidEnumerators
      return
    }

    Write-Log 'Creating/binding Root\WinUHid via SetupAPI (works on all Windows 10/11 builds)'
    if (-not ('WinUHidDevNode' -as [type])) {
      # PowerShell Add-Type uses an older C# compiler - keep this C# 5 compatible (no => methods, no uint suffixes).
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
  const uint DIF_INSTALLDEVICE = 0x00000002;
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
      bool created = SetupDiCreateDeviceInfo(set, "WinUHid", ref classGuid, "WinUHid", IntPtr.Zero, DICD_GENERATE_ID, ref data);
      if (!created)
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

        SetupDiCallClassInstaller(DIF_INSTALLDEVICE, set, ref data);
      }

      bool reboot;
      uint flags = INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE;
      if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, "Root\\WinUHid", infPath, flags, out reboot))
      {
        int err = Marshal.GetLastWin32Error();
        return "UpdateDriverForPlugAndPlayDevices failed: " + err + ". Check test signing / Secure Boot.";
      }

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

    $setupResult = [WinUHidDevNode]::Create($InfPath)
    Write-Log "SetupAPI result: $setupResult"
    if ($setupResult -notmatch '^OK') { throw $setupResult }
  }
""";
    }

    private readonly record struct ElevatedResult(bool Success, int ExitCode, string Message);

    private static ElevatedResult RunElevatedScript(string scriptBody)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"g920emulator-winuhid-{Guid.NewGuid():N}.ps1");
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(tempScript, scriptBody, utf8Bom);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = true,
                Verb = "runas",
            };

            Process p;
            try
            {
                p = Process.Start(psi)
                    ?? throw new InvalidOperationException("Could not start elevated PowerShell.");
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException(
                    "UAC prompt was cancelled or elevation failed.\n\n" +
                    "Run manually in Admin Command Prompt:\n" +
                    "  bcdedit /set testsigning on\n" +
                    "Then reboot.");
            }

            using (p)
            {
                p.WaitForExit();
                if (p.ExitCode == 0)
                    return new ElevatedResult(true, 0, "OK");

                return new ElevatedResult(false, p.ExitCode, $"Elevated command failed (exit code {p.ExitCode}).");
            }
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* ignore */ }
        }
    }

    private static string ReadLogTail(string? logPath, int maxChars = 1200)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
                return "";
            var text = File.ReadAllText(logPath);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch
        {
            return "";
        }
    }

    private static string? FindFile(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string Escape(string path) => path.Replace("'", "''");
}

public sealed record PackageInfo(
    string RootFolder,
    string? UserDllPath,
    string? InfPath,
    string? DriverDllPath,
    string? CatPath,
    string? CertificatePath)
{
    public bool HasDriverPackage => InfPath is not null && DriverDllPath is not null;
    public bool HasUserDll => UserDllPath is not null;

    public string Describe()
    {
        var bits = new List<string>();
        if (HasUserDll) bits.Add("WinUHid.dll");
        if (HasDriverPackage) bits.Add("driver INF/DLL");
        if (CatPath is not null) bits.Add("CAT");
        if (CertificatePath is not null) bits.Add("certificate");
        return bits.Count == 0 ? "No usable files" : string.Join(", ", bits);
    }
}
