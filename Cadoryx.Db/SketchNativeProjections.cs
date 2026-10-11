using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>An explicitly selected native source and its copied curve in the sketch's local plane.</summary>
public sealed record SketchNativeProjection(SketchEntityId TargetEntity,ExactTopologySelection Selection,
    GeometryAssetRef Source,RigidTransform3d TargetPlane,ImmutableArray<SketchConstraintId> Anchors);

public static class SketchNativeProjectionGeometry
{
    public static bool IsCurrent(DocumentSnapshot doc,CadSketch sketch,SketchNativeProjection p)=>
        p.Selection.DocumentId==doc.Id&&p.TargetPlane==sketch.Plane&&doc.Features.TryGetValue(p.Selection.FeatureId,out var feature)&&
        feature.PartId==sketch.PartId&&!feature.IsSuppressed&&!feature.IsStale&&feature.Result==p.Source;
    public static bool HasStaleInputs(DocumentSnapshot doc,SketchId id)
    {
        var seen=new HashSet<SketchId>();
        bool Visit(SketchId source)=>!doc.Sketches.TryGetValue(source,out var sketch)||seen.Add(source)&&
            (sketch.NativeProjections.Any(p=>!IsCurrent(doc,sketch,p))||sketch.Projections.Any(p=>Visit(p.SourceSketch)));
        return Visit(id);
    }
    public static void ValidateLocal(CadSketch sketch)
    {
        if(sketch.NativeProjections.IsDefault||sketch.NativeProjections.Length>256||
            sketch.NativeProjections.Select(p=>p.TargetEntity).Concat(sketch.Projections.Select(p=>p.TargetEntity)).Distinct().Count()!=
                sketch.NativeProjections.Length+sketch.Projections.Length)
            throw new CadValidationException("Invalid or duplicate native sketch projections.");
        var used=new HashSet<SketchConstraintId>(sketch.Projections.SelectMany(p=>p.Anchors));
        foreach(var p in sketch.NativeProjections)
        {
            CadGuard.Id(p.TargetEntity);p.Selection.Validate();p.Source.Validate();p.TargetPlane.Validate();
            if(p.Selection.Kind!=HistoryShapeKind.Edge||p.Selection.Asset!=p.Source.AssetId||p.Selection.Revision!=p.Source.Revision||
                p.Anchors.IsDefault||p.Anchors.Any(id=>!used.Add(id)))throw new CadValidationException("Invalid exact native projection source.");
            var points=SketchProjectionGeometry.Points(sketch,p.TargetEntity);
            if(sketch.Ellipses.All(e=>e.Id!=p.TargetEntity)&&sketch.Freeforms.All(e=>e.Id!=p.TargetEntity)&&sketch.Lines.All(l=>l.Id!=p.TargetEntity)||p.Anchors.Length!=points.Length)
                throw new CadValidationException("A native projection must retain its exact definition and coordinate anchors.");
            foreach(var (id,index) in points.Select((id,index)=>(id,index)))
            {
                var point=sketch.Points.Single(v=>v.Id==id);
                if(sketch.Constraints.SingleOrDefault(c=>c.Id==p.Anchors[index]) is not FixPointConstraint {IsEnabled:true} anchor||
                    anchor.Point!=id||double.Hypot(anchor.Position.X-point.Position.X,anchor.Position.Y-point.Position.Y)>1e-7)
                    throw new CadValidationException("Native projection coordinates are source-driven.");
            }
        }
    }
    public static void ValidateDrivenEdit(CadSketch before,CadSketch after)
    {
        foreach(var p in before.NativeProjections.Where(after.NativeProjections.Contains))
        {
            if(before.Freeforms.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} freeform&&
                !freeform.Geometry.SameValues(after.Freeforms.Single(e=>e.Id==p.TargetEntity).Geometry))
                throw SketchCurveFailure.Create("NativeProjectionDriven","Detach the native projection before editing its freeform definition.");
            if(before.Ellipses.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} ellipse&&
                ellipse.Geometry!=after.Ellipses.Single(e=>e.Id==p.TargetEntity).Geometry)
                throw new CadValidationException("Detach the native projection before editing its conic.");
            foreach(var id in SketchProjectionGeometry.Points(before,p.TargetEntity))
                if(before.Points.Single(v=>v.Id==id).Position!=after.Points.Single(v=>v.Id==id).Position)
                    throw new CadValidationException("Detach the native projection before editing its coordinates.");
            if(p.Anchors.Any(id=>before.Constraints.Single(c=>c.Id==id)!=after.Constraints.Single(c=>c.Id==id)))
                throw new CadValidationException("Native projection anchors cannot be edited or disabled.");
        }
    }
}
