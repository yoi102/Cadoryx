using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IGeometryInspector
{
    public Task<GeometryInspection> InspectAsync(IReadOnlyList<GeometryInstance> instances,IAssetStore assets,CancellationToken token=default)
    {
        // Copy inputs before queuing; the caller holds a snapshot asset lease until completion.
        var inputs=instances.ToArray();
        if(inputs.Length is < 1 or > 256)throw new CadValidationException("Select between 1 and 256 body instances for exact inspection.");
        foreach(var item in inputs){item.Geometry.Validate();item.WorldTransform.Validate();}
        return Run(()=>
        {
            var shapes=new List<Shape>();var results=ImmutableArray.CreateBuilder<BodyInspection>();
            try
            {
                foreach(var item in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    using var local=OcctGeometryBridge.ReadShape(item.Geometry,assets);
                    using var transform=OcctGeometryBridge.ToNative(item.WorldTransform);
                    var shape=local.Transformed(transform);shapes.Add(shape);
                    var counts=shape.GetTopologySummary().UniqueCounts;
                    double area=0;var faces=shape.GetFaces();
                    try{foreach(var face in faces){token.ThrowIfCancellationRequested();area+=Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass);}}
                    finally{foreach(var face in faces)face.Dispose();}
                    // A mixed compound's volume is not a meaningful closed-solid mass property.
                    double? volume=null;Vector3d? center=null;
                    if(counts.SolidCount==1&&item.Geometry.Kind==BodyKind.Solid)
                    {
                        var p=shape.InspectProperties(InspectionPropertyKind.Volume);
                        volume=Math.Abs(p.Mass);if(volume>1e-15)center=Point(p.CenterOfMass);
                    }
                    results.Add(new(item.Path,item.BodyId,area,volume,center,counts.FaceCount,counts.EdgeCount,counts.SolidCount));
                }
                MinimumDistance? distance=null;
                if(shapes.Count==2)
                {
                    token.ThrowIfCancellationRequested();var d=shapes[0].DistanceTo(shapes[1]);
                    distance=new(d.Distance,Point(d.PointOnFirst),Point(d.PointOnSecond),d.SolutionCount);
                }
                token.ThrowIfCancellationRequested();return new GeometryInspection(results.ToImmutable(),distance);
            }
            finally{foreach(var shape in shapes)shape.Dispose();}
        },token);
    }
    private static Vector3d Point(GpPoint p)=>new(p.X,p.Y,p.Z);
}
