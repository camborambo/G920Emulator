using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Linq;

var path = @"C:\Program Files (x86)\SimHub\SimHub.Plugins.dll";
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var t in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(t);
    var ns = md.GetString(type.Namespace);
    if (!ns.Contains("ExternalSims", StringComparison.Ordinal)) continue;
    var name = md.GetString(type.Name);
    Console.WriteLine($"TYPE {ns}.{name}");
    foreach (var f in type.GetFields())
    {
        var field = md.GetFieldDefinition(f);
        Console.WriteLine($"  F {md.GetString(field.Name)}");
    }
    foreach (var p in type.GetProperties())
    {
        var prop = md.GetPropertyDefinition(p);
        Console.WriteLine($"  P {md.GetString(prop.Name)}");
    }
}
