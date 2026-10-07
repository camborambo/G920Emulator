using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Linq;

var simhub = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("sh", isCollectible: false);
alc.Resolving += (_, name) => {
    var p = Path.Combine(simhub, name.Name + ".dll");
    return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
};
var asm = alc.LoadFromAssemblyPath(Path.Combine(simhub, "SimHub.Plugins.dll"));

var binType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.BinaryDataType")!;
Console.WriteLine("=== BinaryDataType ===");
foreach (var n in Enum.GetNames(binType))
    Console.WriteLine($"  {n} = {(int)Enum.Parse(binType, n)}");

var manType = asm.GetType("SimHub.Plugins.ExternalSims.ExternalSimManifest")!;
var load = manType.GetMethod("LoadFromFile", BindingFlags.Public|BindingFlags.Static)
    ?? manType.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m => m.Name.Contains("Load") && m.GetParameters().Length==1);
Console.WriteLine("Load methods: " + string.Join(", ", manType.GetMethods(BindingFlags.Public|BindingFlags.Static).Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name)) + ")")));

var path = @"G:\Projects\Github\G920Emulator\simhub\G920Telemetry.simdef";
object? obj = null;
foreach (var m in manType.GetMethods(BindingFlags.Public|BindingFlags.Static))
{
    if (m.GetParameters().Length != 1) continue;
    if (m.GetParameters()[0].ParameterType != typeof(string)) continue;
    try { obj = m.Invoke(null, new object[]{ path }); if (obj != null) { Console.WriteLine("Loaded via " + m.Name); break; } } catch (Exception ex) { Console.WriteLine(m.Name + " fail: " + ex.InnerException?.Message); }
}
if (obj is null) { Console.WriteLine("Could not load simdef"); return; }

Console.WriteLine("PacketLength=" + manType.GetProperty("PacketLength")!.GetValue(obj));
Console.WriteLine("TelemetrySignature=" + manType.GetProperty("TelemetrySignature")!.GetValue(obj));
Console.WriteLine("GameSignature=" + manType.GetProperty("GameSignature")!.GetValue(obj));

var td = manType.GetProperty("TelemetryDefinition")!.GetValue(obj)!;
var fields = (System.Collections.IList)td.GetType().GetProperty("Fields")!.GetValue(td)!;
var offset = 55;
for (var i = 0; i < fields.Count; i++)
{
    var f = fields[i]!;
    var ft = f.GetType();
    object? name = ft.GetProperty("Name")?.GetValue(f) ?? ft.GetProperty("FieldType")?.GetValue(f);
    var bt = ft.GetProperty("BinaryType")?.GetValue(f)?.ToString();
    int size = -1;
    foreach (var iface in new[]{ ft }.Concat(ft.GetInterfaces()))
    {
        foreach (var mName in new[]{ "GetSize", "GetByteSize", "GetBinarySize", "Size" })
        {
            var m = iface.GetMethod(mName, BindingFlags.Public|BindingFlags.Instance|BindingFlags.FlattenHierarchy);
            if (m is null || m.GetParameters().Length != 0) continue;
            try { size = Convert.ToInt32(m.Invoke(f, null)); break; } catch {}
        }
        if (size >= 0) break;
        var sp = iface.GetProperty("Size") ?? iface.GetProperty("ByteSize");
        if (sp != null) { try { size = Convert.ToInt32(sp.GetValue(f)); break; } catch {} }
    }
    if (size < 0)
    {
        // try ComputePacketLength incremental by reflecting serializer
        size = bt switch { "Float32" => 4, "Float64" or "Double" => 8, "Byte" or "Boolean" or "Bool" => 1, "Int32" => 4, "UTF8String" => 8, _ => -1 };
        var fieldType = ft.GetProperty("FieldType")?.GetValue(f)?.ToString();
        if (size < 0 && fieldType != null)
            size = fieldType is "EngineIgnitionOn" or "Boolean" ? 1 : fieldType is "Gear" ? 8 : 4;
    }
    Console.WriteLine($"[{i}] off={offset,3} +{size,2} bt={bt ?? "-"} {name}");
    // dump interesting props for Gear / EngineRpm
    if (name?.ToString() is "Gear" or "EngineRpm" or "SurfaceRumble" or "EngineIgnitionOn")
    {
        foreach (var p in ft.GetProperties(BindingFlags.Public|BindingFlags.Instance))
        {
            try { var v = p.GetValue(f); if (v != null && v.ToString()!.Length < 80) Console.WriteLine($"      {p.Name}={v}"); } catch {}
        }
    }
    if (size > 0) offset += size;
}
Console.WriteLine($"computedEnd={offset}");
