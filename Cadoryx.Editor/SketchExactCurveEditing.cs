using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

internal static class SketchExactCurveEditing
{
    private const double ParameterTolerance=1e-10;
    private static double Parameter(ExactSketchCurve curve,Point2d click,IExactSketchGeometry geometry)
    {
        CadGuard.Finite(click.X,click.Y);var projections=geometry.Project(curve,click);
        if(projections.IsDefaultOrEmpty)throw SketchCurveFailure.Create("CurveAmbiguousProjection","The point has no finite projection on the selected curve.");
        double minimum=projections.Min(p=>p.Distance);
        var nearest=projections.Where(p=>p.Distance<=minimum+1e-7).ToArray();
        if(nearest.Length!=1)throw SketchCurveFailure.Create("CurveAmbiguousProjection","The selected point has several equally near curve parameters.");
        return nearest[0].Parameter;
    }
    internal static (double First,double Last) RemoveInterval(CadSketch s,SketchEntityId id,ExactSketchCurve curve,
        Point2d click,IExactSketchGeometry geometry,bool closed=false,bool rejectEndpoints=false)
    {
        double selected=Parameter(curve,click,geometry);var cuts=new List<double>();
        if(rejectEndpoints&&(selected<=ParameterTolerance||selected>=1-ParameterTolerance))
            throw SketchCurveFailure.Create("CurvePickInterval","Select inside a finite interval, away from its endpoints.");
        foreach(var other in Curves(s).Where(c=>c.Id!=id))
            cuts.AddRange(geometry.Intersect(curve,other.Curve).Select(hit=>hit.FirstParameter));
        var unique=new List<double>();
        foreach(double t in cuts.Where(t=>closed?t>=0&&t<1-ParameterTolerance:t>ParameterTolerance&&t<1-ParameterTolerance).Order())
            if(unique.Count==0||t-unique[^1]>ParameterTolerance)unique.Add(t);
        if(unique.Count<(closed?2:1))throw SketchCurveFailure.Create("CurveNoIntersection","No distinct finite intersections delimit a removable interval.");
        if(unique.Any(t=>Math.Abs(t-selected)<=ParameterTolerance))
            throw SketchCurveFailure.Create("CurvePickInterval","Select inside the interval, away from its intersection.");
        return(unique.Where(t=>t<selected).DefaultIfEmpty(closed?unique[^1]-1:0).Max(),
            unique.Where(t=>t>selected).DefaultIfEmpty(closed?unique[0]+1:1).Min());
    }
    internal static (bool Start,double Amount) Extension(CadSketch s,SketchEntityId id,ExactSketchCurve curve,
        Point2d click,IExactSketchGeometry geometry,Func<bool,double,ExactSketchCurve> continuation)
    {
        double parameter=Parameter(curve,click,geometry);
        if(Math.Abs(parameter-.5)<=ParameterTolerance)throw SketchCurveFailure.Create("CurveAmbiguousProjection","Select a uniquely nearest endpoint side to extend.");
        bool start=parameter<.5;const double maximumAmount=4;var extended=continuation(start,maximumAmount);
        var candidates=new List<double>();
        foreach(var other in Curves(s).Where(c=>c.Id!=id))foreach(var hit in geometry.Intersect(extended,other.Curve))
        {
            double amount=start?maximumAmount-hit.FirstParameter*(1+maximumAmount):hit.FirstParameter*(1+maximumAmount)-1;
            if(amount>ParameterTolerance&&amount<=maximumAmount)candidates.Add(amount);
        }
        if(candidates.Count==0)throw SketchCurveFailure.Create("CurveNoIntersection","No finite boundary intersects the declared endpoint polynomial continuation.");
        return(start,candidates.Min());
    }
    internal static void RequireInterval(double first,double last)
    {
        if(!double.IsFinite(first)||!double.IsFinite(last)||first<0||last>1||first>=last)
            throw SketchCurveFailure.Create("CurveTrimBounds","Trim interval must increase inside [0,1].");
    }
    internal static void RequireExtension(double amount)
    {
        if(!double.IsFinite(amount)||amount<=0||amount>4)
            throw SketchCurveFailure.Create("CurveExtensionBounds","Polynomial extension amount must be in (0,4].");
    }
    internal static IEnumerable<(SketchEntityId Id,ExactSketchCurve Curve)> Curves(CadSketch s)
    {
        var points=s.Points.ToDictionary(p=>p.Id,p=>p.Position);Point2d P(SketchEntityId id)=>points[id];
        foreach(var l in s.Lines)yield return(l.Id,new(P(l.Start),P(l.End)));
        foreach(var a in s.Arcs)yield return(a.Id,new(P(a.Start),P(a.End),SketchArcGeometry.Through(P(a.Start),P(a.Middle),P(a.End))));
        foreach(var b in s.Beziers)yield return(b.Id,new(P(b.Start),P(b.End),Bezier:new(P(b.Start),P(b.Control),P(b.End))));
        foreach(var c in s.Circles){var arc=new SketchArcGeometry(P(c.Center),c.Radius,0,Math.Tau);yield return(c.Id,new(arc.At(0),arc.At(1),arc));}
        foreach(var c in s.Splines)yield return(c.Id,ExactSketchCurve.Cubic(CubicSplineKnotVector.Definition(c.Controls.Select(P).ToImmutableArray(),c.Knots)));
        foreach(var c in s.Ellipses)yield return(c.Id,new(c.Geometry.At(0),c.Geometry.At(1),Ellipse:c.Geometry));
        foreach(var c in s.Freeforms)yield return(c.Id,new(c.Geometry.At(0),c.Geometry.At(1),Freeform:c.Geometry));
        foreach(var c in s.ExpressionCurves)yield return(c.Id,new(c.Geometry.At(0),c.Geometry.At(1),Expression:c.Geometry));
    }
}
