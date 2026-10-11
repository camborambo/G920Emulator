namespace SteeringWheelEmulator.Core.Telemetry;

public static class SimHubRegistration
{
    public static string RegistrationsFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimHub", "ExternalSims", "Registrations");

    public static string DefaultSimDefPath =>
        Path.Combine(AppContext.BaseDirectory, "simhub", SimHubPacket.DefinitionFileName);

    public static string LinkFilePath =>
        Path.Combine(RegistrationsFolder, SimHubPacket.DefinitionUniqueId + ".simlink");

    public static (bool Ok, string Message) Register(string? simDefPath = null)
    {
        var path = string.IsNullOrWhiteSpace(simDefPath) ? DefaultSimDefPath : simDefPath;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return (false, $"SimHub definition not found: {path}");

            Directory.CreateDirectory(RegistrationsFolder);
            File.WriteAllText(LinkFilePath, path);

            var plugin = SimHubPluginInstaller.Install();
            var pluginNote = plugin.Ok
                ? " " + plugin.Message
                : " RPM plugin: " + plugin.Message;

            return (true, $"Registered {LinkFilePath}.{pluginNote}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static bool IsRegistered => File.Exists(LinkFilePath);

    /// <summary>
    /// Deletes our External Sim .simlink (and leftover .shlink). SimHub must be restarted to drop the game tile.
    /// </summary>
    public static (bool Ok, string Message) Unregister()
    {
        try
        {
            var removed = 0;
            foreach (var path in new[] { LinkFilePath, Path.ChangeExtension(LinkFilePath, ".shlink") })
            {
                if (!File.Exists(path))
                    continue;
                File.Delete(path);
                removed++;
            }

            var plugin = SimHubPluginInstaller.Uninstall();
            if (removed == 0 && plugin.Ok)
                return (true, "No External Sim registration found. " + plugin.Message);

            if (removed == 0)
                return (true, "No SimHub registration found.");

            return (true,
                "Removed SimHub registration. " + plugin.Message +
                " Restart SimHub, then Register again to pick up a new icon.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
