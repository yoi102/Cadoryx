using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IGeometryReviewKernel
{
    public Task<GeometryResult> SectionAsync(IReadOnlyList<GeometryInstance> instances,CuttingPlane plane,IAssetStore assets,CancellationToken token=default)
    {
        plane.Validate();var inputs=ReviewInputs(instances,1,256);
        return Run(()=>
        {
            var curves=new List<Shape>();
            try
            {
                foreach(var input in inputs)
                {
                    token.ThrowIfCancellationRequested();using var shape=WorldShape(input,assets);
                    var b=shape.GetBoundingBox();var n=plane.Normal;
                    var helper=Math.Abs(n.Z)<.9?Vector3d.UnitZ:new Vector3d(1,0,0);
                    var u=n.Cross(helper).Normalized();var v=n.Cross(u).Normalized();var origin=n*plane.OffsetMm;
                    var projected=new List<(double U,double V)>();
                    foreach(double x in new[]{b.Minimum.X,b.Maximum.X})
                    foreach(double y in new[]{b.Minimum.Y,b.Maximum.Y})
                    foreach(double z in new[]{b.Minimum.Z,b.Maximum.Z})
                    {var p=new Vector3d(x,y,z)-origin;projected.Add((p.Dot(u),p.Dot(v)));}
                    double minU=projected.Min(p=>p.U),maxU=projected.Max(p=>p.U),minV=projected.Min(p=>p.V),maxV=projected.Max(p=>p.V);
                    double margin=Math.Max(1,Math.Max(maxU-minU,maxV-minV)*.01);
                    GpPoint P(double a,double c){var p=origin+u*a+v*c;return new(p.X,p.Y,p.Z);}
                    using var wire=ShapeFactory.CreatePolygonWire([P(minU-margin,minV-margin),P(maxU+margin,minV-margin),P(maxU+margin,maxV+margin),P(minU-margin,maxV+margin)],true);
                    using var face=ShapeFactory.CreatePlanarFace(wire);
                    curves.Add(shape.Section(face));
                }
                token.ThrowIfCancellationRequested();using var result=ShapeFactory.CreateCompound(curves);
                return OcctGeometryBridge.StoreShape(result,assets);
            }
            finally{foreach(var curve in curves)curve.Dispose();}
        },token);
    }
    public Task<InterferenceReport> CheckInterferenceAsync(IReadOnlyList<GeometryInstance> instances,double toleranceMm,IAssetStore assets,CancellationToken token=default)
    {
        var inputs=ReviewInputs(instances,2,32);
        if(!double.IsFinite(toleranceMm)||toleranceMm<=0||toleranceMm>1)
            throw new CadValidationException("Contact tolerance must be greater than zero and at most 1 mm.");
        if(inputs.Any(i=>i.Geometry.Kind!=BodyKind.Solid))throw new CadValidationException("Interference inspection requires single solid bodies; sheets, meshes and mixed compounds are not supported.");
        return Run(()=>
        {
            var shapes=new List<Shape>();
            try
            {
                foreach(var input in inputs){token.ThrowIfCancellationRequested();shapes.Add(WorldShape(input,assets));}
                var results=ImmutableArray.CreateBuilder<BodyPairFinding>();
                for(int i=0;i<inputs.Length;i++)for(int j=i+1;j<inputs.Length;j++)
                {
                    token.ThrowIfCancellationRequested();using var pair=shapes[i].InspectPair(shapes[j],toleranceMm);
                    var relation=pair.Classification switch
                    {
                        ShapePairClassification.Separated=>BodyPairRelation.Separated,
                        ShapePairClassification.Touching=>BodyPairRelation.Touching,
                        ShapePairClassification.Contained=>BodyPairRelation.Contained,
                        ShapePairClassification.Interfering=>BodyPairRelation.Interfering,
                        _=>throw new CadValidationException("Unknown native pair classification.")
                    };
                    results.Add(new(inputs[i],inputs[j],relation,pair.Distance,pair.OverlapVolume));
                }
                token.ThrowIfCancellationRequested();return new InterferenceReport(results.ToImmutable(),toleranceMm);
            }
            finally{foreach(var shape in shapes)shape.Dispose();}
        },token);
    }
    private static GeometryInstance[] ReviewInputs(IReadOnlyList<GeometryInstance> inputs,int minimum,int maximum)
    {
        var copy=inputs.ToArray();
        if(copy.Length<minimum||copy.Length>maximum)throw new CadValidationException($"Select {minimum}–{maximum} body instances.");
        if(copy.Select(i=>(i.Path,i.BodyId)).Distinct().Count()!=copy.Length)throw new CadValidationException("Duplicate review instance.");
        foreach(var input in copy){input.Geometry.Validate();input.WorldTransform.Validate();if(input.Geometry.Kind==BodyKind.Empty)throw new CadValidationException("Empty geometry cannot be reviewed.");}
        return copy;
    }
    private static Shape WorldShape(GeometryInstance input,IAssetStore assets)
    {
        using var local=OcctGeometryBridge.ReadShape(input.Geometry,assets);
        using var transform=OcctGeometryBridge.ToNative(input.WorldTransform);return local.Transformed(transform);
    }
}
