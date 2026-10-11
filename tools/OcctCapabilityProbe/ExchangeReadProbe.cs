using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

// Isolated diagnostic: each option set is a separate process. A failed native call
// leaves its last durable phase, without changing Cadoryx import defaults.
internal static class ExchangeReadProbe
{
    internal static int Run(string[] args)
    {
        if(args.Length!=3)throw new ArgumentException("Expected source STEP, evidence directory and all/no-colors mode.");
        string source=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
        bool colors=args[2] switch{"all"=>true,"no-colors"=>false,_=>throw new ArgumentException("Unknown diagnostic mode.")};
        Directory.CreateDirectory(output);var timer=Stopwatch.StartNew();
        void Phase(string phase,object? detail=null)=>File.AppendAllText(Path.Combine(output,"phases.jsonl"),
            JsonSerializer.Serialize(new{phase,elapsedMs=timer.Elapsed.TotalMilliseconds,utc=DateTimeOffset.UtcNow,detail})+Environment.NewLine);
        try
        {
            using(var stream=File.OpenRead(source))Phase("source",new{path=source,bytes=stream.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(stream)),colors});
            Phase("native-read-start");
            using var document=XdeDocument.ReadStep(source,new XdeStepReadOptions(ReadColors:colors));
            Phase("native-read-complete");var roots=document.GetFreeShapes();Phase("free-shapes",new{count=roots.Length});
            document.Save(Path.Combine(output,"context.xbf"));Phase("context-save-complete");
            var visited=new HashSet<string>();int parts=0,assemblies=0;
            void Inspect(XdeLabel label)
            {
                if(!visited.Add(label.Entry))return;
                Phase("definition-start",new{label.Entry,label.Name,label.IsAssembly});
                if(label.IsAssembly){assemblies++;foreach(var child in label.GetComponents())Inspect(child.ReferredShape);}
                else
                {
                    using var shape=label.Shape;Phase("shape-read",new{label.Entry});
                    var topology=shape.GetTopologySummary();Phase("shape-topology",new{label.Entry,topology.IsValid});
                    _=shape.GetBoundingBox();Phase("shape-bounds",new{label.Entry});
                    ShapeExchange.WriteBrep(shape,Path.Combine(output,"current-part.brep"));Phase("current-part-saved",new{label.Entry});
                    if(topology.UniqueCounts.SolidCount>0)_=shape.InspectProperties(InspectionPropertyKind.Volume);
                    Phase("shape-properties",new{label.Entry});parts++;
                }
            }
            foreach(var root in roots)Inspect(root);
            Phase("complete",new{parts,assemblies});
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,colors,parts,assemblies,totalMs=timer.Elapsed.TotalMilliseconds}));return 0;
        }
        catch(Exception error)
        {
            Phase("managed-failure",new{error=error.ToString()});File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString()}));return 1;
        }
    }
}
