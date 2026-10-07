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

// Newtonsoft needs System.Security.Permissions on net48 sometimes — use LoadFromString after fixing refs
var plugins = Assembly.LoadFrom(Path.Combine(sh, "SimHub.Plugins.dll"));
Type[] TypesOf(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
}

var manType = TypesOf(plugins).First(t => t.FullName == "SimHub.Plugins.ExternalSims.ExternalSimManifest");
foreach (var p in manType.GetProperties().Where(p => p.Name.Contains("Signature") || p.Name.Contains("Telemetry") || p.Name == "Name"))
    Console.WriteLine(p.Name + " : " + p.PropertyType.Name);

var load = manType.GetMethod("LoadFromFile", BindingFlags.Public | BindingFlags.Static);
var simdef = @"G:\Projects\Github\G920Emulator\simhub\G920Telemetry.simdef";
try
{
    // Ensure System.Security.Permissions
    try { Assembly.LoadFrom(Path.Combine(sh, "System.Security.Permissions.dll")); } catch { }
    var man = load!.Invoke(null, new object[] { simdef });
    Console.WriteLine("GameSignature=" + manType.GetProperty("GameSignature")!.GetValue(man));
    Console.WriteLine("TelemetrySignature=" + manType.GetProperty("TelemetrySignature")!.GetValue(man));
    var def = manType.GetProperty("TelemetryDefinition")!.GetValue(man);
    var parserType = TypesOf(plugins).First(t => t.Name == "ExternalSimTelemetryParser");
    var parser = Activator.CreateInstance(parserType, man);
    var len = parserType.GetMethod("GetExpectedPacketLenght")!.Invoke(parser, new[] { man });
    Console.WriteLine("ExpectedPacketLength=" + len);
}
catch (Exception ex)
{
    Console.WriteLine("LOAD FAIL: " + ex);
    if (ex.InnerException != null) Console.WriteLine("INNER: " + ex.InnerException);
}
