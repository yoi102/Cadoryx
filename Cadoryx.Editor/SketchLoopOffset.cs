using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public static class SketchLoopOffset
{
    private const double Tolerance=1e-7;
    private sealed record Corner(Point2d PreviousEnd,Point2d NextStart,SketchBoundaryCurve? Bridge=null);

    public sealed record CreatedBoundary(CadSketch Sketch,SketchRegionReference Reference);
    public static CadSketch Offset(CadSketch sketch,SketchEntityId selected,double distance,SketchOffsetJoin join=SketchOffsetJoin.Miter)=>
        Create(sketch,selected,distance,join).Sketch;
    /// <summary>Independent exact line/arc offset. Returned IDs belong to this operation only.</summary>
    public static CreatedBoundary Create(CadSketch sketch,SketchEntityId selected,double distance,SketchOffsetJoin join=SketchOffsetJoin.Miter)
    {
        try{return CreateCore(sketch,selected,distance,join);}
        catch(CadValidationException error)
        {throw SketchCurveFailure.Create("LoopOffsetFailure",error.Message);}
    }
    private static CreatedBoundary CreateCore(CadSketch sketch,SketchEntityId selected,double distance,SketchOffsetJoin join)
    {
        if(!Enum.IsDefined(join))throw new CadValidationException("Unknown offset join.");
        sketch.Validate();CadGuard.Finite(distance);
        if(Math.Abs(distance)<=Tolerance)throw new CadValidationException("Loop offset distance must be nonzero.");
        if(sketch.Circles.Any(c=>c.Id==selected&&!c.IsConstruction))
        {
            var circular=SketchCurveEditing.Offset(sketch,selected,distance);
            var created=circular.Circles.Single(c=>!sketch.Circles.Any(old=>old.Id==c.Id));
            return new(circular,new(CircleId:created.Id));
        }
        var loops=SketchLoops.Find(sketch).Concat(SketchMixedLoops.Find(sketch)).Where(loop=>loop.Contains(selected)).ToArray();
        if(loops.Length!=1)throw new CadValidationException("Select an edge in one isolated, closed analytical loop.");
        var ids=loops[0];
        if(ids.Any(id=>!sketch.Lines.Any(l=>l.Id==id)&&!sketch.Arcs.Any(a=>a.Id==id)))
            throw new CadValidationException("Only straight lines and circular arcs may be offset.");
        var endpoints=sketch.Lines.Where(l=>ids.Contains(l.Id)).SelectMany(l=>new[]{l.Start,l.End})
            .Concat(sketch.Arcs.Where(a=>ids.Contains(a.Id)).SelectMany(a=>new[]{a.Start,a.End})).ToHashSet();
        if(sketch.Lines.Where(l=>!l.IsConstruction&&!ids.Contains(l.Id)).Any(l=>endpoints.Contains(l.Start)||endpoints.Contains(l.End))||
            sketch.Arcs.Where(a=>!a.IsConstruction&&!ids.Contains(a.Id)).Any(a=>endpoints.Contains(a.Start)||endpoints.Contains(a.End))||
            sketch.Beziers.Where(b=>!b.IsConstruction).Any(b=>endpoints.Contains(b.Start)||endpoints.Contains(b.End))||
            sketch.Splines.Where(s=>!s.IsConstruction).Any(s=>endpoints.Contains(s.Controls[0])||endpoints.Contains(s.Controls[^1]))||
            sketch.Ellipses.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Any(e=>endpoints.Contains(e.Endpoints!.Start)||endpoints.Contains(e.Endpoints.End))||
            sketch.Freeforms.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Any(e=>endpoints.Contains(e.Endpoints!.Start)||endpoints.Contains(e.Endpoints.End)))
            throw new CadValidationException("A branched component cannot be offset as a loop.");
        var profile=ids.All(id=>sketch.Lines.Any(l=>l.Id==id))?SketchProfileBuilder.Polygon(sketch,ids):SketchProfileBuilder.Mixed(sketch,ids);
        var boundary=profile.BoundaryCurves.IsEmpty?SketchMixedProfile.Polygon(profile.Points):profile.BoundaryCurves;
        var original=boundary.Select(Curve).ToArray();double area=Area(boundary);
        double left=-Math.Sign(area)*distance;
        var shifted=original.Select(c=>Shift(c,left)).ToArray();var corners=new Corner[shifted.Length];
        for(int i=0;i<shifted.Length;i++)
            corners[i]=Connect(original[(i+original.Length-1)%original.Length],original[i],
                shifted[(i+shifted.Length-1)%shifted.Length],shifted[i],left,join);
        var result=ImmutableArray.CreateBuilder<SketchBoundaryCurve>();
        for(int i=0;i<shifted.Length;i++)
        {
            var next=corners[(i+1)%corners.Length];
            result.Add(Trim(shifted[i],corners[i].NextStart,next.PreviousEnd));
            if(next.Bridge is {} bridge)result.Add(bridge);
        }
        var curves=result.ToImmutable();
        var candidate=curves.Any(c=>c.Middle is not null)?new SketchProfile([]){BoundaryCurves=curves}:new SketchProfile(curves.Select(c=>c.Start).ToImmutableArray());
        candidate.Validate();double newArea=Area(curves);
        if(area*newArea<=0||(Math.Abs(newArea)-Math.Abs(area))*distance<=0)
            throw new CadValidationException("Offset reverses the loop or crosses a collapse.");
        var points=sketch.Points;var lines=sketch.Lines;var arcs=sketch.Arcs;
        var vertices=curves.Select(c=>new SketchPoint(SketchEntityId.New(),c.Start){Origin=SketchPointOrigin.CurveGenerated}).ToArray();points=points.AddRange(vertices);
        var authored=ImmutableArray.CreateBuilder<SketchEntityId>();
        for(int i=0;i<curves.Length;i++)
        {
            var id=SketchEntityId.New();authored.Add(id);var start=vertices[i].Id;var end=vertices[(i+1)%curves.Length].Id;
            if(curves[i].Middle is {} middle)
            {
                var mid=new SketchPoint(SketchEntityId.New(),middle){Origin=SketchPointOrigin.CurveGenerated};points=points.Add(mid);arcs=arcs.Add(new(id,start,mid.Id,end));
            }
            else lines=lines.Add(new(id,start,end));
        }
        var reference=curves.Any(c=>c.Middle is not null)?new SketchRegionReference(MixedCurveIds:authored.ToImmutable()):
            new SketchRegionReference(PolygonLineIds:authored.ToImmutable());
        var sketchResult=sketch with{Points=points,Lines=lines,Arcs=arcs};sketchResult.Validate();
        return new(sketchResult,reference);
    }

    private static Corner Connect(SketchAnalyticalCurve originalA,SketchAnalyticalCurve originalB,
        SketchAnalyticalCurve a,SketchAnalyticalCurve b,double left,SketchOffsetJoin join)
    {
        if(Distance(a.End,b.Start)<=Tolerance)return new(b.Start,b.Start);
        if(join==SketchOffsetJoin.Miter)
        {
            var candidates=Supports(a,b).OrderBy(p=>Distance(p,a.End)+Distance(p,b.Start)).ToArray();
            if(candidates.Length==0||candidates.Length>1&&Math.Abs(
                Distance(candidates[0],a.End)+Distance(candidates[0],b.Start)-
                Distance(candidates[1],a.End)-Distance(candidates[1],b.Start))<=Tolerance)
                throw new CadValidationException("Offset corner has no unique analytical join.");
            var point=candidates[0];
            if(Distance(point,originalB.Start)>20*Math.Abs(left)+Tolerance)
                throw new CadValidationException("Offset corner exceeds the 20:1 miter limit.");
            return new(point,point);
        }
        var ta=Tangent(originalA,true);var tb=Tangent(originalB,false);
        double turn=Math.Atan2(ta.X*tb.Y-ta.Y*tb.X,ta.X*tb.X+ta.Y*tb.Y);
        if(Math.Abs(Math.PI-Math.Abs(turn))<=1e-10||Math.Abs(turn)<=1e-10)
            throw new CadValidationException("Offset corner direction is singular.");
        if(left*turn<0)
        {
            if(join==SketchOffsetJoin.Bevel)return new(a.End,b.Start,new(a.End,b.Start));
            var center=originalB.Start;double radius=Math.Abs(left);
            if(Distance(center,originalA.End)>Tolerance||Math.Abs(Distance(center,a.End)-radius)>Tolerance||
                Math.Abs(Distance(center,b.Start)-radius)>Tolerance)
                throw new CadValidationException("Corner endpoints are not on the explicit offset circle.");
            var arc=new SketchArcGeometry(center,radius,Math.Atan2(a.End.Y-center.Y,a.End.X-center.X),turn);
            if(Distance(arc.At(1),b.Start)>Tolerance)throw new CadValidationException("Corner has no proven directed circular bridge.");
            return new(a.End,b.Start,new(a.End,b.Start,arc.At(.5)));
        }
        var hits=new List<Point2d>();
        foreach(var p in Supports(a,b).Where(p=>a.Contains(p)&&b.Contains(p)))
            if(!hits.Any(old=>Distance(old,p)<=Tolerance))hits.Add(p);
        if(hits.Count!=1)throw new CadValidationException("Inner corner has no unique finite analytical intersection.");
        return new(hits[0],hits[0]);
    }
    private static SketchBoundaryCurve Trim(SketchAnalyticalCurve c,Point2d start,Point2d end)
    {
        if(c.Arc is {} arc)
        {
            double ds=SignedAngle(Math.Atan2(start.Y-arc.Center.Y,start.X-arc.Center.X)-arc.StartAngle);
            double de=SignedAngle(Math.Atan2(end.Y-arc.Center.Y,end.X-arc.Center.X)-arc.StartAngle-arc.SweepAngle);
            double sweep=arc.SweepAngle+de-ds;
            if(sweep*arc.SweepAngle<=0||Math.Abs(sweep)>=Math.Tau-1e-9||Math.Abs(sweep)*arc.Radius<=Tolerance)
                throw new CadValidationException("Offset arc collapses, reverses or becomes a full circle.");
            return new(start,end,(arc with{StartAngle=arc.StartAngle+ds,SweepAngle=sweep}).At(.5));
        }
        if((end.X-start.X)*(c.End.X-c.Start.X)+(end.Y-start.Y)*(c.End.Y-c.Start.Y)<=Tolerance*c.Length)
            throw new CadValidationException("Offset line collapses or reverses.");
        return new(start,end);
    }
    internal static double Area(IEnumerable<SketchBoundaryCurve> curves)
    {
        var boundary=curves.Select(Curve).ToArray();
        if(boundary.Length==0)return 0;
        var origin=boundary[0].Start;
        return boundary.Sum(c=>
            (((c.Start.X-origin.X)*(c.End.Y-origin.Y)-(c.End.X-origin.X)*(c.Start.Y-origin.Y))+
             (c.Arc is {} arc?arc.Radius*arc.Radius*(arc.SweepAngle-Math.Sin(arc.SweepAngle)):0))/2);
    }
    private static SketchAnalyticalCurve Curve(SketchBoundaryCurve c)=>new(c.Start,c.End,
        c.Middle is {} middle?SketchArcGeometry.Through(c.Start,middle,c.End):null);
    private static Point2d Tangent(SketchAnalyticalCurve c,bool end)
    {
        if(c.Arc is {} arc)
        {
            double angle=arc.StartAngle+(end?arc.SweepAngle:0),sign=Math.Sign(arc.SweepAngle);
            return new(-Math.Sin(angle)*sign,Math.Cos(angle)*sign);
        }
        return new((c.End.X-c.Start.X)/c.Length,(c.End.Y-c.Start.Y)/c.Length);
    }
    private static SketchAnalyticalCurve Shift(SketchAnalyticalCurve c,double left)
    {
        if(c.Arc is {} arc)
        {
            double radius=arc.Radius-Math.Sign(arc.SweepAngle)*left;
            if(radius<=Tolerance)throw new CadValidationException("Loop offset collapses a circular radius.");
            var shifted=arc with{Radius=radius};return new(shifted.At(0),shifted.At(1),shifted);
        }
        double dx=-(c.End.Y-c.Start.Y)*left/c.Length,dy=(c.End.X-c.Start.X)*left/c.Length;
        return new(new(c.Start.X+dx,c.Start.Y+dy),new(c.End.X+dx,c.End.Y+dy));
    }
    private static IEnumerable<Point2d> Supports(SketchAnalyticalCurve a,SketchAnalyticalCurve b)
    {
        if(a.Arc is null&&b.Arc is null)
        {
            double dx=a.End.X-a.Start.X,dy=a.End.Y-a.Start.Y,ex=b.End.X-b.Start.X,ey=b.End.Y-b.Start.Y,den=dx*ey-dy*ex;
            if(Math.Abs(den)>1e-12*a.Length*b.Length)yield return a.At(((b.Start.X-a.Start.X)*ey-(b.Start.Y-a.Start.Y)*ex)/den);
            yield break;
        }
        if(a.Arc is null||b.Arc is null)
        {
            var line=a.Arc is null?a:b;var arc=(a.Arc??b.Arc)!.Value;
            double dx=(line.End.X-line.Start.X)/line.Length,dy=(line.End.Y-line.Start.Y)/line.Length;
            double along=(arc.Center.X-line.Start.X)*dx+(arc.Center.Y-line.Start.Y)*dy;
            var foot=new Point2d(line.Start.X+along*dx,line.Start.Y+along*dy);double d=Distance(foot,arc.Center);
            if(d>arc.Radius+Tolerance)yield break;
            double half=Math.Sqrt(Math.Max(0,(arc.Radius-d)*(arc.Radius+d)));
            yield return new(foot.X-half*dx,foot.Y-half*dy);
            if(half>Tolerance)yield return new(foot.X+half*dx,foot.Y+half*dy);
            yield break;
        }
        var first=a.Arc!.Value;var second=b.Arc!.Value;double separation=Distance(first.Center,second.Center);
        if(separation<=Tolerance||separation>first.Radius+second.Radius+Tolerance||separation<Math.Abs(first.Radius-second.Radius)-Tolerance)yield break;
        double alongCenters=(first.Radius*first.Radius-second.Radius*second.Radius+separation*separation)/(2*separation);
        double height=Math.Sqrt(Math.Max(0,(first.Radius-alongCenters)*(first.Radius+alongCenters)));
        double ux=(second.Center.X-first.Center.X)/separation,uy=(second.Center.Y-first.Center.Y)/separation;
        var basePoint=new Point2d(first.Center.X+alongCenters*ux,first.Center.Y+alongCenters*uy);
        yield return new(basePoint.X-height*uy,basePoint.Y+height*ux);
        if(height>Tolerance)yield return new(basePoint.X+height*uy,basePoint.Y-height*ux);
    }
    private static double SignedAngle(double a)=>((a+Math.PI)%Math.Tau+Math.Tau)%Math.Tau-Math.PI;
    private static double Distance(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}
