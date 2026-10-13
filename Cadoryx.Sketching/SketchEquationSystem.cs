using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using MathNet.Numerics.LinearAlgebra;

namespace Cadoryx.Sketching;

internal sealed partial class SketchEquationSystem
{
    private readonly CadSketch sketch;
    private readonly SketchSolveOptions options;
    private readonly Dictionary<SketchEntityId,int> pointIndices=[],radiusIndices=[];
    private readonly Dictionary<SketchEntityId,SketchLine> lines;
    private readonly Dictionary<SketchConstraintId,double> tangentSigns=[];
    private readonly Dictionary<SketchEntityId,SketchArc> arcs;
    private readonly Dictionary<SketchEntityId,double> arcSigns=[];
    private readonly Dictionary<SketchEntityId,double> parametricSigns=[];
    private readonly Dictionary<SketchEntityId,SketchBezier> beziers;
    private readonly Dictionary<SketchEntityId,SketchSpline> splines;
    private readonly EndpointTangentConstraint[] endpointTangencies;
    private readonly CircularContactConstraint[] circularContacts;
    private readonly FiniteParametricContactConstraint[] finiteContacts;
    private readonly Dictionary<(SketchConstraintId Id,bool A),(int Index,BoundedContactCoordinate Bounds)> contactParameters=[];
    private readonly Dictionary<(SketchEntityId Curve,double Parameter),ImmutableArray<(SketchEntityId Point,double Position,double First,double Second)>> finiteWeights=[];
    private readonly Dictionary<(SketchEntityId Curve,SketchEntityId Joint),ImmutableArray<(SketchEntityId Point,double First,double Second)>> curvatureWeights=[];
    private readonly HashSet<SketchEntityId> curvatureCurves=[];
    private readonly Dictionary<SketchEntityId,Point2d> prescribedPoints;
    private readonly Dictionary<SketchEntityId,double> prescribedRadii;
    private readonly Dictionary<int,double> prescribedCoordinates=[];
    private readonly Point2d origin;
    private readonly double scale;
    private readonly SketchRegionEvaluationContext? regionContext;
    private readonly SketchRegionSolveChart? regionChart;
    private readonly CancellationToken regionToken;
    private double[]? evaluatedCoordinates;
    private CadSketch? evaluatedSketch;
    internal double[] Initial {get;}
    internal ImmutableArray<EquationGroup> Groups {get;}
    internal int EquationCount {get;}
    internal bool IsAffine=>Groups.All(g=>g.IsAffine);
    internal bool RequiresRowEquilibration=>curvatureCurves.Count>0||finiteContacts.Length>0||regionChart is not null;

