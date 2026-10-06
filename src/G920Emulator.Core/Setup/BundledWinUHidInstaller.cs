using System.Diagnostics;

namespace G920Emulator.Core.Setup;

/// <summary>
/// Installs WinUHid from files shipped next to the app (winuhid\), no downloads.
/// Expected package files:
///   WinUHid.dll
///   WinUHidDriver.dll (UMDF driver binary)
///   WinUHidDriver.inf
///   WinUHidDriver.cat (optional)
///   WinUHidCertificate.cer (optional but recommended)
/// </summary>
public sealed class BundledWinUHidInstaller
{
    private readonly WinUHidSetupService _setup = new();

    /// <summary>
    /// Marker written when test signing was staged but not yet live — after reboot,
    /// the next Install finishes the driver and turns test signing back off (Forza-safe).
    /// </summary>
    public static string PendingInstallMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "G920Emulator",
        "winuhid-install-pending");

    public static bool HasPendingInstall => File.Exists(PendingInstallMarkerPath);

    public string PackageDirectory
    {
        get
        {
            var besideApp = Path.Combine(AppContext.BaseDirectory, "winuhid");
            if (Directory.Exists(besideApp))
                return besideApp;

            // Dev fallback: repo native\winuhid
            var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "winuhid"));
            return Directory.Exists(dev) ? dev : besideApp;
        }
    }

    public PackageInfo? DetectBundledPackage()
    {
        var dir = PackageDirectory;
        if (!Directory.Exists(dir))
            return null;
        return _setup.DetectPackage(dir);
    }

    public string DescribePackage()
    {
        var pkg = DetectBundledPackage();
        if (pkg is null)
            return $"No bundled WinUHid package found in:\n{PackageDirectory}";

        var missing = new List<string>();
        if (!pkg.HasUserDll) missing.Add("WinUHid.dll");
        if (pkg.InfPath is null) missing.Add("WinUHidDriver.inf");
        if (pkg.DriverDllPath is null) missing.Add("WinUHidDriver.dll");

        if (missing.Count == 0)
            return $"Bundled package ready ({pkg.Describe()}).\n{PackageDirectory}";

        return $"Bundled package incomplete — missing: {string.Join(", ", missing)}.\n{PackageDirectory}\nFound: {pkg.Describe()}";
    }

    public bool IsPackageComplete()
    {
        var pkg = DetectBundledPackage();
        return pkg is { HasUserDll: true, HasDriverPackage: true };
    }

    /// <summary>
    /// Forza-friendly install:
    /// 1) enable test signing if the current boot is not in test mode (may need a reboot first)
    /// 2) install the driver package while test signing is live
    /// 3) turn test signing back off (WinUHid UMDF usually keeps working; Forza can launch)
    /// 4) reboot so the OFF state takes effect
    /// </summary>
    public BundledInstallResult Install()
    {
        var pkg = DetectBundledPackage()
                  ?? throw new InvalidOperationException(
                      $"Bundled WinUHid package not found.\nExpected folder:\n{PackageDirectory}");

        if (!pkg.HasUserDll)
            throw new InvalidOperationException("Bundled package is missing WinUHid.dll.");

        // Always place user-mode DLL next to the EXE.
        var dllDir = Path.GetDirectoryName(pkg.UserDllPath!)!;
        _setup.InstallDllFromFolder(dllDir);

        if (!pkg.HasDriverPackage)
        {
            return new BundledInstallResult(
                Success: false,
                NeedsReboot: false,
                Message: "WinUHid.dll was copied, but the driver package (WinUHidDriver.inf + WinUHidDriver.dll) is not bundled yet. Rebuild after the driver is built into native\\winuhid.");
        }

        // Phase A: need a live test-signing boot before the test-signed package can install.
        if (!_setup.IsTestSigningEnabled())
        {
            _setup.EnableTestSigningElevated();
            if (!_setup.IsTestSigningEnabled())
            {
                WritePendingMarker();
                return new BundledInstallResult(
                    Success: true,
                    NeedsReboot: true,
                    Message: "Windows test signing is staged (needed only to install WinUHid).\n\n" +
                             "Secure Boot must already be off in UEFI/BIOS or this will not take effect.\n\n" +
                             "Reboot once, then open the app and click Install WinUHid again.\n" +
                             "That second pass installs the driver and turns test signing back off (Forza-safe).");
            }
        }

        // Phase B: live test mode — install driver, then leave test mode for Forza.
        _setup.InstallPackageElevated(pkg);

        string disableNote;
        try
        {
            disableNote = _setup.DisableTestSigningElevated();
        }
        catch (Exception ex)
        {
            ClearPendingMarker();
            return new BundledInstallResult(
                Success: true,
                NeedsReboot: true,
                Message: "WinUHid installed, but turning off test signing failed:\n\n" + ex.Message +
                         "\n\nUse Dependencies → Disable test signing, then reboot so Forza Horizon 6 can launch.\n" +
                         "WinUHid usually keeps working with test signing off.");
        }

        ClearPendingMarker();
        return new BundledInstallResult(
            Success: true,
            NeedsReboot: true,
            Message: "WinUHid installed. Test signing was turned back off (only needed for install).\n\n" +
                     "Reboot once. After reboot:\n" +
                     "• WinUHid should still work (Recheck — do not Install again)\n" +
                     "• Forza Horizon 6 can launch (test mode off)\n" +
                     "• You may re-enable Secure Boot in UEFI/BIOS (optional)\n\n" +
                     disableNote);
    }

    /// <summary>
    /// Removes Root\WinUHid device node(s) and the published WinUHid driver package.
    /// When <paramref name="disableTestSigning"/> is true, also turns off Windows test signing
    /// (reboot required — needed for Forza Horizon 6).
    /// </summary>
    public BundledInstallResult Uninstall(bool disableTestSigning = true)
    {
        try
        {
            ClearPendingMarker();
            _setup.UninstallPackageElevated();
            if (!disableTestSigning)
            {
                return new BundledInstallResult(
                    Success: true,
                    NeedsReboot: false,
                    Message: "WinUHid device and driver package removed. Test signing was left unchanged.");
            }

            try
            {
                var ts = _setup.DisableTestSigningElevated();
                return new BundledInstallResult(
                    Success: true,
                    NeedsReboot: true,
                    Message: "WinUHid removed and test signing turned off. " + ts);
            }
            catch (Exception tsEx)
            {
                return new BundledInstallResult(
                    Success: false,
                    NeedsReboot: false,
                    Message: "WinUHid was removed, but turning off test signing failed:\n\n" + tsEx.Message);
            }
        }
        catch (Exception ex)
        {
            return new BundledInstallResult(
                Success: false,
                NeedsReboot: false,
                Message: ex.Message);
        }
    }

    private static void WritePendingMarker()
    {
        try
        {
            var dir = Path.GetDirectoryName(PendingInstallMarkerPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PendingInstallMarkerPath, "install-then-disable\n" + DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // non-fatal — user can still Install again after reboot
        }
    }

    private static void ClearPendingMarker()
    {
        try
        {
            if (File.Exists(PendingInstallMarkerPath))
                File.Delete(PendingInstallMarkerPath);
        }
        catch
        {
            // ignore
        }
    }
}

public readonly record struct BundledInstallResult(bool Success, bool NeedsReboot, string Message);
