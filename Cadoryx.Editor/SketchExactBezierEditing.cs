using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public void TrimBezier(SketchEntityId id,double first,double last)=>Change(s=>SketchExactBezierEditing.Keep(s,id,first,last));
    public void RemoveBezierInterval(SketchEntityId id,double first,double last)=>Change(s=>SketchExactBezierEditing.Remove(s,id,first,last));
    public void TrimBezierAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry)=>Change(s=>SketchExactBezierEditing.RemoveAt(s,id,click,geometry));
    public void ExtendBezier(SketchEntityId id,bool start,double amount)=>Change(s=>SketchExactBezierEditing.Extend(s,id,start,amount));
    public void ExtendBezierAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry)=>Change(s=>SketchExactBezierEditing.ExtendAt(s,id,click,geometry));
}

public static class SketchExactBezierEditing
{
    public static QuadraticBezierRegion Definition(CadSketch s,SketchEntityId id)
    {
        s.Validate();if(s.Projections.Any(p=>p.TargetEntity==id))throw new CadValidationException("Detach the projection before editing its author curve.");
        var b=s.Beziers.SingleOrDefault(c=>c.Id==id)??throw new CadValidationException("Select one finite quadratic Bezier.");
        Point2d P(SketchEntityId point)=>s.Points.Single(p=>p.Id==point).Position;
        return new(P(b.Start),P(b.Control),P(b.End));
    }
    private static ExactSketchCurve Exact(QuadraticBezierRegion b)=>new(b.Start,b.End,Bezier:b);
    private static QuadraticBezierRegion Interval(QuadraticBezierRegion b,double first,double last)
    {
        CadGuard.Finite(first,last);if(first>=last)throw new CadValidationException("Curve parameters must increase.");
        Point2d At(double t)=>SketchBezierGeometry.At(b.Start,b.Control,b.End,t);
        var a=At(first);var e=At(last);double width=last-first;
        var derivative=new Point2d(2*((1-first)*(b.Control.X-b.Start.X)+first*(b.End.X-b.Control.X)),
            2*((1-first)*(b.Control.Y-b.Start.Y)+first*(b.End.Y-b.Control.Y)));
        var result=new QuadraticBezierRegion(a,new(a.X+width*derivative.X/2,a.Y+width*derivative.Y/2),e);
        SketchBezierGeometry.Validate(result.Start,result.Control,result.End);return result;
    }
    public static CadSketch Keep(CadSketch s,SketchEntityId id,double first,double last)
    {SketchExactCurveEditing.RequireInterval(first,last);return Replace(s,id,[(Interval(Definition(s,id),first,last),first==0,last==1)],true);}
    public static CadSketch Remove(CadSketch s,SketchEntityId id,double first,double last)
    {
        SketchExactCurveEditing.RequireInterval(first,last);var b=Definition(s,id);var pieces=new List<(QuadraticBezierRegion,bool,bool)>();
        if(first>0)pieces.Add((Interval(b,0,first),true,false));if(last<1)pieces.Add((Interval(b,last,1),false,true));
        return Replace(s,id,pieces,pieces.Count==1);
    }
    private static QuadraticBezierRegion Continue(QuadraticBezierRegion b,bool start,double amount)
    {
        SketchExactCurveEditing.RequireExtension(amount);
        return Interval(b,start?-amount:0,start?1:1+amount);
    }
    public static CadSketch Extend(CadSketch s,SketchEntityId id,bool start,double amount)=>
        Replace(s,id,[(Continue(Definition(s,id),start,amount),!start,start)],true);
    public static CadSketch RemoveAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry)
    {
        var (first,last)=SketchExactCurveEditing.RemoveInterval(s,id,Exact(Definition(s,id)),click,geometry);
        return Remove(s,id,first,last);
    }
    public static CadSketch ExtendAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry)
    {
        var b=Definition(s,id);var (start,amount)=SketchExactCurveEditing.Extension(s,id,Exact(b),click,geometry,(side,length)=>Exact(Continue(b,side,length)));
        return Extend(s,id,start,amount);
    }
    private static CadSketch Replace(CadSketch s,SketchEntityId id,IReadOnlyList<(QuadraticBezierRegion Curve,bool Start,bool End)> pieces,bool keepId)
    {
        var original=s.Beziers.Single(c=>c.Id==id);var points=s.Points.ToBuilder();var replacements=new List<SketchBezier>();
        foreach(var piece in pieces)
        {
            SketchEntityId Point(Point2d value,SketchEntityId? existing=null)
            {var point=existing??SketchEntityId.New();if(existing is null)points.Add(new(point,value){Origin=SketchPointOrigin.CurveGenerated});return point;}
            replacements.Add(new(keepId?id:SketchEntityId.New(),Point(piece.Curve.Start,piece.Start?original.Start:null),
                Point(piece.Curve.Control),Point(piece.Curve.End,piece.End?original.End:null),original.IsConstruction));
        }
        var beziers=s.Beziers.Remove(original).AddRange(replacements);
        var candidate=s with{Points=points.ToImmutable(),Beziers=beziers};
        var removed=SketchPointUsage.GeneratedOrphans(candidate,[original.Start,original.Control,original.End]).ToHashSet();if(!keepId)removed.Add(id);
        var constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)&&
            SketchConstraintEditing.KeepEndpointRelation(c,id,replacements.Count==1?replacements[0].Start:default,
                replacements.Count==1?replacements[0].End:default)).ToImmutableArray();
        return s with{Points=points.Where(p=>!removed.Contains(p.Id)).ToImmutableArray(),Beziers=beziers,Constraints=constraints};
    }
}
