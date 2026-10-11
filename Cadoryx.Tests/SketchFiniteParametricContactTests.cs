using System.Collections.Immutable;
using Cadoryx.Db;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchFiniteParametricContactTests
{
    [Theory]
    [InlineData(1,false,.01)][InlineData(1,true,.01)][InlineData(1,false,1)][InlineData(1,true,1)][InlineData(1,false,1000)][InlineData(1,true,1000)]
    [InlineData(2,false,.01)][InlineData(2,true,.01)][InlineData(2,false,1)][InlineData(2,true,1)][InlineData(2,false,1000)][InlineData(2,true,1000)]
    [InlineData(3,false,.01)][InlineData(3,true,.01)][InlineData(3,false,1)][InlineData(3,true,1)][InlineData(3,false,1000)][InlineData(3,true,1000)]
    public void ExactInteriorBasisAndDerivativeWeightsMatchNativeOracle(int interiorMultiplicity,bool reversed,double scale)
    {
        int count=4+interiorMultiplicity;double angle=.7,translation=1_000_000;
        var points=Enumerable.Range(0,count).Select(i=>
        {
            double x=i,y=i%3-1;
            return new Point2d(translation+scale*(x*Math.Cos(angle)-y*Math.Sin(angle)),translation+scale*(x*Math.Sin(angle)+y*Math.Cos(angle)));
        }).ToImmutableArray();
        var definition=new CubicSplineDefinition(points,[2,2.75,5],[4,interiorMultiplicity,4]);if(reversed)definition=definition.Reverse();
        var curve=SketchCurve2d.BSpline(definition.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),definition.Knots,definition.Multiplicities,3);
        using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);var interval=edge.GetEdgeCurveSnapshot();
        foreach(double t in new[]{0,.01,.13,.24999,.25001,.47,.83,.99,1})
        {
            var basis=definition.BasisDerivatives(t);var origin=definition.Controls[0];
            Point2d position=origin,first=default,second=default;
            for(int i=0;i<count;i++)
            {
                var p=definition.Controls[i];double dx=p.X-origin.X,dy=p.Y-origin.Y;
                position=new(position.X+basis[i].Position*dx,position.Y+basis[i].Position*dy);
                first=new(first.X+basis[i].FirstDerivative*dx,first.Y+basis[i].FirstDerivative*dy);
                second=new(second.X+basis[i].SecondDerivative*dx,second.Y+basis[i].SecondDerivative*dy);
            }
            double range=interval.LastParameter-interval.FirstParameter;
            var native=edge.EvaluateEdgeDerivatives(interval.FirstParameter+t*range);double tolerance=Math.Max(1e-8,scale*1e-8);
            Near(position,new(native.Point.X,native.Point.Y),tolerance);
            Near(first,new(native.FirstDerivative.X*range,native.FirstDerivative.Y*range),tolerance);
            Near(second,new(native.SecondDerivative.X*range*range,native.SecondDerivative.Y*range*range),tolerance*5);
            Assert.InRange(Math.Abs(basis.Sum(b=>b.Position)-1),0,1e-12);
            Assert.InRange(Math.Abs(basis.Sum(b=>b.FirstDerivative)),0,1e-11);
            Assert.InRange(Math.Abs(basis.Sum(b=>b.SecondDerivative)),0,1e-10);
            // The control-point Jacobian is independently checked by a finite author perturbation.
            for(int i=0;i<count;i++)
            {
                const double shift=.002;var altered=definition with{Controls=definition.Controls.SetItem(i,new(definition.Controls[i].X+shift,definition.Controls[i].Y))};
                var a=altered.EvaluateDerivatives(t);var b=definition.EvaluateDerivatives(t);
                Assert.InRange(Math.Abs((a.Point.X-b.Point.X)/shift-basis[i].Position),0,2e-7);
                Assert.InRange(Math.Abs((a.Derivative.X-b.Derivative.X)/shift-basis[i].FirstDerivative),0,2e-5);
                Assert.InRange(Math.Abs((a.SecondDerivative.X-b.SecondDerivative.X)/shift-basis[i].SecondDerivative),0,2e-3);
            }
        }
    }
    [Fact]
    public void BasisRejectsParametersOutsideItsExactFiniteDomain()
    {
        var curve=CubicSplineDefinition.Uniform([new(0,0),new(1,2),new(2,1),new(3,0)]);
        foreach(double t in new[]{double.NaN,double.PositiveInfinity,-.01,1.01})
            Assert.Throws<CadValidationException>(()=>curve.BasisDerivatives(t));
    }
    private static void Near(Point2d actual,Point2d expected,double tolerance)
    {Assert.InRange(Math.Abs(actual.X-expected.X),0,tolerance);Assert.InRange(Math.Abs(actual.Y-expected.Y),0,tolerance);}
}
