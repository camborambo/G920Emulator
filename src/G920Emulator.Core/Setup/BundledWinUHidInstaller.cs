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
    /// One-shot install from bundled files:
    /// 1) enable test signing if needed (caller should reboot if this returns NeedsReboot)
    /// 2) copy WinUHid.dll next to the app
    /// 3) install driver package elevated
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

        if (!_setup.IsTestSigningEnabled())
        {
            _setup.EnableTestSigningElevated();
            // bcdedit returns success even when testsigning was already on.
            // Only ask for reboot if the running OS still has it off.
            if (!_setup.IsTestSigningEnabled())
            {
                return new BundledInstallResult(
                    Success: false,
                    NeedsReboot: true,
                    Message: "Test signing was enabled. Reboot Windows, then click Install WinUHid again to finish driver install.");
            }
        }

        _setup.InstallPackageElevated(pkg);
        return new BundledInstallResult(
            Success: true,
            NeedsReboot: false,
            Message: "WinUHid installed from the bundled package. Click Recheck — the driver should now respond.");
    }
}

public readonly record struct BundledInstallResult(bool Success, bool NeedsReboot, string Message);
