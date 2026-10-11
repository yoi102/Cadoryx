using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Analytical edits against finite straight cutters, without moving shared endpoints.</summary>
public static class SketchLineEditing
{
    private const double Clearance=1e-7;
    public static CadSketch Trim(CadSketch sketch,SketchEntityId id,Point2d click)
    {
        var (line,a,b,t)=Target(sketch,id,click);
        double epsilon=Clearance/Distance(a,b);
        var cuts=Intersections(sketch,line,a,b).Where(v=>v>epsilon&&v<1-epsilon).Order().ToArray();
        if(cuts.Length==0)throw new CadValidationException("The line has no interior straight-line intersection to trim.");
        if(cuts.Any(v=>Math.Abs(v-t)<=epsilon))throw new CadValidationException("Click inside the segment to remove, away from an intersection.");
        double left=cuts.Where(v=>v<t).DefaultIfEmpty(0).Max();
        double right=cuts.Where(v=>v>t).DefaultIfEmpty(1).Min();
        var intervals=new List<(double Start,double End)>();
        if(left>epsilon)intervals.Add((0,left));
        if(right<1-epsilon)intervals.Add((right,1));
        return Replace(sketch,line,a,b,intervals,intervals.Count==1);
    }
    public static CadSketch Extend(CadSketch sketch,SketchEntityId id,Point2d click)
    {
        var (line,a,b,t)=Target(sketch,id,click);
        double epsilon=Clearance/Distance(a,b);
        if(Math.Abs(t-.5)<=epsilon)throw new CadValidationException("Click closer to the endpoint to extend.");
        bool start=t<.5;
        var cuts=Intersections(sketch,line,a,b).Where(v=>start?v < -epsilon:v > 1+epsilon).ToArray();
        if(cuts.Length==0)throw new CadValidationException("No finite straight-line boundary meets the extension ray.");
        double cut=start?cuts.Max():cuts.Min();
        return Replace(sketch,line,a,b,[(start?cut:0,start?1:cut)],true);
    }
    private static (SketchLine Line,Point2d A,Point2d B,double T) Target(CadSketch sketch,SketchEntityId id,Point2d click)
    {
        CadGuard.Finite(click.X,click.Y);
        if(sketch.Projections.Any(p=>p.TargetEntity==id))throw new CadValidationException("Detach projected geometry before editing it.");
        var line=sketch.Lines.SingleOrDefault(l=>l.Id==id)??throw new CadValidationException("Select a straight sketch line.");
        var a=sketch.Points.Single(p=>p.Id==line.Start).Position;var b=sketch.Points.Single(p=>p.Id==line.End).Position;
        double dx=b.X-a.X,dy=b.Y-a.Y;
        return(line,a,b,Math.Clamp(((click.X-a.X)*dx+(click.Y-a.Y)*dy)/(dx*dx+dy*dy),0,1));
    }
    private static IEnumerable<double> Intersections(CadSketch sketch,SketchLine target,Point2d a,Point2d b)
    {
        double dx=b.X-a.X,dy=b.Y-a.Y,length=Distance(a,b);
        foreach(var cutter in sketch.Lines.Where(l=>l.Id!=target.Id))
        {
            var c=sketch.Points.Single(p=>p.Id==cutter.Start).Position;var d=sketch.Points.Single(p=>p.Id==cutter.End).Position;
            double ex=d.X-c.X,ey=d.Y-c.Y,cx=c.X-a.X,cy=c.Y-a.Y,den=dx*ey-dy*ex;
            if(Math.Abs(den)<=1e-12*length*Distance(c,d))
            {
                if(Math.Abs(cx*dy-cy*dx)<=Clearance*length)
                    throw new CadValidationException("Collinear boundaries are ambiguous; select or edit them separately.");
                continue;
            }
            double u=(cx*dy-cy*dx)/den;
            if(u>=-Clearance/Distance(c,d)&&u<=1+Clearance/Distance(c,d))yield return(cx*ey-cy*ex)/den;
        }
    }
    private static CadSketch Replace(CadSketch sketch,SketchLine line,Point2d a,Point2d b,
        IReadOnlyList<(double Start,double End)> intervals,bool retainId)
    {
        var points=sketch.Points;var lines=sketch.Lines.Remove(line);
        SketchEntityId At(double t)
        {
            if(t==0)return line.Start;if(t==1)return line.End;
            var position=new Point2d(a.X+t*(b.X-a.X),a.Y+t*(b.Y-a.Y));
            // Only actual boundary endpoints can share identity. Never choose among duplicate point IDs.
            var ends=sketch.Lines.Where(l=>l.Id!=line.Id).SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
            var matches=points.Where(p=>ends.Contains(p.Id)&&Distance(p.Position,position)<=Clearance).ToArray();
            if(matches.Length>1)throw new CadValidationException("Intersection has multiple endpoint identities; merge or select them explicitly first.");
            if(matches.Length==1)return matches[0].Id;
            var id=SketchEntityId.New();points=points.Add(new(id,position){Origin=SketchPointOrigin.CurveGenerated});return id;
        }
        foreach(var (start,end) in intervals)
            lines=lines.Add(new(retainId?line.Id:SketchEntityId.New(),At(start),At(end),line.IsConstruction));
        var removed=new HashSet<SketchEntityId>{line.Id};
        foreach(var id in SketchPointUsage.GeneratedOrphans(sketch with{Points=points,Lines=lines},[line.Start,line.End]))
        {points=points.Where(p=>p.Id!=id).ToImmutableArray();removed.Add(id);}
        return sketch with{Points=points,Lines=lines,
            Constraints=sketch.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)).ToImmutableArray()};
    }
    private static double Distance(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}
