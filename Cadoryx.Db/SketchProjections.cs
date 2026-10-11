using System.Collections.Immutable;

namespace Cadoryx.Db;

public sealed record SketchProjection(SketchId SourceSketch,SketchEntityId SourceEntity,Guid SourceRevision,
    SketchEntityId TargetEntity,ImmutableArray<SketchConstraintId> Anchors);

/// <summary>Stable authored geometry references; no topology-order guessing or sampled curve projection.</summary>
public static class SketchProjectionGeometry
{
    public static ImmutableArray<SketchEntityId> Points(CadSketch sketch,SketchEntityId id)
    {
        if(sketch.Ellipses.Any(e=>e.Id==id)||sketch.Freeforms.Any(e=>e.Id==id))return [];
        if(sketch.Lines.SingleOrDefault(l=>l.Id==id) is {} l)return[l.Start,l.End];
        if(sketch.Circles.SingleOrDefault(c=>c.Id==id) is {} c)return[c.Center];
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==id) is {} a)return[a.Start,a.Middle,a.End];
        throw new CadValidationException("Projection requires an existing line, circle, circular arc or exact ellipse.");
    }
    public static IEnumerable<SketchEntityId> Entities(CadSketch sketch)=>SketchAnalyticalCurve.Ids(sketch).Concat(sketch.Ellipses.Select(e=>e.Id)).Concat(sketch.Freeforms.Select(e=>e.Id));
    public static SketchEllipseGeometry Conic(CadSketch sketch,SketchEntityId id)
    {
        if(sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} e)return e.Geometry;
        var arc=SketchAnalyticalCurve.From(sketch,id).Arc??throw new CadValidationException("Select an exact conic.");
        return new(arc.Center,new(arc.Radius,0),new(0,arc.Radius),arc.StartAngle,arc.SweepAngle);
    }
    public static void ValidateLocal(CadSketch sketch)
    {
        if(sketch.Projections.IsDefault||sketch.Projections.Length>256||sketch.Projections.Select(p=>p.TargetEntity).Distinct().Count()!=sketch.Projections.Length)
            throw new CadValidationException("Invalid sketch projections.");
        var allAnchors=new HashSet<SketchConstraintId>();
        foreach(var projection in sketch.Projections)
        {
            CadGuard.Id(projection.SourceSketch);CadGuard.Id(projection.SourceEntity);CadGuard.Id(projection.TargetEntity);
            if(projection.SourceSketch==sketch.Id||projection.SourceRevision==Guid.Empty||projection.Anchors.IsDefault)
                throw new CadValidationException("Invalid projection source or revision.");
            var points=Points(sketch,projection.TargetEntity);var circle=sketch.Circles.SingleOrDefault(c=>c.Id==projection.TargetEntity);
            if(projection.Anchors.Length!=points.Length+(circle is null?0:1)||projection.Anchors.Any(a=>!allAnchors.Add(a)))
                throw new CadValidationException("Invalid projection anchors.");
            for(int i=0;i<points.Length;i++)
            {
                var anchor=sketch.Constraints.SingleOrDefault(c=>c.Id==projection.Anchors[i]);
                var point=sketch.Points.Single(p=>p.Id==points[i]);
                if(anchor is not FixPointConstraint f||!f.IsEnabled||f.Point!=point.Id||!Near(f.Position,point.Position))
                    throw new CadValidationException("Projected coordinates are driven by their source; detach before editing.");
            }
            if(circle is not null&&(sketch.Constraints.SingleOrDefault(c=>c.Id==projection.Anchors[^1]) is not RadiusConstraint radius||
                !radius.IsEnabled||radius.Circle!=circle.Id||Math.Abs(radius.Radius-circle.Radius)>1e-7))
                throw new CadValidationException("Projected radius is driven by its source.");
        }
    }
    public static CadSketch Refresh(CadSketch target,IReadOnlyDictionary<SketchId,CadSketch> sketches,bool advance)
    {
        foreach(var p in target.Projections)
        {
            if(!sketches.TryGetValue(p.SourceSketch,out var source)||source.PartId!=target.PartId||source.Id==target.Id)
                throw new CadValidationException("Projected source is missing, foreign or self-referential.");
            if(!advance&&source.Revision!=p.SourceRevision)throw new CadValidationException("Projection source revision is stale.");
            if(target.Freeforms.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} freeform)
            {
                var geometry=source.Freeforms.SingleOrDefault(e=>e.Id==p.SourceEntity)?.Geometry??
                    throw new CadValidationException("Projected freeform source changed family; explicitly reselect it.");
                if(geometry.Kind!=freeform.Geometry.Kind)throw new CadValidationException("Projected freeform source changed family.");
                geometry=geometry.Project(target.Plane.Inverse()*source.Plane);
                target=target with{Freeforms=target.Freeforms.Replace(freeform,freeform with{Geometry=geometry}),
                    Projections=target.Projections.Replace(p,p with{SourceRevision=source.Revision})};
                target=SketchFixedCurveEndpoints.Synchronize(target,p.TargetEntity);continue;
            }
            if(target.Ellipses.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} ellipse)
            {
                var geometry=Conic(source,p.SourceEntity).Project(target.Plane.Inverse()*source.Plane);
                target=target with{Ellipses=target.Ellipses.Replace(ellipse,ellipse with{Geometry=geometry}),
                    Projections=target.Projections.Replace(p,p with{SourceRevision=source.Revision})};
                target=SketchFixedCurveEndpoints.Synchronize(target,p.TargetEntity);continue;
            }
            var sourcePoints=Points(source,p.SourceEntity);var targetPoints=Points(target,p.TargetEntity);
            bool sourceCircle=source.Circles.Any(c=>c.Id==p.SourceEntity),targetCircle=target.Circles.Any(c=>c.Id==p.TargetEntity);
            bool sourceArc=source.Arcs.Any(a=>a.Id==p.SourceEntity),targetArc=target.Arcs.Any(a=>a.Id==p.TargetEntity);
            if(sourceCircle!=targetCircle||sourceArc!=targetArc||sourcePoints.Length!=targetPoints.Length)
                throw new CadValidationException("Projection source type changed; reselect explicitly.");
            var transform=target.Plane.Inverse()*source.Plane;
            if((sourceCircle||sourceArc)&&Math.Abs(transform.Rotation.Rotate(Vector3d.UnitZ).Z)<1-1e-10)
                throw new CadValidationException("Circle and arc projection requires parallel planes; elliptical projection is unsupported.");
            var locations=sourcePoints.Select(id=>
            {
                var point=source.Points.Single(v=>v.Id==id).Position;var transformed=transform.Apply(new(point.X,point.Y,0));
                return new Point2d(transformed.X,transformed.Y);
            }).ToArray();
            var points=target.Points;var constraints=target.Constraints;
            for(int i=0;i<targetPoints.Length;i++)
            {
                var point=points.Single(v=>v.Id==targetPoints[i]);points=points.Replace(point,point with{Position=locations[i]});
                var anchor=constraints.Single(v=>v.Id==p.Anchors[i]);
                constraints=constraints.Replace(anchor,((FixPointConstraint)anchor) with{Position=locations[i]});
            }
            var circles=target.Circles;
            if(sourceCircle)
            {
                double radius=source.Circles.Single(c=>c.Id==p.SourceEntity).Radius;var circle=circles.Single(c=>c.Id==p.TargetEntity);
                circles=circles.Replace(circle,circle with{Radius=radius});var anchor=constraints.Single(c=>c.Id==p.Anchors[^1]);
                constraints=constraints.Replace(anchor,((RadiusConstraint)anchor) with{Radius=radius});
            }
            target=target with{Points=points,Circles=circles,Constraints=constraints,
                Projections=target.Projections.Replace(p,p with{SourceRevision=source.Revision})};
        }
        target=SketchExpressionEntities.Refresh(target);target.Validate();return target;
    }
    public static void ValidateGraph(IReadOnlyDictionary<SketchId,CadSketch> sketches)
    {
        foreach(var s in Order(sketches))
        {
            var expected=Refresh(s,sketches,false);
            foreach(var p in s.Projections)
            {
                if(s.Freeforms.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} freeform&&
                    !freeform.Geometry.SameValues(expected.Freeforms.Single(e=>e.Id==p.TargetEntity).Geometry))
                    throw new CadValidationException("Projected freeform cache differs from its source.");
                if(s.Ellipses.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} ellipse&&
                    !ellipse.Geometry.SameValues(expected.Ellipses.Single(e=>e.Id==ellipse.Id).Geometry))
                    throw new CadValidationException("Projected conic cache differs from its source.");
                foreach(var point in Points(s,p.TargetEntity))
                    if(!Near(s.Points.Single(v=>v.Id==point).Position,expected.Points.Single(v=>v.Id==point).Position))
                        throw new CadValidationException("Projection cache differs from its source.");
                var circle=s.Circles.SingleOrDefault(c=>c.Id==p.TargetEntity);
                if(circle is not null&&Math.Abs(circle.Radius-expected.Circles.Single(c=>c.Id==circle.Id).Radius)>1e-7)
                    throw new CadValidationException("Projection radius cache differs from its source.");
            }
        }
    }
    public static IReadOnlyList<CadSketch> Order(IReadOnlyDictionary<SketchId,CadSketch> sketches)
    {
        var ordered=new List<CadSketch>();
        var visiting=new HashSet<SketchId>();var levels=new Dictionary<SketchId,int>();
        void Visit(CadSketch s,int depth)
        {
            if(depth>64||!visiting.Add(s.Id))throw new CadValidationException("Projection dependencies are cyclic or exceed 64 levels.");
            int level=0;
            foreach(var p in s.Projections)
            {
                if(!sketches.TryGetValue(p.SourceSketch,out var source))throw new CadValidationException("Missing projection source.");
                if(!levels.ContainsKey(source.Id))Visit(source,depth+1);
                level=Math.Max(level,levels[source.Id]+1);
            }
            if(level>64)throw new CadValidationException("Projection dependencies exceed 64 levels.");
            visiting.Remove(s.Id);levels.Add(s.Id,level);ordered.Add(s);
        }
        foreach(var s in sketches.Values)if(!levels.ContainsKey(s.Id))Visit(s,0);
        return ordered;
    }
    public static void ValidateDrivenEdit(CadSketch before,CadSketch after)
    {
        foreach(var p in before.Projections.Where(p=>after.Projections.Contains(p)))
        {
            if(before.Freeforms.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} freeform&&
                !freeform.Geometry.SameValues(after.Freeforms.Single(e=>e.Id==p.TargetEntity).Geometry))
                throw SketchCurveFailure.Create("NativeProjectionDriven","Detach projected freeform before editing its definition.");
            if(before.Ellipses.SingleOrDefault(e=>e.Id==p.TargetEntity) is {} ellipse&&
                ellipse.Geometry!=after.Ellipses.Single(e=>e.Id==ellipse.Id).Geometry)
                throw new CadValidationException("Detach projected conic before editing its definition.");
            foreach(var id in Points(before,p.TargetEntity))
                if(before.Points.Single(v=>v.Id==id).Position!=after.Points.Single(v=>v.Id==id).Position)
                    throw new CadValidationException("Detach projected geometry before editing its coordinates.");
            var circle=before.Circles.SingleOrDefault(c=>c.Id==p.TargetEntity);
            if(circle is not null&&circle.Radius!=after.Circles.Single(c=>c.Id==circle.Id).Radius)
                throw new CadValidationException("Detach projected geometry before editing its radius.");
            if(p.Anchors.Any(id=>before.Constraints.Single(c=>c.Id==id)!=after.Constraints.Single(c=>c.Id==id)))
                throw new CadValidationException("Projection anchors cannot be edited or disabled.");
        }
    }
    private static bool Near(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-7;
}
