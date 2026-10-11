using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public static class SketchCurveEditing
{
    public static CadSketch Trim(CadSketch s,SketchEntityId id,Point2d click)
    {
        EnsureEditable(s,id);CadGuard.Finite(click.X,click.Y);var curve=SketchAnalyticalCurve.From(s,id);
        double epsilon=SketchAnalyticalCurve.Tolerance/curve.Length,t=curve.Parameter(click);
        var cuts=Cuts(s,id,curve).Where(v=>curve.Closed||v>epsilon&&v<1-epsilon).Order().ToArray();
        var unique=new List<double>();foreach(double v in cuts)if(unique.Count==0||v-unique[^1]>epsilon)unique.Add(v);
        if(curve.Closed&&unique.Count>1&&unique[0]+1-unique[^1]<=epsilon)unique.RemoveAt(unique.Count-1);
        if(unique.Count<(curve.Closed?2:1))throw new CadValidationException("Not enough distinct intersections to trim this curve.");
        if(unique.Any(v=>Math.Abs(v-t)<=epsilon))throw new CadValidationException("Click inside the interval to remove, away from an intersection.");
        double left=unique.Where(v=>v<t).DefaultIfEmpty(curve.Closed?unique[^1]-1:0).Max();
        double right=unique.Where(v=>v>t).DefaultIfEmpty(curve.Closed?unique[0]+1:1).Min();
        if(curve.Closed)return Replace(s,id,curve,[(right,left+1)],false);
        if(t>1+epsilon)throw new CadValidationException("Click on the finite arc to trim.");
        var pieces=new List<(double,double)>();if(left>epsilon)pieces.Add((0,left));if(right<1-epsilon)pieces.Add((right,1));
        return Replace(s,id,curve,pieces,pieces.Count==1);
    }
    public static CadSketch Extend(CadSketch s,SketchEntityId id,Point2d click)
    {
        EnsureEditable(s,id);CadGuard.Finite(click.X,click.Y);var curve=SketchAnalyticalCurve.From(s,id);
        if(curve.Closed)throw new CadValidationException("A full circle has no endpoint to extend.");
        double epsilon=SketchAnalyticalCurve.Tolerance/curve.Length,t=curve.Parameter(click);
        if(Math.Abs(t-.5)<=epsilon||curve.Arc is not null&&t>1+epsilon)throw new CadValidationException("Click on the curve near the endpoint to extend.");
        bool start=t<.5;double period=curve.Arc is {} arc?Math.Tau/Math.Abs(arc.SweepAngle):double.PositiveInfinity;
        var cuts=Cuts(s,id,curve).Select(v=>start&&curve.Arc is not null?v-period:v)
            .Where(v=>start?v < -epsilon&&v>1-period+epsilon:v>1+epsilon&&v<period-epsilon).ToArray();
        if(cuts.Length==0)throw new CadValidationException("No finite boundary meets this extension.");
        double cut=start?cuts.Max():cuts.Min();
        return Replace(s,id,curve,[(start?cut:0,start?1:cut)],true);
    }
    public static CadSketch Offset(CadSketch s,SketchEntityId id,double distance)
    {
        CadGuard.Finite(distance);if(Math.Abs(distance)<=SketchAnalyticalCurve.Tolerance)throw new CadValidationException("Offset distance must be nonzero.");
        var curve=SketchAnalyticalCurve.From(s,id);var d=new SketchDraft(s);
        bool construction=s.Lines.Any(l=>l.Id==id&&l.IsConstruction)||s.Circles.Any(c=>c.Id==id&&c.IsConstruction)||s.Arcs.Any(a=>a.Id==id&&a.IsConstruction);
        if(curve.Arc is {} arc)
        {
            double radius=arc.Radius+distance;if(radius<=SketchAnalyticalCurve.Tolerance)throw new CadValidationException("Offset collapses the radius.");
            var shifted=arc with{Radius=radius};
            if(curve.Closed)d.Change(v=>
            {
                var center=SketchEntityId.New();return v with{Points=v.Points.Add(new(center,arc.Center){Origin=SketchPointOrigin.CurveGenerated}),Circles=v.Circles.Add(new(SketchEntityId.New(),center,radius,construction))};
            });
            else d.AddArc(shifted.At(0),shifted.At(.5),shifted.At(1),construction:construction);
        }
        else
        {
            double dx=-(curve.End.Y-curve.Start.Y)*distance/curve.Length,dy=(curve.End.X-curve.Start.X)*distance/curve.Length;
            d.AddLine(new(curve.Start.X+dx,curve.Start.Y+dy),new(curve.End.X+dx,curve.End.Y+dy),construction:construction);
        }
        return d.Value;
    }
    private static IEnumerable<double> Cuts(CadSketch s,SketchEntityId id,SketchAnalyticalCurve target)
    {
        foreach(var other in SketchAnalyticalCurve.Ids(s).Where(v=>v!=id))
            foreach(var hit in SketchAnalyticalCurve.Intersections(target,SketchAnalyticalCurve.From(s,other)))yield return target.Parameter(hit);
    }
    private static void EnsureEditable(CadSketch s,SketchEntityId id)
    {
        if(s.Projections.Any(p=>p.TargetEntity==id))throw new CadValidationException("Detach the projected geometry before editing it.");
    }
    private static CadSketch Replace(CadSketch s,SketchEntityId id,SketchAnalyticalCurve curve,IReadOnlyList<(double Start,double End)> intervals,bool retain)
    {
        var line=s.Lines.SingleOrDefault(l=>l.Id==id);var arc=s.Arcs.SingleOrDefault(a=>a.Id==id);var circle=s.Circles.SingleOrDefault(c=>c.Id==id);
        var points=s.Points;var lines=s.Lines.Where(l=>l.Id!=id).ToImmutableArray();var arcs=s.Arcs.Where(a=>a.Id!=id).ToImmutableArray();
        var circles=s.Circles.Where(c=>c.Id!=id).ToImmutableArray();
        var oldPoints=line is not null?new[]{line.Start,line.End}:arc is not null?new[]{arc.Start,arc.Middle,arc.End}:new[]{circle!.Center};
        bool construction=line?.IsConstruction??arc?.IsConstruction??circle!.IsConstruction;
        var boundaryEnds=s.Lines.Where(l=>l.Id!=id).SelectMany(l=>new[]{l.Start,l.End})
            .Concat(s.Arcs.Where(a=>a.Id!=id).SelectMany(a=>new[]{a.Start,a.End})).ToHashSet();
        SketchEntityId At(double t,bool middle=false)
        {
            if(!middle&&!curve.Closed&&t==0)return line?.Start??arc!.Start;
            if(!middle&&!curve.Closed&&t==1)return line?.End??arc!.End;
            var position=curve.At(t);
            var matches=middle?[]:points.Where(p=>boundaryEnds.Contains(p.Id)&&double.Hypot(p.Position.X-position.X,p.Position.Y-position.Y)<=SketchAnalyticalCurve.Tolerance).ToArray();
            if(matches.Length>1)throw new CadValidationException("Intersection has multiple endpoint identities.");
            if(matches.Length==1)return matches[0].Id;
            var result=SketchEntityId.New();points=points.Add(new(result,position){Origin=SketchPointOrigin.CurveGenerated});return result;
        }
        foreach(var (start,end) in intervals)
        {
            var newId=retain?id:SketchEntityId.New();var a=At(start);var b=At(end);
            if(curve.Arc is not null)arcs=arcs.Add(new(newId,a,At((start+end)/2,true),b,construction));
            else lines=lines.Add(new(newId,a,b,construction));
        }
        var removed=new HashSet<SketchEntityId>{id};
        foreach(var p in SketchPointUsage.GeneratedOrphans(s with{Points=points,Lines=lines,Circles=circles,Arcs=arcs},oldPoints))
        {points=points.Where(v=>v.Id!=p).ToImmutableArray();removed.Add(p);}
        return s with{Points=points,Lines=lines,Arcs=arcs,Circles=circles,Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)).ToImmutableArray()};
    }
}
