using System.Reflection;

namespace G920Emulator.Core;

/// <summary>Package version from the executing assembly (Directory.Build.props).</summary>
public static class AppVersion
{
    public static string Display { get; } = Read();

    private static string Read()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        var version = asm.GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
