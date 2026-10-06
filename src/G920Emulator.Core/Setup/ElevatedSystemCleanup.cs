using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Elevated undo for leftovers that need Administrator (Program Files rename, ProgramData, driver store).
/// Invoked by FullCleanRestore.
/// </summary>
public static class ElevatedSystemCleanup
{
    public static string RunForzaSafeCleanup()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "g920emulator-full-clean.log");
        var script = BuildScript(logPath);
        var result = RunElevated(script);
        var tail = ReadLogTail(logPath, 2000);
        if (!result.Success)
        {
            return "Elevated cleanup failed (approve UAC). " +
                   (string.IsNullOrWhiteSpace(tail) ? result.Message : tail);
        }

        return string.IsNullOrWhiteSpace(tail)
            ? "Elevated cleanup OK (hidpp restore, ProgramData SDK cache, DI C262 cache, WinUHid packages)."
            : "Elevated cleanup: " + Compact(tail);
    }

    private static string BuildScript(string logPath)
    {
        var log = Escape(logPath);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = '{log}'");
        sb.AppendLine("Set-Content -LiteralPath $log -Value ('G920 Emulator full-clean ' + (Get-Date -Format o)) -Encoding UTF8");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -LiteralPath $log -Value $msg; Write-Host $msg }");
        sb.AppendLine("try {");
        sb.AppendLine("  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()");
        sb.AppendLine("  $principal = New-Object Security.Principal.WindowsPrincipal($identity)");
        sb.AppendLine("  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }");

        // Restore Logitech DI FFB DLL renamed by older GHubConflictRepair builds.
        sb.AppendLine("  $ffbDir = Join-Path ${env:ProgramFiles} 'Logitech\\Direct Input Force Feedback\\1_1_13'");
        sb.AppendLine("  $active = Join-Path $ffbDir 'hidpp_forcefeedback_x64.dll'");
        sb.AppendLine("  $disabled = $active + '.g920emulator-disabled'");
        sb.AppendLine("  if ((Test-Path -LiteralPath $disabled) -and -not (Test-Path -LiteralPath $active)) {");
        sb.AppendLine("    Rename-Item -LiteralPath $disabled -NewName 'hidpp_forcefeedback_x64.dll' -Force");
        sb.AppendLine("    Write-Log 'restored hidpp_forcefeedback_x64.dll'");
        sb.AppendLine("  } elseif ((Test-Path -LiteralPath $disabled) -and (Test-Path -LiteralPath $active)) {");
        sb.AppendLine("    Remove-Item -LiteralPath $disabled -Force");
        sb.AppendLine("    Write-Log 'removed duplicate hidpp .g920emulator-disabled'");
        sb.AppendLine("  } else {");
        sb.AppendLine("    Write-Log ('hidpp active=' + (Test-Path -LiteralPath $active) + ' disabled=' + (Test-Path -LiteralPath $disabled))");
        sb.AppendLine("  }");

        // HKLM DirectInput cache only here. HKCU must be cleared in the unelevated
        // process (elevated PowerShell sees the admin user's HKCU, not the logged-on user).
        sb.AppendLine("  $hklmDi = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\MediaProperties\\PrivateProperties\\DirectInput\\VID_046D&PID_C262'");
        sb.AppendLine("  if (Test-Path -LiteralPath $hklmDi) {");
        sb.AppendLine("    Remove-Item -LiteralPath $hklmDi -Recurse -Force");
        sb.AppendLine("    Write-Log 'removed HKLM DI cache C262'");
        sb.AppendLine("  } else { Write-Log 'HKLM DI cache C262 already absent' }");

        // Private SDK cache left under ProgramData.
        sb.AppendLine("  $pd = Join-Path $env:ProgramData 'G920Emulator'");
        sb.AppendLine("  if (Test-Path -LiteralPath $pd) {");
        sb.AppendLine("    Remove-Item -LiteralPath $pd -Recurse -Force");
        sb.AppendLine("    Write-Log 'removed ProgramData\\G920Emulator'");
        sb.AppendLine("  } else { Write-Log 'ProgramData\\G920Emulator already absent' }");

        // Any published WinUHid packages still in the driver store.
        sb.AppendLine("  $enum = & pnputil.exe /enum-drivers 2>&1 | Out-String");
        sb.AppendLine("  foreach ($block in ($enum -split '(?=Published Name:)')) {");
        sb.AppendLine("    if ($block -notmatch 'WinUHid' -and $block -notmatch 'winuhid') { continue }");
        sb.AppendLine("    if ($block -match 'Published Name:\\s*(\\S+)') {");
        sb.AppendLine("      $oem = $Matches[1]");
        sb.AppendLine("      Write-Log ('delete-driver ' + $oem)");
        sb.AppendLine("      & pnputil.exe /delete-driver $oem /uninstall /force 2>&1 | ForEach-Object { Write-Log \"$_\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");

        // Test signing was enabled by Install WinUHid. Forza Horizon 6 exits (code 100) at the splash
        // while the OS boots in test mode, so a clean restore must turn it back off.
        sb.AppendLine("  & \"$env:SystemRoot\\System32\\bcdedit.exe\" /set '{current}' testsigning off 2>&1 | ForEach-Object { Write-Log \"testsigning off: $_\" }");

        sb.AppendLine("  Write-Log 'SUCCESS'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Log $_.Exception.Message");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (bool Success, string Message) RunElevated(string scriptBody)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"g920emulator-fullclean-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(tempScript, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                using var p = Process.Start(psi)
                    ?? throw new InvalidOperationException("Could not start elevated PowerShell.");
                p.WaitForExit();
                return p.ExitCode == 0
                    ? (true, "OK")
                    : (false, $"Exit code {p.ExitCode}");
            }
            catch (Win32Exception)
            {
                return (false, "UAC cancelled or elevation failed.");
            }
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* ignore */ }
        }
    }

    private static string ReadLogTail(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch { return ""; }
    }

    private static string Compact(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("G920 Emulator full-clean", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12));

    private static string Escape(string path) => path.Replace("'", "''");
}
