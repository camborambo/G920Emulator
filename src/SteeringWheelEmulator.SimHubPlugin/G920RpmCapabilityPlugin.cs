using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using GameReaderCommon;
using GameReaderCommon.Feedback;
using SimHub.Plugins;
using SimHub.Plugins.DataPlugins.ShakeItV3;

namespace SteeringWheelEmulator.SimHubPlugin;

/// <summary>
/// SimHub External Sims never set <see cref="FeedbackCapabilities.RPM"/>, so the built-in
/// ShakeIt "Engine vibrations" effect stays unavailable even when EngineRpm is in the packet.
/// This plugin patches that flag for the Steering Wheel Emulator game, refreshes ShakeIt, and applies
/// the Engine vibration scale from Steering Wheel Emulator (LocalAppData) to <see cref="FeedbackData.RPMPercent"/>.
/// </summary>
[PluginDescription("Enables built-in ShakeIt Engine vibrations for Steering Wheel Emulator and applies Engine scale.")]
[PluginAuthor("Steering Wheel Emulator")]
[PluginName("Steering Wheel Emulator RPM")]
public sealed class G920RpmCapabilityPlugin : IPlugin, IDataPlugin
{
    public const string PluginClassName = "SteeringWheelEmulator.SimHubPlugin.G920RpmCapabilityPlugin";

    private static readonly string ScaleFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteeringWheelEmulator",
        "engine-vibration-scale");

    private static readonly string LegacyScaleFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G920Emulator",
        "engine-vibration-scale");

    public PluginManager PluginManager { get; set; } = null!;

    private bool _capsPatched;
    private bool _shakeItRefreshed;
    private MethodInfo? _feedbackDataMethod;
    private int _warnCount;

    public void Init(PluginManager pluginManager)
    {
        _capsPatched = false;
        _shakeItRefreshed = false;
        _feedbackDataMethod = null;
        TryApplyCapabilities(pluginManager);
    }

    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        if (data == null || !IsOurGameName(data.GameName))
            return;

        if (!_capsPatched || !_shakeItRefreshed)
            TryApplyCapabilities(pluginManager);

        ApplyEngineVibrationScale(pluginManager, data);
    }

    public void End(PluginManager pluginManager)
    {
        _capsPatched = false;
        _shakeItRefreshed = false;
        _feedbackDataMethod = null;
    }

    private void TryApplyCapabilities(PluginManager pluginManager)
    {
        try
        {
            var gm = pluginManager?.GameManager;
            if (gm == null)
                return;

            var gameName = gm.GameDisplayName ?? SafeGameName(gm);
            if (!IsOurGameName(gameName))
                return;

            var capsField = FindCapabilitiesField(gm.GetType());
            if (capsField == null)
            {
                WarnOnce("no capabilities field on " + gm.GetType().FullName);
                return;
            }

            if (capsField.GetValue(gm) is not FeedbackCapabilities caps)
            {
                WarnOnce("capabilities field was null");
                return;
            }

            if (!caps.RPM)
                caps.RPM = true;

            if (!_capsPatched)
            {
                _capsPatched = true;
                SimHub.Logging.Current.Info(
                    "Steering Wheel Emulator RPM: FeedbackCapabilities.RPM enabled for '" + gameName + "'.");
            }

            if (!_shakeItRefreshed && pluginManager != null)
                _shakeItRefreshed = RefreshShakeIt(pluginManager, caps, gameName ?? "Steering Wheel Emulator");
        }
        catch (Exception ex)
        {
            WarnOnce(ex.Message);
        }
    }

    private void ApplyEngineVibrationScale(PluginManager pluginManager, GameData data)
    {
        try
        {
            if (data.NewData == null)
                return;

            var scale = ReadScale();
            var rpm = data.NewData.Rpms;
            var max = data.NewData.MaxRpm;
            if (max <= 1)
                max = 1;
            var pct = Math.Max(0.0, Math.Min(1.0, (rpm / max) * scale));

            // Keep dashboards showing true RPM%; scale only the ShakeIt feedback channel.
            var gm = pluginManager.GameManager;
            if (gm == null)
                return;

            _feedbackDataMethod ??= gm.GetType().GetMethod(
                "GD_FeedbackData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_feedbackDataMethod == null)
                return;

            if (_feedbackDataMethod.Invoke(gm, null) is FeedbackData fb)
                fb.RPMPercent = pct;
        }
        catch (Exception ex)
        {
            WarnOnce("scale: " + ex.Message);
        }
    }

    private static float ReadScale()
    {
        try
        {
            if (!File.Exists(ScaleFilePath))
                return 1f;
            var text = File.ReadAllText(ScaleFilePath).Trim();
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return Math.Max(0f, Math.Min(2f, v));
        }
        catch
        {
            // ignore
        }

        return 1f;
    }

    private static string? SafeGameName(IGameManager gm)
    {
        try { return gm.GameName(); }
        catch { return null; }
    }

    internal static bool IsOurGameName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name!.IndexOf("Steering Wheel Emulator", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("G920 Emulator", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("G920", StringComparison.OrdinalIgnoreCase) >= 0);

    private static FieldInfo? FindCapabilitiesField(Type type)
    {
        for (var t = type; t != null; t = t.BaseType!)
        {
            var field = t.GetField(
                "capabilities",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null && field.FieldType == typeof(FeedbackCapabilities))
                return field;
        }

        return null;
    }

    private static bool RefreshShakeIt(PluginManager pm, FeedbackCapabilities caps, string gameName)
    {
        var any = false;
        try
        {
            var motors = pm.GetPlugin<ShakeITMotorsV3Plugin>();
            if (motors?.Settings != null)
            {
                motors.Settings.SetGameCapabilities(caps, gameName);
                any = true;
            }
        }
        catch
        {
            // ShakeIt Motors may be disabled.
        }

        try
        {
            var bass = pm.GetPlugin<ShakeITBSV3Plugin>();
            if (bass?.Settings != null)
            {
                bass.Settings.SetGameCapabilities(caps, gameName);
                any = true;
            }
        }
        catch
        {
            // ShakeIt Bass Shakers may be disabled.
        }

        return any;
    }

    private void WarnOnce(string message)
    {
        if (_warnCount++ >= 3)
            return;
        try { SimHub.Logging.Current.Warn("Steering Wheel Emulator RPM: " + message); }
        catch { /* ignore logging failures */ }
    }
}
