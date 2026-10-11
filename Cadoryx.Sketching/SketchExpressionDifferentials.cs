using System.Collections.Immutable;
using Cadoryx.Db;
using Scalar=Cadoryx.Sketching.SketchContactDifferentials.Scalar;
using Vector=Cadoryx.Sketching.SketchContactDifferentials.Vector;
using Geometry=Cadoryx.Sketching.SketchContactDifferentials.Geometry;

namespace Cadoryx.Sketching;

/// <summary>Forward analytic author-coordinate and parameter sensitivities of exact expressions.
/// Taylor coefficients carry full solver gradients; no curve fitting or finite differences.</summary>
internal sealed partial class SketchExpressionDifferentials
{
    private readonly CadSketch sketch;
    private readonly double[] coordinates;
    private readonly IReadOnlyDictionary<SketchEntityId,int> points,radii;
    private readonly Point2d origin;
    private readonly double scale;
    private readonly SketchRegionSolveChart? regionChart;
    private readonly Dictionary<SketchEntityId,Node> authors=[];
    private readonly HashSet<SketchEntityId> active=[];
    private sealed record Node(SketchCurveExpressionKind Kind,Scalar First,Scalar Last,bool Reversed,double Distance,
        Node? Basis,Func<Scalar,int,bool,Vector[]>? Primitive);

    private SketchExpressionDifferentials(CadSketch sketch,double[] coordinates,IReadOnlyDictionary<SketchEntityId,int> points,
        IReadOnlyDictionary<SketchEntityId,int> radii,Point2d origin,double scale,SketchRegionSolveChart? regionChart)
    {this.sketch=sketch;this.coordinates=coordinates;this.points=points;this.radii=radii;this.origin=origin;this.scale=scale;this.regionChart=regionChart;}

