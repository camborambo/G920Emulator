using System.IO;
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
var t = asm.GetType("SimHub.Plugins.ExternalSims.ExternalSimManifest")!;
var obj = t.GetMethod("CreateNew", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
t.GetProperty("Name")!.SetValue(obj, "G920 Emulator (estimated)");
t.GetProperty("UniqueId")!.SetValue(obj, Guid.Parse("8c4e2b1a-9f70-4d3e-b6a1-2d5c8e0f1734"));
t.GetProperty("DefaultUDPPort")!.SetValue(obj, 20778);
t.GetProperty("IconPath")!.SetValue(obj, "logo.png");

var det = t.GetProperty("DetectionProcesses")!.GetValue(obj)!;
var detType = asm.GetType("SimHub.Plugins.ExternalSims.ExternalSimProcessDetection")!;
void AddProc(string name)
{
    var p = Activator.CreateInstance(detType)!;
    detType.GetProperty("ProcessName")!.SetValue(p, name);
    det.GetType().GetMethod("Add")!.Invoke(det, [p]);
}
AddProc("NeedForSpeedHeat");
AddProc("NeedForSpeedUnbound");

var td = t.GetProperty("TelemetryDefinition")!.GetValue(obj)!;
var fields = td.GetType().GetProperty("Fields")!.GetValue(td)!;
var stdType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardField")!;
var stdEnum = asm.GetType("SimHub.Plugins.ExternalSims.Fields.StandardFields.StandardFieldType")!;
var customType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.CustomFields.CustomTelemetryField")!;
var binType = asm.GetType("SimHub.Plugins.ExternalSims.Fields.BinaryDataType")!;

void AddStd(string enumName)
{
    var f = Activator.CreateInstance(stdType)!;
    stdType.GetProperty("FieldType")!.SetValue(f, Enum.Parse(stdEnum, enumName));
    fields.GetType().GetMethod("Add")!.Invoke(fields, [f]);
}

void AddCustom(string name, string display, string comment)
{
    var f = Activator.CreateInstance(customType)!;
    customType.GetProperty("Name")!.SetValue(f, name);
    customType.GetProperty("DisplayName")!.SetValue(f, display);
    customType.GetProperty("Comment")!.SetValue(f, comment);
    customType.GetProperty("BinaryType")!.SetValue(f, Enum.Parse(binType, "Float32"));
    fields.GetType().GetMethod("Add")!.Invoke(fields, [f]);
}

foreach (var n in new[]
{
    "SpeedKmh",
    "EngineRpm",
    "EngineMaxRpm",
    "EngineShiftRpm",
    "EngineIgnitionOn",
    "EngineStarted",
    "Throttle",
    "Brake",
    "Clutch",
    "Gear",
    "LocalSurgeMs2",
    "LocalSwayMs2",
    "LocalHeaveMs2",
    "SuspensionVelocityFrontLeftMps",
    "SuspensionVelocityFrontRightMps",
    "SuspensionVelocityRearLeftMps",
    "SuspensionVelocityRearRightMps",
    "TyreContactSurfaceFrontLeft",
    "TyreContactSurfaceFrontRight",
    "TyreContactSurfaceRearLeft",
    "TyreContactSurfaceRearRight",
})
    AddStd(n);

AddCustom("Steering", "Steering", "Virtual G920 steering -1..1");
AddCustom("FfbConstant", "FFB Constant", "DI constant force mix -1..1");
AddCustom("FfbSpring", "FFB Spring", "DI spring mix -1..1");
AddCustom("FfbDamper", "FFB Damper", "DI damper mix -1..1");
AddCustom("FfbPeriodic", "FFB Periodic", "Sine/triangle/square/saw mix -1..1");
AddCustom("SurfaceRumble", "Surface rumble", "Vibration magnitude 0..1 (abs periodic types + custom force)");
AddCustom("Impact", "Impact", "Wall/hit pulse 0..1 from FFB spikes");
AddCustom("RoadLoad", "Road load", "Abs constant force + friction 0..1");

var tb = t.GetProperty("TelemetryBehaviour")!.GetValue(obj)!;
tb.GetType().GetProperty("DefaultMaxSpeed")!.SetValue(tb, 350.0);
tb.GetType().GetProperty("HasRumbleStripsContactSurface")!.SetValue(tb, true);

var outPath = @"G:\Projects\Github\G920Emulator\simhub\G920Telemetry.simdef";
t.GetMethod("SaveToFile")!.Invoke(obj, [outPath]);
Console.WriteLine("GameSignature=" + t.GetProperty("GameSignature")!.GetValue(obj));
Console.WriteLine("TelemetrySignature=" + t.GetProperty("TelemetrySignature")!.GetValue(obj));
Console.WriteLine("PacketLength=" + t.GetProperty("PacketLength")!.GetValue(obj));
Console.WriteLine("Wrote " + outPath);
