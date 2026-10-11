using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public sealed record SketchCornerEdit(CadSketch Sketch,ImmutableArray<SketchCornerReplacement> Replacements);

/// <summary>Calculates all corners against the original sketch before trimming any shared line.</summary>
public static class SketchCornerEditing
{
    private const double Clearance=1e-7;
    private sealed record Corner(SketchLine A,SketchLine B,SketchEntityId Joint,Point2d Start,Point2d End,Point2d? Middle,double TrimA,double TrimB);
    public static SketchCornerEdit Lines(CadSketch s,SketchEntityId first,SketchEntityId second,bool fillet,double radius,double distanceA,double distanceB,bool constrainRadius=false)=>
        Apply(s,[Calculate(s,first,second,fillet,radius,distanceA,distanceB)],constrainRadius,radius);
    public static SketchCornerEdit Loop(CadSketch s,IEnumerable<SketchEntityId> selectedJoints,bool fillet,double radius,double distanceA,double distanceB,bool constrainRadius=false)
    {
        s.Validate();var joints=selectedJoints.ToArray();
        if(joints.Length is <1 or >32||joints.Distinct().Count()!=joints.Length)throw SketchCornerFailure.Create("CornerBatchSelection");
        var loops=SketchLoops.Find(s).Where(ids=>joints.All(j=>ids.Count(id=>s.Lines.Single(l=>l.Id==id) is var l&&(l.Start==j||l.End==j))==2)).ToArray();
        if(loops.Length!=1)throw SketchCornerFailure.Create("CornerBatchLoop");
        var loop=loops[0];var corners=joints.Select(j=>
        {
            int index=Enumerable.Range(0,loop.Length).Single(i=>
            {
                var a=s.Lines.Single(l=>l.Id==loop[i]);var b=s.Lines.Single(l=>l.Id==loop[(i+1)%loop.Length]);
                return(a.Start==j||a.End==j)&&(b.Start==j||b.End==j);
            });
            return Calculate(s,loop[index],loop[(index+1)%loop.Length],fillet,radius,distanceA,distanceB);
        }).ToArray();
        var result=Apply(s,corners,constrainRadius,radius);var ordered=new List<SketchEntityId>();
        for(int i=0;i<loop.Length;i++)
        {
            ordered.Add(loop[i]);var a=loop[i];var b=loop[(i+1)%loop.Length];
            var r=result.Replacements.SingleOrDefault(r=>(r.LineA==a&&r.LineB==b)||(r.LineA==b&&r.LineB==a));
            if(r is not null)ordered.Add(r.Connection);
        }
        if(fillet)_=SketchProfileBuilder.Mixed(result.Sketch,ordered);
        else _=SketchProfileBuilder.Polygon(result.Sketch,ordered);
        return result;
    }
    private static Corner Calculate(CadSketch s,SketchEntityId first,SketchEntityId second,bool fillet,double radius,double distanceA,double distanceB)
    {
        s.Validate();
        if(!double.IsFinite(fillet?radius:distanceA)||!double.IsFinite(fillet?radius:distanceB))throw SketchCornerFailure.Create("CornerDimensionsInvalid");
        if((fillet?radius:Math.Min(distanceA,distanceB))<=Clearance)throw SketchCornerFailure.Create("CornerDimensionsInvalid");
        if(first==second)throw SketchCornerFailure.Create("CornerSelectLines");
        var a=s.Lines.SingleOrDefault(l=>l.Id==first);var b=s.Lines.SingleOrDefault(l=>l.Id==second);
        if(a is null||b is null)throw SketchCornerFailure.Create("CornerSelectLines");
        if(a.IsConstruction!=b.IsConstruction)throw SketchCornerFailure.Create("CornerConstructionMismatch");
        if(s.Projections.Any(p=>p.TargetEntity==first||p.TargetEntity==second))throw SketchCornerFailure.Create("CornerProjectionDriven");
        var joints=new[]{a.Start,a.End}.Intersect(new[]{b.Start,b.End}).ToArray();
        if(joints.Length!=1)throw SketchCornerFailure.Create("CornerUniqueJoint");
        var joint=joints[0];
        if(s.Lines.Any(l=>l.Id!=first&&l.Id!=second&&(l.Start==joint||l.End==joint))||s.Circles.Any(c=>c.Center==joint)||
            s.Arcs.Any(c=>c.Start==joint||c.Middle==joint||c.End==joint)||s.Beziers.Any(c=>c.Start==joint||c.Control==joint||c.End==joint)||
            s.Splines.Any(c=>c.Controls.Contains(joint)))throw SketchCornerFailure.Create("CornerBranch");
        Point2d P(SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
        var o=P(joint);var endA=P(a.Start==joint?a.End:a.Start);var endB=P(b.Start==joint?b.End:b.Start);
        double la=double.Hypot(endA.X-o.X,endA.Y-o.Y),lb=double.Hypot(endB.X-o.X,endB.Y-o.Y);
        var u=new Point2d((endA.X-o.X)/la,(endA.Y-o.Y)/la);var v=new Point2d((endB.X-o.X)/lb,(endB.Y-o.Y)/lb);
        double angle=Math.Atan2(Math.Abs(u.X*v.Y-u.Y*v.X),u.X*v.X+u.Y*v.Y);
        if(angle<=1e-6||Math.PI-angle<=1e-6)throw SketchCornerFailure.Create("CornerUnstable");
        double trimA=fillet?radius/Math.Tan(angle/2):distanceA,trimB=fillet?trimA:distanceB;
        if(!double.IsFinite(trimA+trimB)||trimA<=Clearance||trimB<=Clearance||la-trimA<=Clearance||lb-trimB<=Clearance)
            throw SketchCornerFailure.Create("CornerConsume",joint);
        var start=new Point2d(o.X+u.X*trimA,o.Y+u.Y*trimA);var end=new Point2d(o.X+v.X*trimB,o.Y+v.Y*trimB);Point2d? middle=null;
        if(fillet)
        {
            double centerDistance=radius/Math.Sin(angle/2),bisectorLength=double.Hypot(u.X+v.X,u.Y+v.Y);
            var bisector=new Point2d((u.X+v.X)/bisectorLength,(u.Y+v.Y)/bisectorLength);
            middle=new(o.X+bisector.X*(centerDistance-radius),o.Y+bisector.Y*(centerDistance-radius));
            var geometry=SketchArcGeometry.Through(start,middle.Value,end);
            if(Math.Abs(geometry.Radius-radius)>Math.Max(Clearance,radius*1e-8)||Math.Abs(geometry.SweepAngle)>=Math.PI)
                throw SketchCornerFailure.Create("CornerAccuracy");
        }
        return new(a,b,joint,start,end,middle,trimA,trimB);
    }
    private static SketchCornerEdit Apply(CadSketch s,IReadOnlyList<Corner> corners,bool constrainRadius,double radius)
    {
        if(corners.Select(c=>c.Joint).Distinct().Count()!=corners.Count)throw SketchCornerFailure.Create("CornerDuplicate");
        Point2d P(SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
        foreach(var group in corners.SelectMany(c=>new[]{(Line:c.A,Trim:c.TrimA),(Line:c.B,Trim:c.TrimB)}).GroupBy(v=>v.Line.Id))
        {
            var line=group.First().Line;
            if(double.Hypot(P(line.Start).X-P(line.End).X,P(line.Start).Y-P(line.End).Y)-group.Sum(v=>v.Trim)<=Clearance)
                throw SketchCornerFailure.Create("CornerBudgetConsume",line.Id);
        }
        var points=s.Points.ToBuilder();var arcs=s.Arcs.ToBuilder();var extraLines=new List<SketchLine>();
        var lines=s.Lines.ToDictionary(l=>l.Id);var additions=new List<SketchConstraint>();var replacements=ImmutableArray.CreateBuilder<SketchCornerReplacement>();
        var affected=new HashSet<SketchEntityId>();
        foreach(var c in corners)
        {
            var p=SketchEntityId.New();var q=SketchEntityId.New();var id=SketchEntityId.New();points.Add(new(p,c.Start){Origin=SketchPointOrigin.CurveGenerated});points.Add(new(q,c.End){Origin=SketchPointOrigin.CurveGenerated});
            affected.UnionWith([c.A.Id,c.B.Id]);
            SketchLine Trim(SketchLine line,SketchEntityId point)=>line.Start==c.Joint?line with{Start=point}:line with{End=point};
            lines[c.A.Id]=Trim(lines[c.A.Id],p);lines[c.B.Id]=Trim(lines[c.B.Id],q);
            if(c.Middle is {} middle)
            {
                var m=SketchEntityId.New();points.Add(new(m,middle){Origin=SketchPointOrigin.CurveGenerated});arcs.Add(new(id,p,m,q,c.A.IsConstruction));
                additions.Add(new EndpointTangentConstraint(SketchConstraintId.New(),c.A.Id,id,p));additions.Add(new EndpointTangentConstraint(SketchConstraintId.New(),c.B.Id,id,q));
                if(constrainRadius)additions.Add(new RadiusConstraint(SketchConstraintId.New(),id,radius));
            }
            else extraLines.Add(new(id,p,q,c.A.IsConstruction));
            replacements.Add(new(c.Joint,c.A.Id,c.B.Id,id,p,q));
        }
        var candidate=s with{Points=points.ToImmutable(),Arcs=arcs.ToImmutable(),Lines=s.Lines.Select(l=>lines[l.Id]).Concat(extraLines).ToImmutableArray()};
        var removedPoints=SketchPointUsage.GeneratedOrphans(candidate,corners.Select(c=>c.Joint)).ToHashSet();affected.UnionWith(removedPoints);
        var result=s with{Points=points.Where(p=>!removedPoints.Contains(p.Id)).ToImmutableArray(),Arcs=arcs.ToImmutable(),
            Lines=s.Lines.Select(l=>lines[l.Id]).Concat(extraLines).ToImmutableArray(),
            Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(affected.Contains)||
                c is EndpointTangentConstraint tangent&&!corners.Any(corner=>corner.Joint==tangent.Joint)).Concat(additions).ToImmutableArray()};
        result.Validate();return new(result,replacements.ToImmutable());
    }
}
