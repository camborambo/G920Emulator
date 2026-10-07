using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Buffers.Binary;
using System.Collections;
using G920Emulator.Core.Telemetry;

var simhub = @"C:\Program Files (x86)\SimHub";
var alc = new AssemblyLoadContext("sh", isCollectible: false);
alc.Resolving += (_, name) => {
    var p = Path.Combine(simhub, name.Name + ".dll");
    return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
};
var asm = alc.LoadFromAssemblyPath(Path.Combine(simhub, "SimHub.Plugins.dll"));
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
var manType = types.First(t => t.FullName == "SimHub.Plugins.ExternalSims.ExternalSimManifest");
var manifest = manType.GetMethod("LoadFromFile")!.Invoke(null, new object[]{ @"G:\Projects\Github\G920Emulator\simhub\G920Telemetry.simdef" })!;
Console.WriteLine("Loaded PacketLength=" + manType.GetProperty("PacketLength")!.GetValue(manifest));
Console.WriteLine("Loaded TelemetrySignature=" + manType.GetProperty("TelemetrySignature")!.GetValue(manifest));
var parserType = types.First(t => t.FullName!.EndsWith("ExternalSimTelemetryParser"));
var parser = Activator.CreateInstance(parserType, new[]{ manifest })!;
var frame = new TelemetryFrame(true,false,160.9f,5500f,8000f,7360f,true,true,0.75f,0.1f,0f,"3",5f,-2f,1f,0.2f,-0.15f,0.1f,-0.12f,2,2,1,1,0.25f,0.4f,0.1f,0.05f,0.6f,0.55f,0.8f,0.35f);
var buf = SimHubPacket.CreateBuffer();
SimHubPacket.Write(buf, frame, 1, 2, 3, 12.5);
var result = parserType.GetMethod("Parse", new[]{typeof(byte[])})!.Invoke(parser, new object[]{ buf })!;
Console.WriteLine("ParseStatus=" + result.GetType().GetProperty("ParseStatus")!.GetValue(result));
var tel = result.GetType().GetProperty("Telemetry")!.GetValue(result)!;
var std = (IDictionary)tel.GetType().GetProperty("Telemetry")!.GetValue(tel)!;
foreach (var key in new[]{"EngineRpm","EngineShiftRpm","EngineStarted","SuspensionVelocityFrontLeftMps","TyreContactSurfaceFrontLeft","LocalSurgeMs2"})
{
    foreach (DictionaryEntry e in std)
        if (e.Key.ToString() == key)
        {
            var v = e.Value!.GetType().GetProperty("Value")!.GetValue(e.Value);
            Console.WriteLine(key + "=" + v);
        }
}
