using SteeringWheelEmulator.Core.Profiles;

namespace SteeringWheelEmulator.Core.Models;

/// <summary>
/// Live HidHide Client state (cloak / apps / hidden devices) stored on an input profile.
/// Captured on Save; restored on Load (and on Start when apply mode is Off).
/// </summary>
public sealed class ProfileHidHideSnapshot
{
    public bool CloakOn { get; set; } = true;
    public bool InverseOn { get; set; }
    public List<string> Apps { get; set; } = [];
    public List<string> HiddenDevices { get; set; } = [];
}

/// <summary>
/// Slim session blob on an input profile — HidHide fields only (full fork session is out of scope).
/// </summary>
public sealed class ProfileSessionSettings
{
    public HidHideApplyMode HidHideApplyMode { get; set; } = HidHideApplyMode.Off;

    public bool UnloadHidHideConfigWhenStopped { get; set; }

    /// <summary>
    /// Exact HidHide device/app list from Client at Save time.
    /// Null on legacy profiles.
    /// </summary>
    public ProfileHidHideSnapshot? HidHideSnapshot { get; set; }

    public static ProfileSessionSettings CaptureFrom(AppSettings settings)
    {
        return new ProfileSessionSettings
        {
            HidHideApplyMode = settings.HidHideApplyMode,
            UnloadHidHideConfigWhenStopped = settings.UnloadHidHideConfigWhenStopped,
        };
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.HidHideApplyMode = HidHideApplyMode;
        settings.UnloadHidHideConfigWhenStopped = UnloadHidHideConfigWhenStopped;
        settings.NormalizeHidHide();
    }

    public void Normalize()
    {
        if (HidHideApplyMode is not (HidHideApplyMode.Off or HidHideApplyMode.HideAll or HidHideApplyMode.HideBound))
            HidHideApplyMode = HidHideApplyMode.Off;
        if (HidHideApplyMode == HidHideApplyMode.Off)
            UnloadHidHideConfigWhenStopped = false;
    }
}
