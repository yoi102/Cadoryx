using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Stable endpoint variables and the identities of their derived structural equations.</summary>
public sealed record SketchExpressionEndpoints(SketchEntityId Start,SketchEntityId End,
    SketchConstraintId StartEquation,SketchConstraintId EndEquation);

/// <summary>A derived structural equation. Never an authored, disableable constraint-table entry.</summary>
public sealed record SketchExpressionEndpointEquation(SketchConstraintId Id,SketchEntityId Curve,
    SketchEntityId Point,double Parameter):SketchConstraint(Id,true);

public static class SketchAssociativeEndpoints
{
    public static IEnumerable<SketchExpressionEndpointEquation> Equations(CadSketch sketch)
    {
        foreach(var c in sketch.ExpressionCurves.Where(c=>c.Endpoints is not null))
        {
            var e=c.Endpoints!;
            yield return new(e.StartEquation,c.Id,e.Start,0);
            yield return new(e.EndEquation,c.Id,e.End,1);
        }
    }
    public static void Validate(CadSketch sketch)
    {
        var ids=sketch.Constraints.Select(c=>c.Id).ToHashSet();
        foreach(var c in sketch.ExpressionCurves)
        {
            if(c.Endpoints is not {} ends)
            {if(!c.IsConstruction)throw new CadValidationException("A non-construction expression needs explicit associative endpoints.");continue;}
            CadGuard.Id(ends.Start);CadGuard.Id(ends.End);CadGuard.Id(ends.StartEquation);CadGuard.Id(ends.EndEquation);
            if(ends.Start==ends.End||!ids.Add(ends.StartEquation)||!ids.Add(ends.EndEquation)||
                !sketch.Points.Any(p=>p.Id==ends.Start)||!sketch.Points.Any(p=>p.Id==ends.End))
                throw new CadValidationException("Expression endpoints need distinct existing point identities and unique structural equation identities.");
        }
        if(sketch.Constraints.Length+Equations(sketch).Count()>10000)
            throw new CadValidationException("Combined authored and structural constraint table exceeds its budget.");
    }
    public static CadSketch Attach(CadSketch sketch,SketchEntityId id,SketchEntityId? start=null,SketchEntityId? end=null)
    {
        sketch.Validate();var c=sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==id)??throw new CadValidationException("Select an exact expression.");
        if(c.Endpoints is not null)throw new CadValidationException("Expression endpoints already exist.");
        var ends=new SketchExpressionEndpoints(start??SketchEntityId.New(),end??SketchEntityId.New(),SketchConstraintId.New(),SketchConstraintId.New());
        var points=sketch.Points;
        void Add(SketchEntityId? selected,SketchEntityId point,Point2d position)
        {
            if(selected is not null)
            {
                if(!points.Any(p=>p.Id==point&&Near(p.Position,position,1e-7)))
                    throw new CadValidationException("The explicitly selected point must match the expression endpoint.");
            }
            else points=points.Add(new(point,position,c.IsConstruction){Origin=SketchPointOrigin.CurveGenerated});
        }
        Add(start,ends.Start,c.Geometry.At(0));Add(end,ends.End,c.Geometry.At(1));
        var next=sketch with{Points=points,ExpressionCurves=sketch.ExpressionCurves.Replace(c,c with{Endpoints=ends})};
        next.Validate();return next;
    }
    public static void RequireSolved(CadSketch sketch,double tolerance=1e-7,SketchEntityId? curve=null)
    {
        CadGuard.Positive(tolerance);
        foreach(var e in Equations(sketch).Where(e=>curve is null||e.Curve==curve))
        {
            var c=sketch.ExpressionCurves.Single(c=>c.Id==e.Curve);var p=sketch.Points.Single(p=>p.Id==e.Point).Position;
            if(!Near(p,c.Geometry.At(e.Parameter),tolerance))throw new CadValidationException("SKETCH.EXPRESSION_ENDPOINT_OUTPUT");
        }
    }
    public static bool HasExpressionTarget(CadSketch sketch,SketchConstraint constraint)
    {
        var pair=constraint switch
        {
            FiniteParametricContactConstraint c=>(c.A,c.B),EndpointTangentConstraint c=>(c.A,c.B),EndpointCurvatureConstraint c=>(c.A,c.B),
            _=>(default(SketchEntityId),default(SketchEntityId))
        };
        return sketch.ExpressionCurves.Any(c=>c.Id==pair.Item1||c.Id==pair.Item2);
    }
    internal static ImmutableArray<SketchEntityId> AuthorPoints(CadSketch sketch,SketchEntityId id)
    {
        if(sketch.Lines.SingleOrDefault(c=>c.Id==id) is {} l)return[l.Start,l.End];
        if(sketch.Circles.SingleOrDefault(c=>c.Id==id) is {} c)return[c.Center];
        if(sketch.Arcs.SingleOrDefault(c=>c.Id==id) is {} a)return[a.Start,a.Middle,a.End];
        if(sketch.Beziers.SingleOrDefault(c=>c.Id==id) is {} b)return[b.Start,b.Control,b.End];
        if(sketch.Splines.SingleOrDefault(c=>c.Id==id) is {} s)return s.Controls;
        return[];
    }
    private static bool Near(Point2d a,Point2d b,double tolerance)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=tolerance;
}
