using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

var simhub = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("sh", isCollectible: false);
alc.Resolving += (ctx, name) => {
    var p = Path.Combine(simhub, name.Name + "".dll"");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};
var asm = alc.LoadFromAssemblyPath(Path.Combine(simhub, ""SimHub.Plugins.dll""));
var binType = asm.GetType(""SimHub.Plugins.ExternalSims.Fields.BinaryDataType"")!;
Console.WriteLine(""BinaryDataType:"");
foreach (var n in Enum.GetNames(binType))
    Console.WriteLine($""  {(int)Enum.Parse(binType, n)} = {n}"");

var stdEnum = asm.GetType(""SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardFieldType"")!;
var stdType = asm.GetType(""SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardField"")!;
var wire = asm.GetType(""SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardFieldWireFormat"");
Console.WriteLine(""WireFormat type: "" + (wire?.FullName ?? ""null""));

// Load our simdef and print PacketLength / signatures
var manType = asm.GetType(""SimHub.Plugins.ExternalSims.ExternalSimManifest"")!;
var load = manType.GetMethod(""LoadFromFile"", BindingFlags.Public|BindingFlags.Static)
        ?? manType.GetMethods().FirstOrDefault(m => m.Name.Contains(""Load"") && m.GetParameters().Length==1);
Console.WriteLine(""Load methods:"");
foreach (var m in manType.GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance))
    if (m.Name.Contains(""Load"") || m.Name.Contains(""Save"") || m.Name.Contains(""Packet""))
        Console.WriteLine(""  "" + m);

var path = @""G:\Projects\Github\G920Emulator\simhub\G920Telemetry.simdef"";
object? man = null;
foreach (var m in manType.GetMethods(BindingFlags.Public|BindingFlags.Static))
{
    var ps = m.GetParameters();
    if (ps.Length==1 && ps[0].ParameterType==typeof(string) && m.ReturnType==manType)
    {
        man = m.Invoke(null, new object[]{path});
        Console.WriteLine(""Loaded via "" + m.Name);
        break;
    }
}
if (man is null)
{
    // try deserialize
    var json = File.ReadAllText(path);
    Console.WriteLine(""raw GameSignature from json parse via SaveToFile recreate"");
}
else
{
    Console.WriteLine(""GameSignature="" + manType.GetProperty(""GameSignature"")!.GetValue(man));
    Console.WriteLine(""TelemetrySignature="" + manType.GetProperty(""TelemetrySignature"")!.GetValue(man));
    Console.WriteLine(""PacketLength="" + manType.GetProperty(""PacketLength"")!.GetValue(man));
    var td = manType.GetProperty(""TelemetryDefinition"")!.GetValue(man)!;
    var fields = (System.Collections.IList)td.GetType().GetProperty(""Fields"")!.GetValue(td)!;
    int i=0;
    foreach (var f in fields)
    {
        var ft = f.GetType();
        var name = ft.GetProperty(""Name"")?.GetValue(f) ?? ft.GetProperty(""FieldType"")?.GetValue(f);
        var bt = ft.GetProperty(""BinaryType"")?.GetValue(f);
        var sizeProp = ft.GetProperty(""Size"") ?? ft.GetProperty(""ByteSize"") ?? ft.GetProperty(""WireSize"");
        Console.WriteLine($""[{i++}] {ft.Name} name={name} bin={bt}"");
        foreach (var p in ft.GetProperties())
            if (p.Name.Contains(""Size"") || p.Name.Contains(""Length"") || p.Name.Contains(""Wire""))
                Console.WriteLine($""      {p.Name}={p.GetValue(f)}"");
    }
}
