using System.Reflection;
using System.Runtime.Loader;

var simhub = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("sh", isCollectible: false);
alc.Resolving += (_, name) =>
{
    var p = Path.Combine(simhub, name.Name + ".dll");
    return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
};
var asm = alc.LoadFromAssemblyPath(Path.Combine(simhub, "SimHub.Plugins.dll"));

var stdEnum = asm.GetType("SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardFieldType")!;
Console.WriteLine("=== RPM / Speed / Gear related StandardFieldType ===");
foreach (var n in Enum.GetNames(stdEnum).OrderBy(x => x))
{
    if (n.Contains("Rpm", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Gear", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Ignition", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Throttle", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Rumble", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Impact", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"  {n} = {(int)Enum.Parse(stdEnum, n)}");
}

var manType = asm.GetType("SimHub.Plugins.ExternalSims.ExternalSimManifest")!;
var obj = manType.GetMethod("CreateNew", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
manType.GetProperty("Name")!.SetValue(obj, "probe");
manType.GetProperty("UniqueId")!.SetValue(obj, Guid.Parse("8c4e2b1a-9f70-4d3e-b6a1-2d5c8e0f1734"));
manType.GetProperty("DefaultUDPPort")!.SetValue(obj, 20778);

var td = manType.GetProperty("TelemetryDefinition")!.GetValue(obj)!;
var fields = td.GetType().GetProperty("Fields")!.GetValue(td)!;
var stdType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardField")!;
var customType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.CustomFields.CustomTelemetryField")!;
var binType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.BinaryDataType")!;

void AddStd(string enumName)
{
    var f = Activator.CreateInstance(stdType)!;
    stdType.GetProperty("FieldType")!.SetValue(f, Enum.Parse(stdEnum, enumName));
    fields.GetType().GetMethod("Add")!.Invoke(fields, [f]);
}
void AddCustom(string name)
{
    var f = Activator.CreateInstance(customType)!;
    customType.GetProperty("Name")!.SetValue(f, name);
    customType.GetProperty("DisplayName")!.SetValue(f, name);
    customType.GetProperty("BinaryType")!.SetValue(f, Enum.Parse(binType, "Float32"));
    fields.GetType().GetMethod("Add")!.Invoke(fields, [f]);
}

foreach (var n in new[]
{
    "SpeedKmh", "EngineRpm", "EngineMaxRpm", "EngineIgnitionOn",
    "Throttle", "Brake", "Clutch", "Gear",
    "LocalSurgeMs2", "LocalSwayMs2", "LocalHeaveMs2"
})
    AddStd(n);
foreach (var n in new[]
{
    "Steering", "FfbConstant", "FfbSpring", "FfbDamper", "FfbPeriodic",
    "SurfaceRumble", "Impact", "RoadLoad"
})
    AddCustom(n);

Console.WriteLine("PacketLength=" + manType.GetProperty("PacketLength")!.GetValue(obj));
Console.WriteLine("TelemetrySignature=" + manType.GetProperty("TelemetrySignature")!.GetValue(obj));
Console.WriteLine("GameSignature=" + manType.GetProperty("GameSignature")!.GetValue(obj));

// Per-field size via ITelemetryField / GetSize
var list = (System.Collections.IList)fields;
var offset = 55;
for (var i = 0; i < list.Count; i++)
{
    var f = list[i]!;
    var ft = f.GetType();
    var name = ft.GetProperty("Name")?.GetValue(f) ?? ft.GetProperty("FieldType")?.GetValue(f);

    int size = -1;
    foreach (var mName in new[] { "GetSize", "GetByteSize", "GetBinarySize", "ComputeSize" })
    {
        var m = ft.GetMethod(mName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        if (m is null || m.GetParameters().Length != 0) continue;
        size = Convert.ToInt32(m.Invoke(f, null));
        break;
    }

    if (size < 0)
    {
        // interface?
        foreach (var iface in ft.GetInterfaces())
        {
            var m = iface.GetMethod("GetSize") ?? iface.GetMethod("GetByteSize");
            if (m is null || m.GetParameters().Length != 0) continue;
            size = Convert.ToInt32(m.Invoke(f, null));
            break;
        }
    }

    if (size < 0)
    {
        var bt = ft.GetProperty("BinaryType")?.GetValue(f)?.ToString();
        size = bt switch
        {
            "Float32" => 4,
            "Double" => 8,
            "Byte" => 1,
            "UTF8String" => GuessUtf8Size(f, ft),
            _ => -1
        };
        // Standard bool/float from FieldType
        var fieldType = ft.GetProperty("FieldType")?.GetValue(f)?.ToString();
        if (size < 0 && fieldType is not null)
        {
            size = fieldType switch
            {
                "EngineIgnitionOn" => 1,
                "Gear" => GuessUtf8Size(f, ft),
                _ => 4 // most standard numeric
            };
        }
    }

    Console.WriteLine($"[{i}] off={offset,3} +{size,2}  {name}");
    if (size > 0) offset += size;
}
Console.WriteLine($"end={offset} expectedPacket={manType.GetProperty("PacketLength")!.GetValue(obj)}");

// Probe UTF8String default length property on custom/standard
static int GuessUtf8Size(object f, Type ft)
{
    foreach (var p in ft.GetProperties())
    {
        if (p.Name is "StringSize" or "FixedLength" or "Length" or "Capacity" or "Utf8Size" or "MaxLength")
        {
            try
            {
                var v = p.GetValue(f);
                if (v is int i && i > 0) return i;
            }
            catch { /* ignore */ }
        }
    }
    // Default in many SimHub gens is 32; our writer uses 8 — print props for Gear
    Console.WriteLine("  props for " + ft.Name + ":");
    foreach (var p in ft.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        try { Console.WriteLine($"    {p.Name}={p.GetValue(f)}"); }
        catch { Console.WriteLine($"    {p.Name}=?"); }
    }
    return 8;
}
