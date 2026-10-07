using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

var simhub = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("sh", false);
alc.Resolving += (_, n) =>
{
    var p = Path.Combine(simhub, n.Name + ".dll");
    return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
};
var plugins = alc.LoadFromAssemblyPath(Path.Combine(simhub, "SimHub.Plugins.dll"));
var pm = plugins.GetType("SimHub.Plugins.PluginManager")!;
foreach (var m in pm.GetMethods(BindingFlags.Public | BindingFlags.Instance)
             .Where(x => x.Name.Contains("Plugin", StringComparison.OrdinalIgnoreCase) ||
                         x.Name.Contains("Get", StringComparison.OrdinalIgnoreCase) && x.Name.Length < 40))
{
    var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
    if (m.Name.Contains("Plugin") || m.ReturnType.Name.Contains("Plugin"))
        Console.WriteLine($"{m.ReturnType.Name} {m.Name}({ps})");
}

Console.WriteLine("\nShakeIt plugin types:");
foreach (var t in plugins.GetTypes().Where(t =>
             (t.FullName ?? "").Contains("ShakeItV3") &&
             ((t.FullName ?? "").Contains("Plugin") || (t.Name.EndsWith("Plugin")))))
    Console.WriteLine("  " + t.FullName);
