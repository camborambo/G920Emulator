using System.Reflection;

var sh = @"C:\Program Files (x86)\SimHub";
AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
{
    try
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        var p = Path.Combine(sh, name);
        return File.Exists(p) ? Assembly.LoadFrom(p) : null;
    }
    catch { return null; }
};

var grc = Assembly.LoadFrom(Path.Combine(sh, "GameReaderCommon.dll"));
Type[] TypesOf(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
}

var igm = TypesOf(grc).First(t => t.Name == "IGameManager");
foreach (var m in igm.GetMethods())
    if (m.Name.Contains("Feedback") || m.Name.Contains("Data"))
        Console.WriteLine(m.Name + " -> " + m.ReturnType.Name);

var gmb = TypesOf(grc).First(t => t.Name == "GameManagerBase`3");
foreach (var m in gmb.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
             .Where(m => m.Name.Contains("Feedback")))
    Console.WriteLine("GMB " + m.Name + " (" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);

// StatusDataBase RPM percent properties
var sdb = TypesOf(grc).First(t => t.Name == "StatusDataBase");
foreach (var p in sdb.GetProperties().Where(p => p.Name.Contains("RPM") || p.Name.Contains("Rpm")))
    Console.WriteLine("Status." + p.Name + " set=" + p.CanWrite);

// How External Sim exposes custom fields on GameData.NewData
var plugins = Assembly.LoadFrom(Path.Combine(sh, "SimHub.Plugins.dll"));
var est = TypesOf(plugins).FirstOrDefault(t => t.Name == "ExternalSimTelemetry");
Console.WriteLine("ExternalSimTelemetry=" + est);
if (est != null)
    foreach (var m in est.GetMethods().Where(m => m.Name.StartsWith("Get")).Take(15))
        Console.WriteLine("  " + m.Name);
