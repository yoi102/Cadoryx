using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSlidingContactDerivativeTests
{
    private static double Difference(Func<double,double> f,double t,double h)=>
        (8*(f(t+h)-f(t-h))-(f(t+2*h)-f(t-2*h)))/(12*h);
    private static void Near(double actual,double expected)=>
        Assert.InRange(Math.Abs(actual-expected),0,1e-6*Math.Max(1,Math.Abs(expected)));

    [Theory]
    [InlineData("Circle",false)][InlineData("Arc",false)][InlineData("Arc",true)]
    [InlineData("Ellipse",false)][InlineData("Ellipse",true)]
    [InlineData("RationalBezier",false)][InlineData("RationalBezier",true)]
    [InlineData("RationalSpline",false)][InlineData("RationalSpline",true)]
    [InlineData("Periodic",false)][InlineData("Periodic",true)]
    [InlineData("Line",false)][InlineData("Bezier",false)][InlineData("Bezier",true)]
    [InlineData("Spline",false)][InlineData("Spline",true)]
    public void AnalyticThirdAndSignedCurvatureDerivativeMatchIndependentNativeOracle(string kind,bool reverse)
    {
        var(sketch,id,curve)=SketchMixedFiniteContactTests.Curve(kind,reverse);
        using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);
        var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter;
        double sign=curve.Reversed?-1:1,scale=range*sign;
        foreach(double t in new[]{.07,.27,.57,.91})
        {
            var g=SketchFiniteParametricContact.EvaluateThirdDerivatives(sketch,id,t);
            double u=interval.FirstParameter+(curve.Reversed?1-t:t)*range,h=range*1e-4;
            var native=edge.EvaluateEdgeDerivatives(u);
            Near(g.Position.X,native.Point.X);Near(g.Position.Y,native.Point.Y);
            Near(g.First.X,native.FirstDerivative.X*scale);Near(g.First.Y,native.FirstDerivative.Y*scale);
            Near(g.Second.X,native.SecondDerivative.X*scale*scale);Near(g.Second.Y,native.SecondDerivative.Y*scale*scale);
            double ThirdX(double step)=>Difference(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.X,u,step)*scale*scale*scale;
            double ThirdY(double step)=>Difference(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.Y,u,step)*scale*scale*scale;
            Near(ThirdX(h),ThirdX(h/2));Near(ThirdY(h),ThirdY(h/2));
            Near(g.Third.X,ThirdX(h/2));Near(g.Third.Y,ThirdY(h/2));
            double speed=double.Hypot(g.First.X,g.First.Y),cross=g.First.X*g.Second.Y-g.First.Y*g.Second.X;
            double derivative=(g.First.X*g.Third.Y-g.First.Y*g.Third.X)/Math.Pow(speed,3)-
                3*cross*(g.First.X*g.Second.X+g.First.Y*g.Second.Y)/Math.Pow(speed,5);
            double Curvature(double parameter)
            {
                var j=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(curve.Reversed?1-parameter:parameter)*range);
                double v=double.Hypot(j.FirstDerivative.X,j.FirstDerivative.Y);
                return sign*(j.FirstDerivative.X*j.SecondDerivative.Y-j.FirstDerivative.Y*j.SecondDerivative.X)/Math.Pow(v,3);
            }
            Near(derivative,Difference(Curvature,t,1e-4));
        }
    }

    [Theory][InlineData(1,false)][InlineData(1,true)][InlineData(2,false)][InlineData(2,true)]
    public void LowerDegreeRationalThirdDerivativeIsNotDiscarded(int degree,bool reverse)
    {
        var geometry=new SketchFreeformGeometry(SketchFreeformKind.Bezier,
            degree==1?[new(8,0),new(18,6)]:[new(8,0),new(12,7),new(18,0)],
            degree==1?[1,.6]:[1,.65,1.2],[],[],degree,false,.2,.85,reverse);
        var native=SketchCurve2d.Bezier(geometry.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),geometry.Weights).Trim(.2,.85);
        if(reverse)native=native.Reverse();
        using var edge=SketchModeling.CreateEdge(native,SketchPlane.XY);
        var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter,scale=range*(reverse?-1:1);
        foreach(double t in new[]{.07,.27,.57,.91})
        {
            var g=geometry.EvaluateThirdDerivatives(t);Assert.True(double.Hypot(g.Third.X,g.Third.Y)>1e-3);
            double u=interval.FirstParameter+(reverse?1-t:t)*range,h=range*1e-4;
            Near(g.Third.X,Difference(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.X,u,h)*scale*scale*scale);
            Near(g.Third.Y,Difference(v=>edge.EvaluateEdgeDerivatives(v).SecondDerivative.Y,u,h)*scale*scale*scale);
        }
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void RationalThirdDerivativePreservesTranslationAndFiniteDirection(int kind)
    {
        var g=SketchFreeformGeometryTests.Geometry(kind);
        var shifted=g with{Poles=g.Poles.Select(p=>new Point2d(p.X+1e9,p.Y-1e9)).ToImmutableArray()};
        foreach(double t in new[]{.07,.27,.57,.91})
        {
            var a=g.EvaluateThirdDerivatives(t);var b=shifted.EvaluateThirdDerivatives(t);var reverse=g.Reverse().EvaluateThirdDerivatives(1-t);
            Near(a.Third.X,b.Third.X);Near(a.Third.Y,b.Third.Y);
            Near(a.First.X,-reverse.First.X);Near(a.Second.X,reverse.Second.X);Near(a.Third.X,-reverse.Third.X);
            Near(a.First.Y,-reverse.First.Y);Near(a.Second.Y,reverse.Second.Y);Near(a.Third.Y,-reverse.Third.Y);
        }
    }

    [Fact]
    public void G2MovingParameterRejectsC2KnotAndPeriodicSeamWhileFixedG2RemainsValid()
    {
        var(sketch,id,_)=SketchMixedFiniteContactTests.Curve("Spline");
        var g=SketchFiniteParametricContact.EvaluateDerivatives(sketch,id,.25,2);Assert.True(double.IsFinite(g.Second.X));
        Assert.Throws<CadValidationException>(()=>SketchFiniteParametricContact.EvaluateThirdDerivatives(sketch,id,.25));
        // Periodic cubic seam is C2. A shifted finite interval puts that seam strictly inside.
        var periodic=SketchFreeformGeometryTests.Geometry(3) with{FirstParameter=3.5,LastParameter=4.5};
        periodic.RequireDerivativeContinuity(.5,2);
        Assert.Throws<CadValidationException>(()=>periodic.RequireDerivativeContinuity(.5,3));
        // Quartic single knots prove C3 even at the knot itself.
        var quartic=SketchFreeformGeometryTests.Geometry(2);
        quartic.RequireDerivativeContinuity((.35-.1)/.8,3);
        Assert.True(double.IsFinite(quartic.EvaluateThirdDerivatives((.35-.1)/.8).Third.X));
    }

    [Fact]
    public void AllCubicControlThirdWeightsMatchIndependentParameterAndControlDifferences()
    {
        var(sketch,id,_)=SketchMixedFiniteContactTests.Curve("Spline");
        foreach(double t in new[]{.07,.27,.57,.91})
        {
            var third=SketchFiniteParametricContact.ThirdDerivativeWeights(sketch,id,t);
            Near(third.Sum(w=>w.Position),1);Near(third.Sum(w=>w.First),0);Near(third.Sum(w=>w.Second),0);Near(third.Sum(w=>w.Third),0);
            for(int i=0;i<third.Length;i++)
            {
                int index=i;
                Near(third[i].Third,Difference(u=>SketchFiniteParametricContact.DerivativeWeights(sketch,id,u)[index].Second,t,1e-5));
                var baseline=SketchFiniteParametricContact.EvaluateThirdDerivatives(sketch,id,t);
                var p=sketch.Points.Single(p=>p.Id==third[i].Point);
                var moved=sketch with{Points=sketch.Points.Replace(p,p with{Position=new(p.Position.X+.001,p.Position.Y-.002)})};
                var changed=SketchFiniteParametricContact.EvaluateThirdDerivatives(moved,id,t);
                Near((changed.Third.X-baseline.Third.X)/.001,third[i].Third);
                Near((changed.Third.Y-baseline.Third.Y)/-.002,third[i].Third);
            }
        }
    }

    [Theory][InlineData(0d)][InlineData(1d)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void MovingContactJetRejectsInvalidFiniteParameter(double parameter)
    {
        var(sketch,id,_)=SketchMixedFiniteContactTests.Curve("Circle");
        Assert.Throws<CadValidationException>(()=>SketchFiniteParametricContact.EvaluateThirdDerivatives(sketch,id,parameter));
    }

    [Theory]
    [InlineData("Circle",false)][InlineData("Arc",false)][InlineData("Arc",true)]
    [InlineData("Ellipse",false)][InlineData("Ellipse",true)]
    [InlineData("RationalBezier",false)][InlineData("RationalBezier",true)]
    [InlineData("RationalSpline",false)][InlineData("RationalSpline",true)]
    [InlineData("Periodic",false)][InlineData("Periodic",true)]
    [InlineData("Line",false)][InlineData("Bezier",false)][InlineData("Bezier",true)]
    [InlineData("Spline",false)][InlineData("Spline",true)]
    public void AllParameterControlAndRadiusJacobianColumnsMatchIndependentDomainDifferences(string kind,bool reverse)
    {
        var(sketch,id,_)=SketchMixedFiniteContactTests.Curve(kind,reverse);
        const double scale=40,factor=.7;var origin=new Point2d(2,-3);
        var x=new List<double>();var points=new Dictionary<SketchEntityId,int>();var radii=new Dictionary<SketchEntityId,int>();
        foreach(var p in sketch.Points)
        {points.Add(p.Id,x.Count);x.Add((p.Position.X-origin.X)/scale);x.Add((p.Position.Y-origin.Y)/scale);}
        foreach(var c in sketch.Circles){radii.Add(c.Id,x.Count);x.Add(c.Radius/scale);}
        int parameterIndex=x.Count;x.Add(.37/factor);var coordinates=x.ToArray();
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!;
        var scalar=type.GetNestedType("Scalar",BindingFlags.NonPublic)!;
        var gradient=new double[coordinates.Length];gradient[parameterIndex]=factor;
        var parameter=Activator.CreateInstance(scalar,[.37,gradient])!;
        var result=type.GetMethod("EvaluateParameter",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,
            [sketch,id,parameter,coordinates,points,radii,origin,scale,FiniteContactContinuity.Curvature])!;
        object Property(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o)!;
        object[] values=[Property(Property(result,"Position"),"X"),Property(Property(result,"Position"),"Y"),
            Property(Property(result,"Unit"),"X"),Property(Property(result,"Unit"),"Y"),Property(result,"Curvature"),Property(result,"Speed")];
        double[] Domain(double[] c)
        {
            var candidate=sketch with
            {
                Points=sketch.Points.Select(p=>{int i=points[p.Id];return p with{Position=new(c[i]*scale+origin.X,c[i+1]*scale+origin.Y)};}).ToImmutableArray(),
                Circles=sketch.Circles.Select(r=>r with{Radius=c[radii[r.Id]]*scale}).ToImmutableArray()
            };
            double t=c[parameterIndex]*factor;
            var g=SketchFiniteParametricContact.EvaluateGeometry(candidate,id,t,2);
            var d=SketchFiniteParametricContact.EvaluateDerivatives(candidate,id,t,2);
            return[(g.Position.X-origin.X)/scale,(g.Position.Y-origin.Y)/scale,g.Tangent.X,g.Tangent.Y,
                g.CurvaturePerMm*scale,double.Hypot(d.First.X,d.First.Y)/scale];
        }
        var baseline=Domain(coordinates);
        for(int row=0;row<values.Length;row++)
        {
            Near((double)Property(values[row],"Value"),baseline[row]);
            var actual=(double[])Property(values[row],"Gradient");
            for(int column=0;column<coordinates.Length;column++)
            {
                const double h=1e-5;
                double At(double value){var c=(double[])coordinates.Clone();c[column]=value;return Domain(c)[row];}
                Near(actual[column],Difference(At,coordinates[column],h));
            }
        }
    }

    [Theory][InlineData(FiniteContactContinuity.Tangent,false)][InlineData(FiniteContactContinuity.Curvature,true)]
    public void G1MovingParameterAllowsC2KnotButG2RequiresThirdOrderProof(FiniteContactContinuity continuity,bool rejected)
    {
        var geometry=SketchFreeformGeometryTests.Geometry(3) with{FirstParameter=3.5,LastParameter=4.5};
        var sketch=CadSketch.Create(DefinitionId.New(),"Periodic seam",RigidTransform3d.Identity);
        var curve=new SketchFreeform(SketchEntityId.New(),geometry);sketch=sketch with{Freeforms=[curve]};
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!;
        var scalar=type.GetNestedType("Scalar",BindingFlags.NonPublic)!;
        var parameter=Activator.CreateInstance(scalar,[.5,new[]{1d}])!;
        void Evaluate()=>type.GetMethod("EvaluateParameter",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,
            [sketch,curve.Id,parameter,new[]{.5},new Dictionary<SketchEntityId,int>(),new Dictionary<SketchEntityId,int>(),default(Point2d),1d,continuity]);
        if(rejected)Assert.IsType<CadValidationException>(Assert.Throws<TargetInvocationException>(Evaluate).InnerException);
        else Evaluate();
    }
}
