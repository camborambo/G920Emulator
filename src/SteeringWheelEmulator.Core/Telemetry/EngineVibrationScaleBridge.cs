namespace SteeringWheelEmulator.Core.Telemetry;

/// <summary>
/// Publishes the Engine vibration scale for the SimHub RPM plugin (LocalAppData file).
/// RPM stays accurate in the UDP packet; the plugin applies this scale to ShakeIt force.
/// </summary>
public static class EngineVibrationScaleBridge
{
    public const string FileName = "engine-vibration-scale";

    public static string FilePath => Path.Combine(AppPaths.LocalAppDataRoot, FileName);

    private static float _lastWritten = float.NaN;

    public static void Publish(float scale)
    {
        scale = Math.Clamp(scale, 0f, 2f);
        if (!float.IsNaN(_lastWritten) && Math.Abs(_lastWritten - scale) < 0.0005f)
            return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            _lastWritten = scale;
        }
        catch
        {
            // best-effort; plugin falls back to 1.0
        }
    }

    public static float Read(float fallback = 1f)
    {
        try
        {
            if (!File.Exists(FilePath))
                return fallback;
            var text = File.ReadAllText(FilePath).Trim();
            if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return Math.Clamp(v, 0f, 2f);
        }
        catch
        {
            // ignore
        }

        return fallback;
    }
}
