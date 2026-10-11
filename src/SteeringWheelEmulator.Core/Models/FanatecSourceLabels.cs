namespace SteeringWheelEmulator.Core.Models;

/// <summary>
/// Friendly labels for Fanatec DI sources (PC Comp faces map Wheel→X, Accelerator→Y, …).
/// Stored SourceRef keys stay DI names (X/Y/Rz/Slider0); UI can show these labels.
/// </summary>
public static class FanatecSourceLabels
{
    public static bool LooksLikeFanatecDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;
        return deviceName.Contains("FANATEC", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Podium Wheel", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("ClubSport", StringComparison.OrdinalIgnoreCase);
    }

    public static string AxisLabel(string? axis, string? deviceName = null)
    {
        if (string.IsNullOrEmpty(axis))
            return axis ?? "";
        if (deviceName is not null && !LooksLikeFanatecDeviceName(deviceName))
            return axis;

        return axis.ToUpperInvariant() switch
        {
            "X" => "Wheel (X)",
            "Y" => "Accelerator (Y)",
            "RZ" => "Brake (Rz)",
            "SLIDER0" => "Clutch (Slider0)",
            "SLIDER1" => "Handbrake (Dial / Slider1)",
            "Z" => "Combined pedals (Z)",
            "RX" => "X Rotation (Rx)",
            "RY" => "Y Rotation (Ry)",
            _ => axis,
        };
    }

    /// <summary>DI button index is 0-based; Fanatec UIs usually show 1-based.</summary>
    public static string ButtonLabel(int diIndex, string? deviceName = null)
    {
        if (deviceName is not null && LooksLikeFanatecDeviceName(deviceName))
            return $"button {diIndex} (Fanatec #{diIndex + 1})";
        return $"button {diIndex}";
    }
}
