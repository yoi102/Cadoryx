using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Explicit author identities at the ends of a fixed numeric curve.</summary>
public sealed record SketchCurveEndpoints(SketchEntityId Start,SketchEntityId End,
    SketchConstraintId StartAnchor,SketchConstraintId EndAnchor);

public static class SketchFixedCurveEndpoints
{
    public static IEnumerable<(SketchEntityId Curve,SketchCurveEndpoints Ends)> All(CadSketch sketch)=>
        sketch.Ellipses.Where(e=>e.Endpoints is not null).Select(e=>(e.Id,e.Endpoints!))
            .Concat(sketch.Freeforms.Where(e=>e.Endpoints is not null).Select(e=>(e.Id,e.Endpoints!)));

    public static (Point2d Start,Point2d End) Positions(CadSketch sketch,SketchEntityId id)=>
        sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse?(ellipse.Geometry.At(0),ellipse.Geometry.At(1)):
        sketch.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform?(freeform.Geometry.At(0),freeform.Geometry.At(1)):
        throw Failure("Select a finite exact ellipse or freeform curve.");

    public static CadSketch Attach(CadSketch sketch,SketchEntityId id,SketchEntityId? start=null,SketchEntityId? end=null)
    {
        sketch.Validate();
        if(All(sketch).Any(e=>e.Curve==id))throw Failure("The curve already has explicit endpoints.");
        var(a,b)=Positions(sketch,id);
        if(Near(a,b))throw Failure("A closed curve cannot expose two distinct boundary endpoints.");
        var ends=new SketchCurveEndpoints(start??SketchEntityId.New(),end??SketchEntityId.New(),SketchConstraintId.New(),SketchConstraintId.New());
        var points=sketch.Points;
        void Point(SketchEntityId? selected,SketchEntityId created,Point2d position)
        {
            if(selected is {} existing)
            {
                if(!points.Any(p=>p.Id==existing&&Near(p.Position,position)))
                    throw Failure("The explicitly selected endpoint does not match the exact curve end.");
            }
            else points=points.Add(new(created,position){Origin=SketchPointOrigin.CurveGenerated});
        }
        Point(start,ends.Start,a);Point(end,ends.End,b);
        var next=sketch with{Points=points,Constraints=sketch.Constraints.Add(new FixPointConstraint(ends.StartAnchor,ends.Start,a))
            .Add(new FixPointConstraint(ends.EndAnchor,ends.End,b))};
        if(next.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
            next=next with{Ellipses=next.Ellipses.Replace(ellipse,ellipse with{Endpoints=ends})};
        else
        {
            var freeform=next.Freeforms.Single(e=>e.Id==id);
            next=next with{Freeforms=next.Freeforms.Replace(freeform,freeform with{Endpoints=ends})};
        }
        next.Validate();return next;
    }

    /// <summary>Only an explicit definition edit or source refresh may move these identities.</summary>
    public static CadSketch Synchronize(CadSketch sketch,SketchEntityId id)
    {
        var ends=All(sketch).Where(e=>e.Curve==id).Select(e=>e.Ends).SingleOrDefault();
        if(ends is null)return sketch;
        var(a,b)=Positions(sketch,id);var points=sketch.Points;var constraints=sketch.Constraints;
        void Set(SketchEntityId pointId,SketchConstraintId anchorId,Point2d position)
        {
            var point=points.Single(p=>p.Id==pointId);
            if(constraints.Single(c=>c.Id==anchorId) is not FixPointConstraint {IsEnabled:true} anchor||anchor.Point!=pointId)
                throw Failure("The exact endpoint anchor was changed.");
            points=points.Replace(point,point with{Position=position});
            constraints=constraints.Replace(anchor,anchor with{Position=position});
        }
        Set(ends.Start,ends.StartAnchor,a);Set(ends.End,ends.EndAnchor,b);
        return sketch with{Points=points,Constraints=constraints};
    }

    public static void Validate(CadSketch sketch)
    {
        var anchors=new HashSet<SketchConstraintId>();
        foreach(var(id,ends) in All(sketch))
        {
            CadGuard.Id(ends.Start);CadGuard.Id(ends.End);CadGuard.Id(ends.StartAnchor);CadGuard.Id(ends.EndAnchor);
            var(a,b)=Positions(sketch,id);
            if(ends.Start==ends.End||Near(a,b)||!anchors.Add(ends.StartAnchor)||!anchors.Add(ends.EndAnchor))
                throw Failure("Exact curve endpoints require distinct identities and anchors.");
            void Check(SketchEntityId pointId,SketchConstraintId anchorId,Point2d expected)
            {
                var point=sketch.Points.SingleOrDefault(p=>p.Id==pointId);
                if(point is null||!Near(point.Position,expected)||
                    sketch.Constraints.SingleOrDefault(c=>c.Id==anchorId) is not FixPointConstraint {IsEnabled:true} anchor||
                    anchor.Point!=pointId||!Near(anchor.Position,expected))
                    throw Failure("Exact curve endpoints are fixed by the numeric definition; edit its definition to move them.");
            }
            Check(ends.Start,ends.StartAnchor,a);Check(ends.End,ends.EndAnchor,b);
        }
        if(sketch.Projections.SelectMany(p=>p.Anchors).Concat(sketch.NativeProjections.SelectMany(p=>p.Anchors)).Any(anchors.Contains))
            throw Failure("Exact endpoint anchors cannot also be projection anchors.");
    }
    private static bool Near(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-7;
    private static CadValidationException Failure(string message)=>SketchCurveFailure.Create("ExactEndpointProtected",message);
}
