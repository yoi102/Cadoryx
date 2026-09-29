using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Pass a real STEP or IGES file path.");
var file=Path.GetFullPath(args[0]);
using var assets=new DiskAssetStore(Path.Combine(Path.GetTempPath(),"Cadoryx","M10ProbeAssets"));
var kernel=new OcctGeometryKernel();
using var loaded=await kernel.ImportAsync(file,assets);
var doc=loaded.Snapshot;var found=new List<object>();int attempted=0;
foreach(var occurrence in doc.EnumerateOccurrences().Where(o=>doc.Definitions[o.DefinitionId] is PartDefinition).Take(30))
{
    var part=(PartDefinition)doc.Definitions[occurrence.DefinitionId];
    foreach(var id in part.Bodies.Take(2))
    {
        var body=doc.Bodies[id];
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
        using var map=RepairSnapshot.Create(shape);
        foreach(var item in map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge))
        {
            using var subshape=map.CopySubshape(item.Selection);
            bool analytic=item.Kind==ShapeKind.Face?
                subshape.GetFaceSurfaceSnapshot().SurfaceType is SurfaceGeometryType.Plane or SurfaceGeometryType.Cylinder:
                subshape.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle;
            if(!analytic)continue;
            attempted++;
            try
            {
                var datum=await kernel.ResolveAssemblyDatumAsync(doc,occurrence.Path,id,item.Selection.Index,
                    map.Fingerprint,assets);
                if(!datum.IsCurrent(doc,occurrence.Path,occurrence.DefinitionId))
                    throw new InvalidOperationException("Extracted datum is not current.");
                found.Add(new{occurrence=occurrence.Path.ToString(),body=id.ToString(),datum.Geometry,
                    datum.FullTopologyIndex,datum.LocalPoint,datum.LocalAxis,datum.RadiusMm});
                break;
            }
            catch(CadValidationException){ /* Some trimmed analytic faces are degenerate. */ }
        }
        if(found.Count>=8)break;
    }
    if(found.Count>=8)break;
}
Console.WriteLine(JsonSerializer.Serialize(new{file,bytes=new FileInfo(file).Length,
    doc.Id,instances=doc.EnumerateOccurrences().Count(),attempted,verified=found.Count,datums=found},
    new JsonSerializerOptions{WriteIndented=true}));
if(found.Count==0)throw new InvalidOperationException("Real exchange model exposed no provable analytic datum.");
