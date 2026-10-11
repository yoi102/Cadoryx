using System.Text.Json;
using OcctSharp;

internal static class ManufacturingReferenceProbe
{
    internal static int Run(string[] args)
    {
        if(args.Length!=1||File.Exists(args[0]))throw new ArgumentException("Expected a fresh result path.");
        var results=new List<object>();
        try
        {
            using var box=ShapeFactory.CreateBox(10,20,30);
            using var cylinder=ShapeFactory.CreateCylinder(5,20);
            using var imported=ShapeExchange.ReadBrep(Path.Combine(AppContext.BaseDirectory,"Fixtures","minipc3-difficult-trim-solid.brep"));
            foreach(var (name,shape) in new[]{("box",box),("cylinder",cylinder),("imported-freeform",imported)})
            {
                using var before=RepairSnapshot.Create(shape);int faces=0,edges=0,freeform=0;
                foreach(var item in before.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge))
                {
                    using var selected=before.CopySubshape(item.Selection);
                    var boundary=item.Kind==ShapeKind.Face?selected.GetSubShapes(ShapeKind.Edge):new[]{selected};
                    try
                    {
                        bool found=false;
                        foreach(var edge in boundary)
                        {
                            try
                            {
                                var range=edge.GetEdgeCurveSnapshot();
                                if(!double.IsFinite(range.FirstParameter)||!double.IsFinite(range.LastParameter)||range.LastParameter<=range.FirstParameter)continue;
                                double parameter=range.FirstParameter+(range.LastParameter-range.FirstParameter)/2;
                                var a=edge.EvaluateEdge(parameter);var b=edge.EvaluateEdge(parameter);
                                if(a!=b)throw new InvalidOperationException("Immutable edge evaluation changed.");
                                if(!double.IsFinite(a.Point.X+a.Point.Y+a.Point.Z))throw new InvalidOperationException("Nonfinite anchor.");
                                found=true;break;
                            }
                            catch(OcctException){ /* A degenerate boundary edge is not an attachment. */ }
                        }
                        if(!found)throw new InvalidOperationException("Fixture topology has no finite boundary attachment.");
                    }
                    finally{if(item.Kind==ShapeKind.Face)foreach(var edge in boundary)edge.Dispose();}
                    if(item.Kind==ShapeKind.Face){faces++;if(selected.GetFaceSurfaceSnapshot().SurfaceType is SurfaceGeometryType.BSplineSurface or SurfaceGeometryType.BezierSurface)freeform++;}
                    else edges++;
                }
                using var after=RepairSnapshot.Create(shape);
                if(before.Fingerprint!=after.Fingerprint)throw new InvalidOperationException("Attachment extraction modified the original BRep.");
                if(name=="imported-freeform"&&freeform==0)throw new InvalidOperationException("Fixture has no freeform face.");
                results.Add(new{name,faces,edges,freeform,unchangedFingerprint=before.Fingerprint});
            }
            File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=true,package=typeof(Shape).Assembly.GetName().Version?.ToString(),results}));return 0;
        }
        catch(Exception error){File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=false,error=error.ToString(),results}));return 1;}
    }
}
