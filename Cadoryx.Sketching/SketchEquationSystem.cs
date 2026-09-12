using System.Collections.Immutable;
using Cadoryx.Db;
using MathNet.Numerics.LinearAlgebra;

namespace Cadoryx.Sketching;

internal sealed class SketchEquationSystem
{
    private readonly CadSketch sketch;
    private readonly SketchSolveOptions options;
    private readonly Dictionary<SketchEntityId,int> pointIndices=[],radiusIndices=[];
    private readonly Dictionary<SketchEntityId,SketchLine> lines;
    private readonly Point2d origin;
    private readonly double scale;
    internal double[] Initial {get;}
    internal ImmutableArray<EquationGroup> Groups {get;}
    internal int EquationCount {get;}
    internal bool IsAffine=>Groups.All(g=>g.IsAffine);

    internal SketchEquationSystem(CadSketch sketch,SketchSolveOptions options)
    {
        this.sketch=sketch;this.options=options;lines=sketch.Lines.ToDictionary(l=>l.Id);
        origin=sketch.Points.IsEmpty?default:sketch.Points[0].Position;
        scale=Math.Max(1,sketch.Points.Select(p=>Math.Max(Math.Abs(p.Position.X-origin.X),Math.Abs(p.Position.Y-origin.Y)))
            .Concat(sketch.Circles.Select(c=>c.Radius)).Concat(sketch.Constraints.Select(Size)).DefaultIfEmpty(1).Max());
        if(!double.IsFinite(scale)||scale/options.LinearToleranceMm>1e12)throw new NotSupportedException("Sketch scale exceeds supported numerical precision.");
        var initial=new List<double>();
        foreach(var p in sketch.Points.OrderBy(p=>p.Id.Value))
        {pointIndices.Add(p.Id,initial.Count);initial.Add((p.Position.X-origin.X)/scale);initial.Add((p.Position.Y-origin.Y)/scale);}
        foreach(var c in sketch.Circles.OrderBy(c=>c.Id.Value)){radiusIndices.Add(c.Id,initial.Count);initial.Add(c.Radius/scale);}
        Initial=initial.ToArray();var groups=ImmutableArray.CreateBuilder<EquationGroup>();int offset=0;
        foreach(var c in sketch.Constraints.Where(c=>c.IsEnabled).OrderBy(c=>c.Id.Value))
        {
            int count=c is FixPointConstraint or CoincidentConstraint?2:1;
            bool affine=c is FixPointConstraint or CoincidentConstraint or HorizontalConstraint or VerticalConstraint or OffsetXConstraint or OffsetYConstraint or RadiusConstraint or EqualRadiusConstraint;
            groups.Add(new(c,offset,count,affine));offset+=count;
        }
        EquationCount=offset;Groups=groups.ToImmutable();
    }
    private double Size(SketchConstraint constraint)=>constraint switch
    {
        FixPointConstraint f=>Math.Max(Math.Abs(f.Position.X-origin.X),Math.Abs(f.Position.Y-origin.Y)),
        DistanceConstraint d=>d.Distance,LengthConstraint l=>l.Length,OffsetXConstraint x=>Math.Abs(x.Offset),
        OffsetYConstraint y=>Math.Abs(y.Offset),RadiusConstraint r=>r.Radius,_=>0
    };
    private Point2d Point(double[] x,SketchEntityId id){int i=pointIndices[id];return new(x[i],x[i+1]);}
    private Point2d Direction(double[] x,SketchEntityId id)
    {var l=lines[id];var a=Point(x,l.Start);var b=Point(x,l.End);return new(b.X-a.X,b.Y-a.Y);}
    private static double Length(Point2d d)=>double.Hypot(d.X,d.Y);
    internal double[] Residual(double[] x)
    {
        var residual=new double[EquationCount];double tolerance=options.LinearToleranceMm/scale;
        foreach(var g in Groups)
        {
            double a=0,b=0;bool angle=false;
            Point2d Delta(SketchEntityId first,SketchEntityId second){var p=Point(x,first);var q=Point(x,second);return new(q.X-p.X,q.Y-p.Y);}
            switch(g.Constraint)
            {
                case FixPointConstraint f:var p=Point(x,f.Point);a=p.X-(f.Position.X-origin.X)/scale;b=p.Y-(f.Position.Y-origin.Y)/scale;break;
                case CoincidentConstraint c:var d=Delta(c.A,c.B);a=d.X;b=d.Y;break;
                case HorizontalConstraint h:a=Direction(x,h.Line).Y;break;
                case VerticalConstraint v:a=Direction(x,v.Line).X;break;
                case OffsetXConstraint c:a=Delta(c.A,c.B).X-c.Offset/scale;break;
                case OffsetYConstraint c:a=Delta(c.A,c.B).Y-c.Offset/scale;break;
                case DistanceConstraint c:a=Length(Delta(c.A,c.B))-c.Distance/scale;break;
                case LengthConstraint c:a=Length(Direction(x,c.Line))-c.Length/scale;break;
                case EqualLengthConstraint c:a=Length(Direction(x,c.A))-Length(Direction(x,c.B));break;
                case RadiusConstraint c:a=x[radiusIndices[c.Circle]]-c.Radius/scale;break;
                case EqualRadiusConstraint c:a=x[radiusIndices[c.A]]-x[radiusIndices[c.B]];break;
                case ParallelConstraint c:{var u=Direction(x,c.A);var w=Direction(x,c.B);a=(u.X*w.Y-u.Y*w.X)/(Length(u)*Length(w));angle=true;break;}
                case PerpendicularConstraint c:{var u=Direction(x,c.A);var w=Direction(x,c.B);a=(u.X*w.X+u.Y*w.Y)/(Length(u)*Length(w));angle=true;break;}
                default:throw new NotSupportedException("Unknown constraint equation.");
            }
            residual[g.Start]=a/(angle?options.AngularTolerance:tolerance);
            if(g.Count==2)residual[g.Start+1]=b/tolerance;
        }
        return residual;
    }
    internal Matrix<double> Jacobian(double[] x,CancellationToken token)
    {
        var matrix=Matrix<double>.Build.Dense(EquationCount,x.Length);
        double linear=scale/options.LinearToleranceMm;
        foreach(var group in Groups)
        {
            token.ThrowIfCancellationRequested();int row=group.Start;
            void PointGradient(SketchEntityId id,double dx,double dy,int targetRow)
            {int i=pointIndices[id];matrix[targetRow,i]+=dx;matrix[targetRow,i+1]+=dy;}
            void Difference(SketchEntityId a,SketchEntityId b,double dx,double dy,int targetRow)
            {PointGradient(a,-dx,-dy,targetRow);PointGradient(b,dx,dy,targetRow);}
            void Distance(SketchEntityId a,SketchEntityId b,double factor)
            {
                var p=Point(x,a);var q=Point(x,b);double dx=q.X-p.X,dy=q.Y-p.Y,length=double.Hypot(dx,dy);
                // At coincident points a positive distance has no preferred direction; report nonconvergence.
                if(length>0)Difference(a,b,dx/length*factor,dy/length*factor,row);
            }
            void LineLength(SketchEntityId id,double factor){var l=lines[id];Distance(l.Start,l.End,factor);}
            void DirectionGradient(SketchEntityId id,double dx,double dy){var l=lines[id];Difference(l.Start,l.End,dx,dy,row);}
            void Relation(SketchEntityId a,SketchEntityId b,bool parallel)
            {
                var u=Direction(x,a);var v=Direction(x,b);double ul=Length(u),vl=Length(v),denominator=ul*vl;
                double value=parallel?(u.X*v.Y-u.Y*v.X)/denominator:(u.X*v.X+u.Y*v.Y)/denominator;
                var du=parallel?new Point2d(v.Y,-v.X):v;var dv=parallel?new Point2d(-u.Y,u.X):u;
                double weight=1/options.AngularTolerance;
                DirectionGradient(a,(du.X/denominator-value*u.X/(ul*ul))*weight,(du.Y/denominator-value*u.Y/(ul*ul))*weight);
                DirectionGradient(b,(dv.X/denominator-value*v.X/(vl*vl))*weight,(dv.Y/denominator-value*v.Y/(vl*vl))*weight);
            }
            switch(group.Constraint)
            {
                case FixPointConstraint c:PointGradient(c.Point,linear,0,row);PointGradient(c.Point,0,linear,row+1);break;
                case CoincidentConstraint c:Difference(c.A,c.B,linear,0,row);Difference(c.A,c.B,0,linear,row+1);break;
                case OffsetXConstraint c:Difference(c.A,c.B,linear,0,row);break;
                case OffsetYConstraint c:Difference(c.A,c.B,0,linear,row);break;
                case HorizontalConstraint c:DirectionGradient(c.Line,0,linear);break;
                case VerticalConstraint c:DirectionGradient(c.Line,linear,0);break;
                case DistanceConstraint c:Distance(c.A,c.B,linear);break;
                case LengthConstraint c:LineLength(c.Line,linear);break;
                case EqualLengthConstraint c:LineLength(c.A,linear);LineLength(c.B,-linear);break;
                case RadiusConstraint c:matrix[row,radiusIndices[c.Circle]]=linear;break;
                case EqualRadiusConstraint c:matrix[row,radiusIndices[c.A]]=linear;matrix[row,radiusIndices[c.B]]=-linear;break;
                case ParallelConstraint c:Relation(c.A,c.B,true);break;
                case PerpendicularConstraint c:Relation(c.A,c.B,false);break;
                default:throw new NotSupportedException("Unknown constraint derivative.");
            }
        }
        return matrix;
    }
    internal CadSketch Apply(double[] x)=>sketch with
    {
        Points=sketch.Points.Select(p=>p with{Position=new(x[pointIndices[p.Id]]*scale+origin.X,x[pointIndices[p.Id]+1]*scale+origin.Y)}).ToImmutableArray(),
        Circles=sketch.Circles.Select(c=>c with{Radius=x[radiusIndices[c.Id]]*scale}).ToImmutableArray()
    };
    internal double[] Coordinates(CadSketch value)
    {
        var x=new double[Initial.Length];
        foreach(var p in value.Points){int i=pointIndices[p.Id];x[i]=(p.Position.X-origin.X)/scale;x[i+1]=(p.Position.Y-origin.Y)/scale;}
        foreach(var c in value.Circles)x[radiusIndices[c.Id]]=c.Radius/scale;
        return x;
    }
    internal bool Admissible(double[] x)=>x.All(double.IsFinite)&&radiusIndices.Values.All(i=>x[i]*scale>1e-10);
    internal sealed record EquationGroup(SketchConstraint Constraint,int Start,int Count,bool IsAffine);
}