    internal static Geometry Evaluate(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] coordinates,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,int order)
        =>EvaluateContact(sketch,curve,parameter,coordinates,points,radii,origin,scale,order,true);

    internal static Geometry EvaluateContact(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] coordinates,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,int order,bool includeCurvature)
        =>EvaluateRegionContact(sketch,curve,parameter,coordinates,points,radii,origin,scale,order,includeCurvature,null);

    internal static Geometry EvaluateRegionContact(CadSketch sketch,SketchEntityId curve,Scalar parameter,double[] coordinates,
        IReadOnlyDictionary<SketchEntityId,int> points,IReadOnlyDictionary<SketchEntityId,int> radii,
        Point2d origin,double scale,int order,bool includeCurvature,SketchRegionSolveChart? regionChart)
    {
        if(order is <1 or >3||parameter.Gradient.Length!=coordinates.Length||!double.IsFinite(parameter.Value)||parameter.Value<0||parameter.Value>1)
            throw new CadValidationException("Invalid exact expression differential order or parameter.");
        CadGuard.Positive(scale);CadGuard.Finite(origin.X,origin.Y);
        var author=sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve)??throw new CadValidationException("Select an exact expression entity.");
        author.Geometry.Validate();if(parameter.Value>0&&parameter.Value<1)author.Geometry.RequireDerivativeContinuity(parameter.Value,order);
        var context=new SketchExpressionDifferentials(sketch,coordinates,points,radii,origin,scale,regionChart);var node=context.Author(curve);
        var span=node.Last-node.First;var native=node.Reversed?node.Last-span*parameter:node.First+span*parameter;
        var jet=context.Jet(node,native,includeCurvature?2:1,native.Value==node.Last.Value);var direction=span*(node.Reversed?-1:1);
        var position=jet[0]*(1/scale);var first=jet[1]*direction*(1/scale);
        var speed=Scalar.Sqrt(first.X*first.X+first.Y*first.Y);
        if(!double.IsFinite(speed.Value)||speed.Value*scale<=1e-7)throw new CadValidationException("Expression differential has a degenerate tangent.");
        var unit=first/speed;var curvature=context.C(0);
        if(includeCurvature)
        {var second=jet[2]*direction*direction*(2/scale);curvature=(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);}
        foreach(var value in new[]{position.X,position.Y,unit.X,unit.Y,curvature,speed})context.Finite(value);
        return new(position,unit,curvature,speed);
    }
    private Scalar C(double value)=>Scalar.Constant(value,coordinates.Length);
    private Vector Zero()=>new(C(0),C(0));
    private Vector Fixed(Point2d point)=>new(C(point.X-origin.X),C(point.Y-origin.Y));
    private Vector P(SketchEntityId id)
    {int i=points[id];return new(Scalar.Variable(coordinates[i],i,coordinates.Length)*scale,Scalar.Variable(coordinates[i+1],i+1,coordinates.Length)*scale);}
    private void Finite(Scalar scalar)
    {if(!double.IsFinite(scalar.Value)||!scalar.Gradient.All(double.IsFinite))throw new CadValidationException("Exact expression differential overflow or singularity.");}
    private Node Plain(Scalar first,Scalar last,bool reversed,Func<Scalar,int,bool,Vector[]> primitive)=>
        new(SketchCurveExpressionKind.Primitive,first,last,reversed,0,null,primitive);
    private Node Static(SketchCurveExpressionGeometry geometry)=>new(geometry.Kind,C(geometry.FirstParameter),C(geometry.LastParameter),
        geometry.Reversed,geometry.SignedOffset,geometry.Basis is {} basis?Static(basis):null,
        geometry.Primitive is {} primitive?FixedPrimitive(primitive):null);

    private Node Author(SketchEntityId id)
    {
        if(authors.TryGetValue(id,out var cached))return cached;
        if(!active.Add(id)||active.Count>SketchCurveExpressionGeometry.MaximumDepth)
            throw new CadValidationException("Expression differential source cycle or author depth exceeded.");
        try
        {
            Node result;
            if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==id) is {} expression)
            {
                expression.Geometry.Validate();
                var region=sketch.RegionOffsets.SingleOrDefault(g=>g.Loops.Any(l=>l.Outputs.Any(o=>o.Curve==id)));
                if(region is {IsFrozen:false})result=RegionAuthor(region,id);
                else if(expression.Source is not {IsFrozen:false} source)result=Static(expression.Geometry);
                else
                {
                    var basis=Author(source.Entity);var path=new List<SketchCurveExpressionGeometry>();var node=expression.Geometry;
                    for(int i=0;i<source.BasisDepth;i++){path.Add(node);node=node.Basis??throw new CadValidationException("Incomplete expression differential source path.");}
                    for(int i=path.Count-1;i>=0;i--)
                    {
                        var operation=path[i];var oldBasis=operation.Basis!;
                        bool whole=operation.FirstParameter==oldBasis.FirstParameter&&operation.LastParameter==oldBasis.LastParameter;
                        var first=whole?basis.First:C(operation.FirstParameter);var last=whole?basis.Last:C(operation.LastParameter);
                        if(first.Value>=last.Value||operation.Kind==SketchCurveExpressionKind.Offset&&
                            (first.Value<basis.First.Value||last.Value>basis.Last.Value))
                            throw new CadValidationException("A differential source no longer contains the finite native author window.");
                        basis=new(operation.Kind,first,last,operation.Reversed^oldBasis.Reversed^basis.Reversed,operation.SignedOffset,basis,null);
                    }
                    result=basis;
                }
            }
            else if(sketch.Lines.SingleOrDefault(c=>c.Id==id) is {} line)
                result=Line(P(line.Start),P(line.End));
            else if(sketch.Circles.SingleOrDefault(c=>c.Id==id) is {} circle)
            {
                int index=radii[circle.Id];var radius=Scalar.Variable(coordinates[index],index,coordinates.Length)*scale;
                if(radius.Value<=1e-7)throw new CadValidationException("A differential source circle has invalid radius.");
                result=Conic(P(circle.Center),new(radius,C(0)),new(C(0),radius),C(0),C(Math.Tau),false);
            }
            else if(sketch.Arcs.SingleOrDefault(c=>c.Id==id) is {} arc)result=Arc(P(arc.Start),P(arc.Middle),P(arc.End),
                sketch.Points.Single(p=>p.Id==arc.Start).Position,sketch.Points.Single(p=>p.Id==arc.Middle).Position,sketch.Points.Single(p=>p.Id==arc.End).Position);
            else if(sketch.Beziers.SingleOrDefault(c=>c.Id==id) is {} bezier)
            {
                var data=new SketchFreeformGeometry(SketchFreeformKind.Bezier,[default,default,default],[],[],[],2,false,0,1);
                result=Plain(C(0),C(1),false,(u,n,upper)=>Rational(data,[P(bezier.Start),P(bezier.Control),P(bezier.End)],u,n,upper));
            }
            else if(sketch.Splines.SingleOrDefault(c=>c.Id==id) is {} spline)
            {
                var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(p=>sketch.Points.Single(v=>v.Id==p).Position).ToImmutableArray(),spline.Knots);
                var data=new SketchFreeformGeometry(SketchFreeformKind.BSpline,definition.Controls,[],definition.Knots,definition.Multiplicities,3,false,definition.Knots[0],definition.Knots[^1]);
                result=Plain(C(data.FirstParameter),C(data.LastParameter),false,(u,n,upper)=>Rational(data,spline.Controls.Select(P).ToArray(),u,n,upper));
            }
            else result=Static(SketchExpressionEntities.Author(sketch,id).Geometry);
            authors.Add(id,result);return result;
        }
        finally{active.Remove(id);}
    }
    private Node Line(Vector start,Vector end)
    {
        var delta=end-start;var length=Scalar.Sqrt(delta.X*delta.X+delta.Y*delta.Y);
        if(length.Value<=1e-7)throw new CadValidationException("A differential source line degenerates.");
        var direction=delta/length;
        return Plain(C(0),length,false,(u,n,_)=>{var result=Enumerable.Range(0,n+1).Select(_=>Zero()).ToArray();result[0]=start+direction*u;if(n>0)result[1]=direction;return result;});
    }
    private Node Arc(Vector start,Vector middle,Vector end,Point2d oldStart,Point2d oldMiddle,Point2d oldEnd)
    {
        var a=middle-start;var b=end-start;var cross=a.X*b.Y-a.Y*b.X;
        double previous=(oldMiddle.X-oldStart.X)*(oldEnd.Y-oldStart.Y)-(oldMiddle.Y-oldStart.Y)*(oldEnd.X-oldStart.X);
        var av=a.X*a.X+a.Y*a.Y;var bv=b.X*b.X+b.Y*b.Y;
        var offset=new Vector((av*b.Y-bv*a.Y)/(cross*2),(bv*a.X-av*b.X)/(cross*2));var center=start+offset;
        var radius=Scalar.Sqrt(offset.X*offset.X+offset.Y*offset.Y);var from=start-center;var to=end-center;
        var angle=Scalar.Atan2(from.Y,from.X);var sweep=Scalar.Atan2(to.Y,to.X)-angle;
        if(cross.Value*previous<=0||radius.Value<=1e-7)throw new CadValidationException("A differential source arc changed its author branch.");
        if(cross.Value>0&&sweep.Value<=0)sweep+=Math.Tau;if(cross.Value<0&&sweep.Value>=0)sweep-=Math.Tau;
        var first=sweep.Value<0?angle+sweep:angle;var last=sweep.Value<0?angle:angle+sweep;
        return Conic(center,new(radius,C(0)),new(C(0),radius),first,last,sweep.Value<0);
    }
    private Node Conic(Vector center,Vector cosine,Vector sine,Scalar first,Scalar last,bool reversed)=>
        Plain(first,last,reversed,(u,n,_)=>
        {
            var result=new Vector[n+1];var cos=Scalar.Cos(u);var sin=Scalar.Sin(u);double factorial=1;
            for(int k=0;k<=n;k++)
            {if(k>0)factorial*=k;var a=(k%4) switch{0=>cos,1=>-sin,2=>-cos,_=>sin};var b=(k%4) switch{0=>sin,1=>cos,2=>-sin,_=>-cos};result[k]=(cosine*a+sine*b)*(1/factorial);}
            result[0]+=center;return result;
        });
    private Func<Scalar,int,bool,Vector[]> FixedPrimitive(SketchBoundaryCurve primitive)
    {
        if(primitive.Ellipse is {} ellipse)return Conic(Fixed(ellipse.Center),new(C(ellipse.Cosine.X),C(ellipse.Cosine.Y)),
            new(C(ellipse.Sine.X),C(ellipse.Sine.Y)),C(0),C(1),false).Primitive!;
        if(primitive.Middle is {} middle)
        {var arc=SketchArcGeometry.Through(primitive.Start,middle,primitive.End);return Conic(Fixed(arc.Center),new(C(arc.Radius),C(0)),new(C(0),C(arc.Radius)),C(0),C(1),false).Primitive!;}
        SketchFreeformGeometry? data=primitive.Freeform;
        if(primitive.BezierControl is {} control)data=new(SketchFreeformKind.Bezier,[primitive.Start,control,primitive.End],[],[],[],2,false,0,1);
        if(!primitive.SplineControls.IsEmpty)
        {var d=primitive.SplineDefinition;data=new(SketchFreeformKind.BSpline,d.Controls,[],d.Knots,d.Multiplicities,3,false,d.Knots[0],d.Knots[^1]);}
        if(data is {} g)return(u,n,upper)=>Rational(g,g.Poles.Select(Fixed).ToArray(),u,n,upper);
        return Line(Fixed(primitive.Start),Fixed(primitive.End)).Primitive!;
    }
}