    internal SketchEquationSystem(CadSketch sketch,SketchSolveOptions options):this(sketch,options,null,default){}
    internal SketchEquationSystem(CadSketch sketch,SketchSolveOptions options,SketchRegionEvaluationContext? regionContext,CancellationToken regionToken=default)
    {
        this.regionContext=regionContext;this.regionToken=regionToken;
        if(!sketch.RegionOffsets.IsEmpty&&regionContext is null)throw new NotSupportedException("Whole-region equations require a complete exact evaluator.");
        this.sketch=sketch;this.options=options;regionChart=regionContext is null?null:SketchRegionSolveChart.TryCreateForAllEquations(sketch);lines=sketch.Lines.ToDictionary(l=>l.Id);
        arcs=sketch.Arcs.ToDictionary(a=>a.Id);endpointTangencies=sketch.Constraints.OfType<EndpointTangentConstraint>().Where(c=>c.IsEnabled)
            .Concat(sketch.Constraints.OfType<EndpointCurvatureConstraint>().Where(c=>c.IsEnabled)
                .Select(c=>new EndpointTangentConstraint(c.Id,c.A,c.B,c.Joint,c.Branch))).ToArray();
        foreach(var c in sketch.Constraints.OfType<EndpointCurvatureConstraint>().Where(c=>c.IsEnabled))
            foreach(var curve in new[]{c.A,c.B})
            {
                curvatureCurves.Add(curve);
                if(!arcs.ContainsKey(curve)&&!sketch.ExpressionCurves.Any(e=>e.Id==curve))
                    curvatureWeights.TryAdd((curve,c.Joint),SketchEndpointCurvature.DerivativeWeights(sketch,curve,c.Joint));
            }
        circularContacts=sketch.Constraints.OfType<CircularContactConstraint>().Where(c=>c.IsEnabled).ToArray();
        finiteContacts=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Where(c=>c.IsEnabled).ToArray();
        foreach(var c in finiteContacts)foreach(var target in new[]{(c.A,c.ParameterA),(c.B,c.ParameterB)})
        {
            if(SketchFiniteParametricContact.HasControlWeights(sketch,target.Item1))
                finiteWeights.TryAdd(target,SketchFiniteParametricContact.DerivativeWeights(sketch,target.Item1,target.Item2));
            curvatureCurves.Add(target.Item1);
        }
        beziers=sketch.Beziers.ToDictionary(b=>b.Id);splines=sketch.Splines.ToDictionary(s=>s.Id);
        origin=sketch.Points.IsEmpty?default:sketch.Points[0].Position;
        scale=Math.Max(1,sketch.Points.Select(p=>Math.Max(Math.Abs(p.Position.X-origin.X),Math.Abs(p.Position.Y-origin.Y)))
            .Concat(sketch.Circles.Select(c=>c.Radius))
            .Concat(sketch.Ellipses.Select(e=>Math.Max(Math.Abs(e.Geometry.Center.X-origin.X)+Math.Abs(e.Geometry.Cosine.X)+Math.Abs(e.Geometry.Sine.X),
                Math.Abs(e.Geometry.Center.Y-origin.Y)+Math.Abs(e.Geometry.Cosine.Y)+Math.Abs(e.Geometry.Sine.Y))))
            .Concat(sketch.Freeforms.SelectMany(e=>e.Geometry.Poles).Select(p=>Math.Max(Math.Abs(p.X-origin.X),Math.Abs(p.Y-origin.Y))))
            .Concat(sketch.Constraints.Select(Size)).DefaultIfEmpty(1).Max());
        if(!double.IsFinite(scale)||scale/options.LinearToleranceMm>1e12)throw new NotSupportedException("Sketch scale exceeds supported numerical precision.");
        var initial=new List<double>();
        foreach(var p in sketch.Points.OrderBy(p=>p.Id.Value))
        {pointIndices.Add(p.Id,initial.Count);initial.Add((p.Position.X-origin.X)/scale);initial.Add((p.Position.Y-origin.Y)/scale);}
        foreach(var c in sketch.Circles.OrderBy(c=>c.Id.Value)){radiusIndices.Add(c.Id,initial.Count);initial.Add(c.Radius/scale);}
        foreach(var c in finiteContacts.OrderBy(c=>c.Id.Value))
            foreach(bool a in new[]{true,false})
                if((a?c.SlidingA:c.SlidingB) is {} author)
                {
                    var bounds=new BoundedContactCoordinate(author.Lower,author.Upper);
                    contactParameters.Add((c.Id,a),(initial.Count,bounds));initial.Add(bounds.Coordinate(author.Seed));
                }
        var authoredPoints=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        foreach(var id in sketch.Constraints.OfType<EndpointTangentConstraint>().Where(c=>c.IsEnabled).SelectMany(c=>new[]{c.A,c.B}).Distinct().Where(id=>beziers.ContainsKey(id)||splines.ContainsKey(id)))
        {
            var ends=SketchEndpointTangency.Endpoints(sketch,id);var control=SketchEndpointTangency.TangentNeighbor(sketch,id,ends.Start);
            var a=authoredPoints[ends.Start];var b=authoredPoints[ends.End];var p=authoredPoints[control];
            parametricSigns[id]=Math.Sign((b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X));
        }
        var dimensionArcs=sketch.Constraints.Where(c=>c.IsEnabled).SelectMany(c=>c switch
        {RadiusConstraint r=>new[]{r.Circle},EqualRadiusConstraint r=>new[]{r.A,r.B},_=>Array.Empty<SketchEntityId>()}).ToHashSet();
        foreach(var arc in arcs.Values.Where(a=>dimensionArcs.Contains(a.Id)||endpointTangencies.Any(c=>c.A==a.Id||c.B==a.Id)||circularContacts.Any(c=>c.A==a.Id||c.B==a.Id)||finiteContacts.Any(c=>c.A==a.Id||c.B==a.Id)))
        {
            var p=authoredPoints[arc.Start];var m=authoredPoints[arc.Middle];var e=authoredPoints[arc.End];
            arcSigns[arc.Id]=Math.Sign((m.X-p.X)*(e.Y-p.Y)-(m.Y-p.Y)*(e.X-p.X));
        }
        foreach(var t in sketch.Constraints.OfType<TangentConstraint>())
        {
            var line=lines[t.Line];var center=authoredPoints[sketch.Circles.Single(c=>c.Id==t.Circle).Center];
            var a=authoredPoints[line.Start];var b=authoredPoints[line.End];
            tangentSigns[t.Id]=(b.X-a.X)*(center.Y-a.Y)-(b.Y-a.Y)*(center.X-a.X)<0?-1:1;
        }
        Initial=initial.ToArray();var groups=ImmutableArray.CreateBuilder<EquationGroup>();int offset=0;
        // Fully prescribed endpoint authors have no free source coordinate.
        // Keep their exact explicit values while solving generated endpoints;
        // all relation/structural rows and final native replay still remain.
        // Partially prescribed authors and general contacts retain the coupled solve.
        bool structuralOnly=sketch.ExpressionCurves.Any(c=>c.Endpoints is not null)&&
            (sketch.Constraints.Where(c=>c.IsEnabled).All(c=>c is FixPointConstraint||c is RadiusConstraint r&&radiusIndices.ContainsKey(r.Circle))||
             FullyPrescribedEndpointAuthors(sketch));
        prescribedPoints=sketch.Constraints.OfType<FixPointConstraint>().Where(c=>c.IsEnabled&&structuralOnly)
            .GroupBy(c=>c.Point).Where(g=>g.Select(c=>c.Position).Distinct().Count()==1)
            .ToDictionary(g=>g.Key,g=>g.First().Position);
        prescribedRadii=sketch.Constraints.OfType<RadiusConstraint>().Where(c=>c.IsEnabled&&structuralOnly&&radiusIndices.ContainsKey(c.Circle))
            .GroupBy(c=>c.Circle).Where(g=>g.Select(c=>c.Radius).Distinct().Count()==1)
            .ToDictionary(g=>g.Key,g=>g.First().Radius);
        foreach(var(id,p) in prescribedPoints)
        {prescribedCoordinates[pointIndices[id]]=(p.X-origin.X)/scale;prescribedCoordinates[pointIndices[id]+1]=(p.Y-origin.Y)/scale;}
        foreach(var(id,r) in prescribedRadii)prescribedCoordinates[radiusIndices[id]]=r/scale;
        foreach(var c in sketch.Constraints.Where(c=>c.IsEnabled).Concat(SketchAssociativeEndpoints.Equations(sketch)).OrderBy(c=>c.Id.Value))
        {
            int count=c is FiniteParametricContactConstraint f?(f.Continuity==FiniteContactContinuity.Curvature?4:3):c is FixPointConstraint or CoincidentConstraint or MidpointConstraint or SymmetryConstraint or EndpointCurvatureConstraint or SketchExpressionEndpointEquation?2:1;
            bool affine=c is FixPointConstraint or CoincidentConstraint or MidpointConstraint or HorizontalConstraint or VerticalConstraint or OffsetXConstraint or OffsetYConstraint or RadiusConstraint or EqualRadiusConstraint;
            if(c is RadiusConstraint r&&arcs.ContainsKey(r.Circle)||c is EqualRadiusConstraint e&&(arcs.ContainsKey(e.A)||arcs.ContainsKey(e.B)))affine=false;
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
    private SketchContactDifferentials.Geometry FiniteGeometry(double[] x,SketchEntityId curve,double parameter,FiniteContactContinuity continuity)
        =>SketchContactDifferentials.EvaluateWithChart(EvaluationSketch(x),curve,parameter,x,pointIndices,radiusIndices,origin,scale,finiteWeights,continuity,regionChart);
    private SketchContactDifferentials.Geometry ContactGeometry(double[] x,FiniteParametricContactConstraint contact,bool a)
    {
        var curve=a?contact.A:contact.B;double fixedParameter=a?contact.ParameterA:contact.ParameterB;
        if(!contactParameters.TryGetValue((contact.Id,a),out var entry))return FiniteGeometry(x,curve,fixedParameter,contact.Continuity);
        if(!entry.Bounds.TryEvaluate(x[entry.Index],entry.Index,x.Length,out var parameter))
            throw new CadValidationException("Contact coordinate rounded outside its strict allowed interval.");
        return SketchContactDifferentials.EvaluateParameterWithChart(EvaluationSketch(x),curve,parameter,x,pointIndices,radiusIndices,origin,scale,contact.Continuity,regionChart);
    }
    private SketchContactDifferentials.Scalar[] ContactResiduals(double[] x,FiniteParametricContactConstraint contact)
    {
        var a=ContactGeometry(x,contact,true);var b=ContactGeometry(x,contact,false);
        double tolerance=options.LinearToleranceMm/scale;
        var positionX=(a.Position.X-b.Position.X)/tolerance;var positionY=(a.Position.Y-b.Position.Y)/tolerance;
        var tangent=(a.Unit.X*b.Unit.Y-a.Unit.Y*b.Unit.X)/options.AngularTolerance;
        if(contact.Continuity!=FiniteContactContinuity.Curvature)return [positionX,positionY,tangent];
        double sign=contact.Branch==FiniteContactTangentBranch.SameDirection?1:-1;
        return [positionX,positionY,tangent,(a.Curvature-b.Curvature*sign)/(options.CurvatureTolerancePerMm*scale)];
    }
    // Circumradius in normalized coordinates. Its logarithmic derivative avoids differentiating a solved center.
    private (double Value,(SketchEntityId Id,Point2d Gradient)[] Gradients) CircularRadius(double[] x,SketchEntityId id)
    {
        if(radiusIndices.TryGetValue(id,out int index))return(x[index],[]);
        var arc=arcs[id];var a=Point(x,arc.Start);var b=Point(x,arc.Middle);var c=Point(x,arc.End);
        var u=new Point2d(b.X-a.X,b.Y-a.Y);var v=new Point2d(c.X-a.X,c.Y-a.Y);var w=new Point2d(c.X-b.X,c.Y-b.Y);
        double u2=u.X*u.X+u.Y*u.Y,v2=v.X*v.X+v.Y*v.Y,w2=w.X*w.X+w.Y*w.Y,cross=u.X*v.Y-u.Y*v.X;
        double radius=Math.Sqrt(u2)*Math.Sqrt(v2)*Math.Sqrt(w2)/(2*Math.Abs(cross));
        var da=new Point2d(radius*(-u.X/u2-v.X/v2-(u.Y-v.Y)/cross),radius*(-u.Y/u2-v.Y/v2-(v.X-u.X)/cross));
        var db=new Point2d(radius*(u.X/u2-w.X/w2-v.Y/cross),radius*(u.Y/u2-w.Y/w2+v.X/cross));
        var dc=new Point2d(radius*(v.X/v2+w.X/w2+u.Y/cross),radius*(v.Y/v2+w.Y/w2-u.X/cross));
        return(radius,[(arc.Start,da),(arc.Middle,db),(arc.End,dc)]);
    }
    private (Point2d Value,(SketchEntityId Id,Point2d Dx,Point2d Dy)[] Gradients) CircularCenter(double[] x,SketchEntityId id)
    {
        if(radiusIndices.ContainsKey(id))
        {
            var center=sketch.Circles.Single(c=>c.Id==id).Center;
            return(Point(x,center),[(center,new(1,0),new(0,1))]);
        }
        var arc=arcs[id];var a=Point(x,arc.Start);var b=Point(x,arc.Middle);var c=Point(x,arc.End);
        var u=new Point2d(b.X-a.X,b.Y-a.Y);var v=new Point2d(c.X-a.X,c.Y-a.Y);
        double u2=u.X*u.X+u.Y*u.Y,v2=v.X*v.X+v.Y*v.Y,den=2*(u.X*v.Y-u.Y*v.X);
        double nx=u2*v.Y-v2*u.Y,ny=u.X*v2-v.X*u2;
        Point2d Derivative(Point2d da,Point2d db,Point2d dc)
        {
            var du=new Point2d(db.X-da.X,db.Y-da.Y);var dv=new Point2d(dc.X-da.X,dc.Y-da.Y);
            double du2=2*(u.X*du.X+u.Y*du.Y),dv2=2*(v.X*dv.X+v.Y*dv.Y);
            double dd=2*(du.X*v.Y+u.X*dv.Y-du.Y*v.X-u.Y*dv.X);
            double dnx=du2*v.Y+u2*dv.Y-dv2*u.Y-v2*du.Y;
            double dny=du.X*v2+u.X*dv2-dv.X*u2-v.X*du2;
            return new(da.X+(dnx-nx/den*dd)/den,da.Y+(dny-ny/den*dd)/den);
        }
        Point2d ex=new(1,0),ey=new(0,1),zero=default;
        return(new(a.X+nx/den,a.Y+ny/den),[
            (arc.Start,Derivative(ex,zero,zero),Derivative(ey,zero,zero)),
            (arc.Middle,Derivative(zero,ex,zero),Derivative(zero,ey,zero)),
            (arc.End,Derivative(zero,zero,ex),Derivative(zero,zero,ey))]);
    }
    private (Point2d Unit,(SketchEntityId Id,Point2d Dx,Point2d Dy)[] Gradients) EndpointTangent(double[] x,SketchEntityId curve,SketchEntityId joint)
    {
        var endpoint=Point(x,joint);Point2d tangent;
        var derivatives=new List<(SketchEntityId Id,Point2d Dx,Point2d Dy)>();
        if(!arcs.ContainsKey(curve))
        {
            var other=SketchEndpointTangency.TangentNeighbor(sketch,curve,joint);var end=Point(x,other);
            tangent=new(end.X-endpoint.X,end.Y-endpoint.Y);
            derivatives.Add((other,new(1,0),new(0,1)));derivatives.Add((joint,new(-1,0),new(0,-1)));
        }
        else
        {
            var arc=arcs[curve];var other=arc.Start==joint?arc.End:arc.Start;
            var m=Point(x,arc.Middle);var e=Point(x,other);var u=new Point2d(m.X-endpoint.X,m.Y-endpoint.Y);var v=new Point2d(e.X-endpoint.X,e.Y-endpoint.Y);
            tangent=SketchEndpointTangency.ArcTangentNumerator(u,v);
            double u2=u.X*u.X+u.Y*u.Y,v2=v.X*v.X+v.Y*v.Y;
            var ux=new Point2d(v2-2*u.X*v.X,-2*u.X*v.Y);var uy=new Point2d(-2*u.Y*v.X,v2-2*u.Y*v.Y);
            var vx=new Point2d(2*v.X*u.X-u2,2*v.X*u.Y);var vy=new Point2d(2*v.Y*u.X,2*v.Y*u.Y-u2);
            derivatives.Add((arc.Middle,ux,uy));derivatives.Add((other,vx,vy));
            derivatives.Add((joint,new(-ux.X-vx.X,-ux.Y-vx.Y),new(-uy.X-vy.X,-uy.Y-vy.Y)));
        }
        double length=Length(tangent);var unit=new Point2d(tangent.X/length,tangent.Y/length);
        Point2d NormalizeDerivative(Point2d d)
        {double dot=unit.X*d.X+unit.Y*d.Y;return new((d.X-unit.X*dot)/length,(d.Y-unit.Y*dot)/length);}
        return(unit,derivatives.Select(d=>(d.Id,NormalizeDerivative(d.Dx),NormalizeDerivative(d.Dy))).ToArray());
    }
    private (double Value,(SketchEntityId Id,Point2d Gradient)[] Gradients) EndpointCurvature(double[] x,SketchEntityId curve,SketchEntityId joint)
    {
        if(arcs.TryGetValue(curve,out var arc))
        {
            // Trial residuals can be degenerate or nonfinite; admissibility separately
            // preserves the authored arc orientation before accepting a step.
            double sign=arcSigns[curve]*(joint==arc.Start?1:-1);
            var radius=CircularRadius(x,curve);double inverse=1/radius.Value;
            return(sign*inverse,radius.Gradients.Select(g=>(g.Id,new Point2d(-sign*inverse*inverse*g.Gradient.X,-sign*inverse*inverse*g.Gradient.Y))).ToArray());
        }
        var weights=curvatureWeights[(curve,joint)];var origin=Point(x,joint);Point2d first=default,second=default;
        foreach(var w in weights)
        {
            var p=Point(x,w.Point);double dx=p.X-origin.X,dy=p.Y-origin.Y;
            first=new(first.X+w.First*dx,first.Y+w.First*dy);second=new(second.X+w.Second*dx,second.Y+w.Second*dy);
        }
        double length=Length(first),inverseCube=1/(length*length*length);
        double k=(first.X*second.Y-first.Y*second.X)*inverseCube;
        var df=new Point2d(second.Y*inverseCube-3*k*first.X/(length*length),-second.X*inverseCube-3*k*first.Y/(length*length));
        var ds=new Point2d(-first.Y*inverseCube,first.X*inverseCube);
        return(k,weights.Select(w=>(w.Point,new Point2d(w.First*df.X+w.Second*ds.X,w.First*df.Y+w.Second*ds.Y))).ToArray());
    }
    internal double[] Residual(double[] x)
    {
        var residual=new double[EquationCount];double tolerance=options.LinearToleranceMm/scale;
        foreach(var g in Groups)
        {
            double a=0,b=0;bool angle=false;
            Point2d Delta(SketchEntityId first,SketchEntityId second){var p=Point(x,first);var q=Point(x,second);return new(q.X-p.X,q.Y-p.Y);}
            switch(g.Constraint)
            {
                case SketchExpressionEndpointEquation c:
                {
                    var values=StructuralResiduals(x,c);for(int i=0;i<values.Length;i++)residual[g.Start+i]=values[i].Value;continue;
                }
                case EndpointTangentConstraint c when SketchAssociativeEndpoints.HasExpressionTarget(sketch,c):
                {var values=ExpressionEndpointContactResiduals(x,c.A,c.B,c.Joint,false);residual[g.Start]=values[0].Value;continue;}
                case EndpointCurvatureConstraint c when SketchAssociativeEndpoints.HasExpressionTarget(sketch,c):
                {var values=ExpressionEndpointContactResiduals(x,c.A,c.B,c.Joint,true);for(int i=0;i<values.Length;i++)residual[g.Start+i]=values[i].Value;continue;}
                case FiniteParametricContactConstraint c:
                {
                    var values=ContactResiduals(x,c);
                    for(int i=0;i<values.Length;i++)residual[g.Start+i]=values[i].Value;
                    continue;
                }
                case FixPointConstraint f:var p=Point(x,f.Point);a=p.X-(f.Position.X-origin.X)/scale;b=p.Y-(f.Position.Y-origin.Y)/scale;break;
                case CoincidentConstraint c:var d=Delta(c.A,c.B);a=d.X;b=d.Y;break;
                case MidpointConstraint c:{var line=lines[c.Line];var target=Point(x,c.Point);var start=Point(x,line.Start);var end=Point(x,line.End);
                    a=target.X-(start.X+end.X)/2;b=target.Y-(start.Y+end.Y)/2;break;}
                case SymmetryConstraint c:{var line=lines[c.Axis];var u=Direction(x,c.Axis);var start=Point(x,line.Start);var first=Point(x,c.A);var second=Point(x,c.B);
                    var mid=new Point2d((first.X+second.X)/2-start.X,(first.Y+second.Y)/2-start.Y);var delta=Delta(c.A,c.B);
                    a=(u.X*mid.Y-u.Y*mid.X)/Length(u);b=(u.X*delta.X+u.Y*delta.Y)/Length(u);break;}
                case HorizontalConstraint h:a=Direction(x,h.Line).Y;break;
                case VerticalConstraint v:a=Direction(x,v.Line).X;break;
                case OffsetXConstraint c:a=Delta(c.A,c.B).X-c.Offset/scale;break;
                case OffsetYConstraint c:a=Delta(c.A,c.B).Y-c.Offset/scale;break;
                case DistanceConstraint c:a=Length(Delta(c.A,c.B))-c.Distance/scale;break;
                case LengthConstraint c:a=Length(Direction(x,c.Line))-c.Length/scale;break;
                case EqualLengthConstraint c:a=Length(Direction(x,c.A))-Length(Direction(x,c.B));break;
                case RadiusConstraint c:a=CircularRadius(x,c.Circle).Value-c.Radius/scale;break;
                case EqualRadiusConstraint c:a=CircularRadius(x,c.A).Value-CircularRadius(x,c.B).Value;break;
                case ParallelConstraint c:{var u=Direction(x,c.A);var w=Direction(x,c.B);a=(u.X*w.Y-u.Y*w.X)/(Length(u)*Length(w));angle=true;break;}
                case PerpendicularConstraint c:{var u=Direction(x,c.A);var w=Direction(x,c.B);a=(u.X*w.X+u.Y*w.Y)/(Length(u)*Length(w));angle=true;break;}
                case AngleConstraint c:{var u=Direction(x,c.A);var w=Direction(x,c.B);a=(u.X*w.X+u.Y*w.Y)/(Length(u)*Length(w))-Math.Cos(c.AngleRadians);angle=true;break;}
                case TangentConstraint c:{var line=lines[c.Line];var u=Direction(x,c.Line);var start=Point(x,line.Start);
                    var center=Point(x,sketch.Circles.Single(circle=>circle.Id==c.Circle).Center);
                    a=(u.X*(center.Y-start.Y)-u.Y*(center.X-start.X))/Length(u)-tangentSigns[c.Id]*x[radiusIndices[c.Circle]];break;}
                case CircularContactConstraint c:
                {
                    if(lines.TryGetValue(c.A,out var line)||lines.TryGetValue(c.B,out line))
                    {
                        var curve=line.Id==c.A?c.B:c.A;var u=Direction(x,line.Id);var start=Point(x,line.Start);var center=CircularCenter(x,curve).Value;
                        a=(u.X*(center.Y-start.Y)-u.Y*(center.X-start.X))/Length(u)-
                            (c.Branch==CircularContactBranch.LineLeft?1:-1)*CircularRadius(x,curve).Value;
                    }
                    else
                    {
                        var first=CircularCenter(x,c.A).Value;var second=CircularCenter(x,c.B).Value;
                        double ra=CircularRadius(x,c.A).Value,rb=CircularRadius(x,c.B).Value;
                        a=double.Hypot(second.X-first.X,second.Y-first.Y)-(c.Branch switch
                            {CircularContactBranch.External=>ra+rb,CircularContactBranch.AContainsB=>ra-rb,_=>rb-ra});
                    }
                    break;
                }
                case EndpointTangentConstraint c:{var u=EndpointTangent(x,c.A,c.Joint).Unit;var v=EndpointTangent(x,c.B,c.Joint).Unit;
                    a=u.X*v.Y-u.Y*v.X;angle=true;break;}
                case EndpointCurvatureConstraint c:{var u=EndpointTangent(x,c.A,c.Joint).Unit;var v=EndpointTangent(x,c.B,c.Joint).Unit;
                    a=u.X*v.Y-u.Y*v.X;angle=true;b=EndpointCurvature(x,c.A,c.Joint).Value+EndpointCurvature(x,c.B,c.Joint).Value;break;}
                default:throw new NotSupportedException("Unknown constraint equation.");
            }
            residual[g.Start]=a/(angle?options.AngularTolerance:tolerance);
            if(g.Count==2)residual[g.Start+1]=b/(g.Constraint is EndpointCurvatureConstraint?options.CurvatureTolerancePerMm*scale:tolerance);
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
            void RadiusGradient(SketchEntityId id,double factor)
            {
                if(radiusIndices.TryGetValue(id,out int index))matrix[row,index]+=factor;
                else foreach(var g in CircularRadius(x,id).Gradients)PointGradient(g.Id,g.Gradient.X*factor,g.Gradient.Y*factor,row);
            }
            void CenterGradient(SketchEntityId id,double dx,double dy)
            {
                foreach(var g in CircularCenter(x,id).Gradients)
                    PointGradient(g.Id,dx*g.Dx.X+dy*g.Dx.Y,dx*g.Dy.X+dy*g.Dy.Y,row);
            }
            void DirectionGradient(SketchEntityId id,double dx,double dy){var l=lines[id];Difference(l.Start,l.End,dx,dy,row);}
            void Tangency(SketchEntityId a,SketchEntityId b,SketchEntityId joint)
            {
                var first=EndpointTangent(x,a,joint);var second=EndpointTangent(x,b,joint);
                foreach(var d in first.Gradients)PointGradient(d.Id,
                    (d.Dx.X*second.Unit.Y-d.Dx.Y*second.Unit.X)/options.AngularTolerance,
                    (d.Dy.X*second.Unit.Y-d.Dy.Y*second.Unit.X)/options.AngularTolerance,row);
                foreach(var d in second.Gradients)PointGradient(d.Id,
                    (first.Unit.X*d.Dx.Y-first.Unit.Y*d.Dx.X)/options.AngularTolerance,
                    (first.Unit.X*d.Dy.Y-first.Unit.Y*d.Dy.X)/options.AngularTolerance,row);
            }
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
                case SketchExpressionEndpointEquation c:
                {
                    var values=StructuralResiduals(x,c);for(int i=0;i<values.Length;i++)for(int column=0;column<x.Length;column++)matrix[row+i,column]=values[i].Gradient[column];break;
                }
                case EndpointTangentConstraint c when SketchAssociativeEndpoints.HasExpressionTarget(sketch,c):
                {var values=ExpressionEndpointContactResiduals(x,c.A,c.B,c.Joint,false);for(int column=0;column<x.Length;column++)matrix[row,column]=values[0].Gradient[column];break;}
                case EndpointCurvatureConstraint c when SketchAssociativeEndpoints.HasExpressionTarget(sketch,c):
                {var values=ExpressionEndpointContactResiduals(x,c.A,c.B,c.Joint,true);for(int i=0;i<values.Length;i++)for(int column=0;column<x.Length;column++)matrix[row+i,column]=values[i].Gradient[column];break;}
                case FiniteParametricContactConstraint c:
                {
                    var values=ContactResiduals(x,c);
                    for(int i=0;i<values.Length;i++)for(int column=0;column<x.Length;column++)
                        matrix[row+i,column]=values[i].Gradient[column];
                    break;
                }
                case FixPointConstraint c:PointGradient(c.Point,linear,0,row);PointGradient(c.Point,0,linear,row+1);break;
                case CoincidentConstraint c:Difference(c.A,c.B,linear,0,row);Difference(c.A,c.B,0,linear,row+1);break;
                case MidpointConstraint c:
                {
                    var l=lines[c.Line];
                    PointGradient(c.Point,linear,0,row);PointGradient(l.Start,-linear/2,0,row);PointGradient(l.End,-linear/2,0,row);
                    PointGradient(c.Point,0,linear,row+1);PointGradient(l.Start,0,-linear/2,row+1);PointGradient(l.End,0,-linear/2,row+1);break;
                }
                case SymmetryConstraint c:
                {
                    var l=lines[c.Axis];var u=Direction(x,c.Axis);double length=Length(u),squared=length*length;
                    var start=Point(x,l.Start);var first=Point(x,c.A);var second=Point(x,c.B);
                    var mid=new Point2d((first.X+second.X)/2-start.X,(first.Y+second.Y)/2-start.Y);
                    double cross=(u.X*mid.Y-u.Y*mid.X)/length;
                    double dux=mid.Y/length-cross*u.X/squared,duy=-mid.X/length-cross*u.Y/squared;
                    double dmx=-u.Y/length,dmy=u.X/length;
                    PointGradient(l.Start,(-dux-dmx)*linear,(-duy-dmy)*linear,row);PointGradient(l.End,dux*linear,duy*linear,row);
                    PointGradient(c.A,dmx*linear/2,dmy*linear/2,row);PointGradient(c.B,dmx*linear/2,dmy*linear/2,row);
                    double dx=second.X-first.X,dy=second.Y-first.Y,dot=(u.X*dx+u.Y*dy)/length;
                    dux=dx/length-dot*u.X/squared;duy=dy/length-dot*u.Y/squared;
                    PointGradient(l.Start,-dux*linear,-duy*linear,row+1);PointGradient(l.End,dux*linear,duy*linear,row+1);
                    PointGradient(c.A,-u.X/length*linear,-u.Y/length*linear,row+1);PointGradient(c.B,u.X/length*linear,u.Y/length*linear,row+1);break;
                }
                case OffsetXConstraint c:Difference(c.A,c.B,linear,0,row);break;
                case OffsetYConstraint c:Difference(c.A,c.B,0,linear,row);break;
                case HorizontalConstraint c:DirectionGradient(c.Line,0,linear);break;
                case VerticalConstraint c:DirectionGradient(c.Line,linear,0);break;
                case DistanceConstraint c:Distance(c.A,c.B,linear);break;
                case LengthConstraint c:LineLength(c.Line,linear);break;
                case EqualLengthConstraint c:LineLength(c.A,linear);LineLength(c.B,-linear);break;
                case RadiusConstraint c:RadiusGradient(c.Circle,linear);break;
                case EqualRadiusConstraint c:RadiusGradient(c.A,linear);RadiusGradient(c.B,-linear);break;
                case ParallelConstraint c:Relation(c.A,c.B,true);break;
                case PerpendicularConstraint c:Relation(c.A,c.B,false);break;
                case AngleConstraint c:Relation(c.A,c.B,false);break;
                case TangentConstraint c:
                {
                    var line=lines[c.Line];var u=Direction(x,c.Line);var a=Point(x,line.Start);
                    var centerId=sketch.Circles.Single(circle=>circle.Id==c.Circle).Center;var center=Point(x,centerId);
                    var v=new Point2d(center.X-a.X,center.Y-a.Y);double length=Length(u);
                    double cross=u.X*v.Y-u.Y*v.X;double inverse=1/length;
                    double dux=v.Y*inverse-cross*u.X/(length*length*length);
                    double duy=-v.X*inverse-cross*u.Y/(length*length*length);
                    double dvx=-u.Y*inverse,dvy=u.X*inverse;
                    PointGradient(line.Start,(-dux-dvx)*linear,(-duy-dvy)*linear,row);
                    PointGradient(line.End,dux*linear,duy*linear,row);
                    PointGradient(centerId,dvx*linear,dvy*linear,row);
                    matrix[row,radiusIndices[c.Circle]]=-tangentSigns[c.Id]*linear;
                    break;
                }
                case EndpointCurvatureConstraint c:
                {
                    double curvatureScale=1/(options.CurvatureTolerancePerMm*scale);
                    foreach(var curve in new[]{c.A,c.B})foreach(var d in EndpointCurvature(x,curve,c.Joint).Gradients)
                        PointGradient(d.Id,d.Gradient.X*curvatureScale,d.Gradient.Y*curvatureScale,row+1);
                    Tangency(c.A,c.B,c.Joint);break;
                }
                case EndpointTangentConstraint c: Tangency(c.A,c.B,c.Joint);break;
                case CircularContactConstraint c:
                {
                    if(lines.TryGetValue(c.A,out var line)||lines.TryGetValue(c.B,out line))
                    {
                        var curve=line.Id==c.A?c.B:c.A;var u=Direction(x,line.Id);var start=Point(x,line.Start);var center=CircularCenter(x,curve).Value;
                        var v=new Point2d(center.X-start.X,center.Y-start.Y);double length=Length(u),cross=u.X*v.Y-u.Y*v.X;
                        double dux=v.Y/length-cross*u.X/(length*length*length),duy=-v.X/length-cross*u.Y/(length*length*length);
                        double dvx=-u.Y/length,dvy=u.X/length;
                        PointGradient(line.Start,(-dux-dvx)*linear,(-duy-dvy)*linear,row);PointGradient(line.End,dux*linear,duy*linear,row);
                        CenterGradient(curve,dvx*linear,dvy*linear);RadiusGradient(curve,-(c.Branch==CircularContactBranch.LineLeft?1:-1)*linear);
                    }
                    else
                    {
                        var a=CircularCenter(x,c.A).Value;var b=CircularCenter(x,c.B).Value;double dx=b.X-a.X,dy=b.Y-a.Y,length=double.Hypot(dx,dy);
                        CenterGradient(c.A,-dx/length*linear,-dy/length*linear);CenterGradient(c.B,dx/length*linear,dy/length*linear);
                        RadiusGradient(c.A,(c.Branch==CircularContactBranch.BContainsA?1:-1)*linear);
                        RadiusGradient(c.B,(c.Branch==CircularContactBranch.AContainsB?1:-1)*linear);
                    }
                    break;
                }
                default:throw new NotSupportedException("Unknown constraint derivative.");
            }
        }
        return matrix;
    }
    internal CadSketch Apply(double[] x)=>sketch with
    {
        Points=sketch.Points.Select(p=>p with{Position=new(
            WorldCoordinate(x,pointIndices[p.Id],p.Position.X,origin.X),WorldCoordinate(x,pointIndices[p.Id]+1,p.Position.Y,origin.Y))}).ToImmutableArray(),
        Circles=sketch.Circles.Select(c=>c with{Radius=WorldCoordinate(x,radiusIndices[c.Id],c.Radius,0)}).ToImmutableArray(),
        Constraints=contactParameters.Count==0?sketch.Constraints:sketch.Constraints.Select(c=>c is FiniteParametricContactConstraint f&&f.IsEnabled?
            f with{ParameterA=SolvedParameter(x,f,true),ParameterB=SolvedParameter(x,f,false)}:c).ToImmutableArray()
    };
    internal CadSketch RestorePrescribedValues(CadSketch candidate)
    {
        // Reapply the exact structural-system prescriptions after denormalization.
        // The caller still rechecks every residual and branch.
        if(prescribedCoordinates.Count==0)return candidate;
        return candidate with
        {
            Points=candidate.Points.Select(p=>prescribedPoints.TryGetValue(p.Id,out var value)?p with{Position=value}:p).ToImmutableArray(),
            Circles=candidate.Circles.Select(c=>prescribedRadii.TryGetValue(c.Id,out double value)?c with{Radius=value}:c).ToImmutableArray()
        };
    }
    internal void PrescribeCoordinates(double[] x)
    {foreach(var(index,value) in prescribedCoordinates)x[index]=value;}
    internal Matrix<double> IterationJacobian(double[] x,CancellationToken token)
    {
        var matrix=Jacobian(x,token);
        // These variables and their equations remain in the budget and final rank.
        // Their exact singleton prescriptions are eliminated only from the step.
        foreach(int column in prescribedCoordinates.Keys)matrix.SetColumn(column,new double[matrix.RowCount]);
        return matrix;
    }
    // Normalization is not exactly invertible in floating point. An unchanged
    // solver coordinate retains the author's value, with no tolerance or snap.
    private double WorldCoordinate(double[] x,int index,double authored,double offset)
        =>x[index]==Initial[index]?authored:x[index]*scale+offset;
    private double SolvedParameter(double[] x,FiniteParametricContactConstraint contact,bool a)
    {
        if(!contactParameters.TryGetValue((contact.Id,a),out var entry))return a?contact.ParameterA:contact.ParameterB;
        if(!entry.Bounds.TryEvaluate(x[entry.Index],entry.Index,x.Length,out var parameter))throw new CadValidationException("Invalid solved contact coordinate.");
        return parameter.Value;
    }
    internal double[] Coordinates(CadSketch value)
    {
        var x=new double[Initial.Length];
        foreach(var p in value.Points){int i=pointIndices[p.Id];x[i]=(p.Position.X-origin.X)/scale;x[i+1]=(p.Position.Y-origin.Y)/scale;}
        foreach(var c in value.Circles)x[radiusIndices[c.Id]]=c.Radius/scale;
        foreach(var c in value.Constraints.OfType<FiniteParametricContactConstraint>())foreach(bool a in new[]{true,false})
            if(contactParameters.TryGetValue((c.Id,a),out var entry))x[entry.Index]=entry.Bounds.Coordinate(a?c.ParameterA:c.ParameterB);
        return x;
    }
    internal bool Admissible(double[] x)
    {
        if(!x.All(double.IsFinite)||radiusIndices.Values.Any(i=>x[i]*scale<=1e-10))return false;
        if(regionContext is not null)
        {try{_=EvaluationSketch(x);}catch(CadValidationException){return false;}}
        foreach(var (id,sign) in arcSigns)
        {
            var arc=arcs[id];var a=Point(x,arc.Start);var m=Point(x,arc.Middle);var b=Point(x,arc.End);
            double ab=double.Hypot(m.X-a.X,m.Y-a.Y),bc=double.Hypot(b.X-m.X,b.Y-m.Y),ca=double.Hypot(a.X-b.X,a.Y-b.Y);
            double cross=(m.X-a.X)*(b.Y-a.Y)-(m.Y-a.Y)*(b.X-a.X);
            if(Math.Min(ab,Math.Min(bc,ca))*scale<=1e-7||cross*sign<=1e-9*Math.Max(ab*bc,Math.Max(bc*ca,ca*ab)))return false;
        }
        foreach(var (id,sign) in parametricSigns)
        {
            Point2d World(SketchEntityId point){var p=Point(x,point);return new(p.X*scale,p.Y*scale);}
            var ends=SketchEndpointTangency.Endpoints(sketch,id);var a=World(ends.Start);var b=World(ends.End);
            var p=World(SketchEndpointTangency.TangentNeighbor(sketch,id,ends.Start));
            if(((b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X))*sign<=0)return false;
            try
            {
                if(beziers.TryGetValue(id,out var bezier))SketchBezierGeometry.Validate(a,World(bezier.Control),b);
                else SketchSplineGeometry.Validate(splines[id].Controls.Select(World).ToImmutableArray(),splines[id].Knots);
            }
            catch(CadValidationException){return false;}
        }
        foreach(var c in endpointTangencies)
        {
            if(SketchAssociativeEndpoints.HasExpressionTarget(sketch,c))
            {
                try
                {
                    var ga=EndpointScalarGeometry(x,c.A,c.Joint,false);var gb=EndpointScalarGeometry(x,c.B,c.Joint,false);
                    double dot=ga.Unit.X.Value*gb.Unit.X.Value+ga.Unit.Y.Value*gb.Unit.Y.Value;
                    if(!double.IsFinite(dot)||dot>=0)return false;
                }
                catch(CadValidationException){return false;}
                continue;
            }
            foreach(var id in new[]{c.A,c.B}.Where(id=>beziers.ContainsKey(id)||splines.ContainsKey(id)))
            {
                var p=Point(x,c.Joint);var q=Point(x,SketchEndpointTangency.TangentNeighbor(sketch,id,c.Joint));
                if(double.Hypot(q.X-p.X,q.Y-p.Y)*scale<=1e-7)return false;
            }
            var a=EndpointTangent(x,c.A,c.Joint).Unit;var b=EndpointTangent(x,c.B,c.Joint).Unit;
            if(!double.IsFinite(a.X+b.X+a.Y+b.Y)||a.X*b.X+a.Y*b.Y>=0)return false;
        }
        foreach(var curve in curvatureCurves.Where(id=>!arcs.ContainsKey(id)&&!lines.ContainsKey(id)))
        {
            Point2d World(SketchEntityId point){var p=Point(x,point);return new(p.X*scale,p.Y*scale);}
            try
            {
                if(beziers.TryGetValue(curve,out var b))SketchBezierGeometry.Validate(World(b.Start),World(b.Control),World(b.End));
                else if(splines.TryGetValue(curve,out var s))SketchSplineGeometry.Validate(s.Controls.Select(World).ToImmutableArray(),s.Knots);
            }
            catch(CadValidationException){return false;}
        }
        foreach(var c in circularContacts)
        {
            if(lines.TryGetValue(c.A,out var line)||lines.TryGetValue(c.B,out line))
            {if(Length(Direction(x,line.Id))*scale<=1e-10)return false;}
            else
            {
                var a=CircularCenter(x,c.A).Value;var b=CircularCenter(x,c.B).Value;
                if(double.Hypot(b.X-a.X,b.Y-a.Y)*scale<=options.LinearToleranceMm)return false;
                double ra=CircularRadius(x,c.A).Value,rb=CircularRadius(x,c.B).Value;
                if(c.Branch==CircularContactBranch.AContainsB&&(ra-rb)*scale<=options.LinearToleranceMm||
                    c.Branch==CircularContactBranch.BContainsA&&(rb-ra)*scale<=options.LinearToleranceMm)return false;
            }
        }
        foreach(var c in finiteContacts)
        {
            try
            {
                var ga=ContactGeometry(x,c,true);var gb=ContactGeometry(x,c,false);
                foreach(var geometry in new[]{ga,gb})
                    if(!double.IsFinite(geometry.Speed.Value)||geometry.Speed.Value*scale<=1e-7||!double.IsFinite(geometry.Curvature.Value))return false;
                double dot=ga.Unit.X.Value*gb.Unit.X.Value+ga.Unit.Y.Value*gb.Unit.Y.Value;
                if(!double.IsFinite(dot)||(c.Branch==FiniteContactTangentBranch.SameDirection?dot<=0:dot>=0))return false;
            }
            catch(CadValidationException){return false;}
        }
        return true;
    }
    internal sealed record EquationGroup(SketchConstraint Constraint,int Start,int Count,bool IsAffine);
}
