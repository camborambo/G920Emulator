using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteeringWheelEmulator.Core.Telemetry;

/// <summary>
/// Installs <c>SteeringWheelEmulator.SimHubPlugin.dll</c> into the SimHub folder and enables it
/// in <c>PluginsActivation.json</c> so built-in Engine vibrations can see RPM.
/// </summary>
public static class SimHubPluginInstaller
{
    public const string PluginFileName = "SteeringWheelEmulator.SimHubPlugin.dll";
    public const string PluginClassName = "SteeringWheelEmulator.SimHubPlugin.G920RpmCapabilityPlugin";
    // Pre-namespace-rename plugin (remove on install so SimHub does not load both).
    private const string LegacyPluginFileName = "G920Emulator.SimHubPlugin.dll";
    private const string LegacyPluginClassName = "G920Emulator.SimHubPlugin.G920RpmCapabilityPlugin";

    public static string BundledPluginPath =>
        Path.Combine(AppContext.BaseDirectory, "simhub", PluginFileName);

    public static string? FindSimHubInstallFolder()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SimHub"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SimHub"),
                 })
        {
            if (File.Exists(Path.Combine(candidate, "SimHubWPF.exe")) ||
                File.Exists(Path.Combine(candidate, "SimHub.Plugins.dll")))
                return candidate;
        }

        return null;
    }

    public static string? InstalledPluginPath
    {
        get
        {
            var root = FindSimHubInstallFolder();
            return root == null ? null : Path.Combine(root, PluginFileName);
        }
    }

    public static bool IsPluginInstalled
    {
        get
        {
            var path = InstalledPluginPath;
            return path != null && File.Exists(path);
        }
    }

    public static bool IsPluginEnabledInActivation
    {
        get
        {
            var act = ActivationJsonPath;
            if (act == null || !File.Exists(act))
                return false;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(act)) as JsonArray;
                if (root == null)
                    return false;
                foreach (var item in root)
                {
                    if (item is not JsonObject obj)
                        continue;
                    if (!string.Equals(obj["ClassName"]?.GetValue<string>(), PluginClassName, StringComparison.Ordinal))
                        continue;
                    return obj["IsEnabled"]?.GetValue<bool>() == true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }

    private static string? ActivationJsonPath
    {
        get
        {
            var root = FindSimHubInstallFolder();
            return root == null ? null : Path.Combine(root, "PluginsData", "PluginsActivation.json");
        }
    }

    public static (bool Ok, string Message) Install(string? sourceDllPath = null)
    {
        try
        {
            var simHub = FindSimHubInstallFolder();
            if (simHub == null)
                return (false, "SimHub install folder not found (expected under Program Files).");

            var source = string.IsNullOrWhiteSpace(sourceDllPath) ? BundledPluginPath : sourceDllPath!;
            source = Path.GetFullPath(source);
            if (!File.Exists(source))
                return (false, $"Bundled plugin not found: {source}");

            var dest = Path.Combine(simHub, PluginFileName);
            File.Copy(source, dest, overwrite: true);
            RemoveLegacyPlugin(simHub);

            var enableMsg = EnableInActivation();
            return (true, $"Installed {dest}. {enableMsg} Restart SimHub if it was open.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Uninstall()
    {
        try
        {
            var dest = InstalledPluginPath;
            if (dest != null && File.Exists(dest))
                File.Delete(dest);
            var simHub = FindSimHubInstallFolder();
            if (simHub != null)
                RemoveLegacyPlugin(simHub);

            DisableInActivation();
            return (true, "Removed Steering Wheel Emulator RPM plugin from SimHub. Restart SimHub.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string EnableInActivation()
    {
        var path = ActivationJsonPath;
        if (path == null)
            return "Could not locate PluginsActivation.json.";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonArray root;
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? [];
            }
            else
            {
                root = [];
            }

            // Drop pre-rename activation entries so only the new class remains.
            for (var i = root.Count - 1; i >= 0; i--)
            {
                if (root[i] is JsonObject obj &&
                    string.Equals(obj["ClassName"]?.GetValue<string>(), LegacyPluginClassName, StringComparison.Ordinal))
                {
                    root.RemoveAt(i);
                }
            }

            JsonObject? existing = null;
            foreach (var item in root)
            {
                if (item is JsonObject obj &&
                    string.Equals(obj["ClassName"]?.GetValue<string>(), PluginClassName, StringComparison.Ordinal))
                {
                    existing = obj;
                    break;
                }
            }

            if (existing == null)
            {
                existing = new JsonObject
                {
                    ["ClassName"] = PluginClassName,
                    ["IsEnabled"] = true,
                    ["ShowInMainMenu"] = false,
                    ["ShowInMainMenuPosition"] = 0,
                };
                root.Add(existing);
            }
            else
            {
                existing["IsEnabled"] = true;
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return "Plugin enabled in SimHub (PluginsActivation).";
        }
        catch (Exception ex)
        {
            return "Plugin DLL copied, but enabling failed: " + ex.Message +
                   " Enable 'Steering Wheel Emulator RPM' under SimHub → Settings → Plugins.";
        }
    }

    private static void DisableInActivation()
    {
        var path = ActivationJsonPath;
        if (path == null || !File.Exists(path))
            return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray root)
                return;
            foreach (var item in root)
            {
                if (item is JsonObject obj)
                {
                    var name = obj["ClassName"]?.GetValue<string>();
                    if (string.Equals(name, PluginClassName, StringComparison.Ordinal) ||
                        string.Equals(name, LegacyPluginClassName, StringComparison.Ordinal))
                    {
                        obj["IsEnabled"] = false;
                    }
                }
            }

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort
        }
    }

    private static void RemoveLegacyPlugin(string simHubFolder)
    {
        try
        {
            var legacy = Path.Combine(simHubFolder, LegacyPluginFileName);
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch
        {
            // best-effort (SimHub may lock the old DLL until restart)
        }
    }
}
