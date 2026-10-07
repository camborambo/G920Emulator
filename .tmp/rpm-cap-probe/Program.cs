using System.Reflection;
using System.Runtime.Loader;

var sh = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("probe", isCollectible: true);
alc.Resolving += (_, n) =>
{
    var p = Path.Combine(sh, n.Name + ".dll");
    return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
};
alc.LoadFromAssemblyPath(Path.Combine(sh, "GameReaderCommon.dll"));
alc.LoadFromAssemblyPath(Path.Combine(sh, "SimHub.Plugins.dll"));

Type[] AllTypes()
{
    var list = new List<Type>();
    foreach (var a in alc.Assemblies)
    {
        try { list.AddRange(a.GetTypes()); }
        catch (ReflectionTypeLoadException ex) { list.AddRange(ex.Types.Where(t => t != null)!); }
        catch { }
    }
    return list.ToArray();
}

var gf = AllTypes().First(t => t.Name == "GameFamily");
foreach (var v in Enum.GetValues(gf))
    Console.WriteLine($"{(int)v} {v}");

// Any JSON property on TelemetryBehaviour we aren't using?
var tb = AllTypes().First(t => t.FullName == "SimHub.Plugins.ExternalSims.TelemetryBehaviour");
foreach (var p in tb.GetProperties())
    Console.WriteLine("TB." + p.Name + " : " + p.PropertyType.Name);
