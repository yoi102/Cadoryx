using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionIntersectionSensitivityTests
{
    [Theory][InlineData(2,3)][InlineData(-2,3)][InlineData(2,-3)][InlineData(-2,-3)]
    public void MovingTwoLineAuthorsHasTheIndependentIntersectionParameterDerivatives(double slope,double abscissa)
    {
        // A(u)=(u,slope*u), B(v)=(abscissa,v): u=abscissa,v=slope*abscissa.
        var point=new Point2d(abscissa,slope*abscissa);
        var a=new SketchRegionCurveSensitivity(point,new(1,slope),[new(0,abscissa),new(0,0)]);
        var b=new SketchRegionCurveSensitivity(point,new(0,1),[new(0,0),new(1,0)]);
        var derivative=SketchRegionIntersectionSensitivity.Differentiate(a,b);
        Assert.InRange(Math.Abs(derivative.First[0]),0,1e-12);Assert.InRange(Math.Abs(derivative.First[1]-1),0,1e-12);
        Assert.InRange(Math.Abs(derivative.Second[0]-abscissa),0,1e-12);Assert.InRange(Math.Abs(derivative.Second[1]-slope),0,1e-12);
    }
    [Theory][InlineData(2,1,false)][InlineData(2,1,true)][InlineData(5,-2,false)][InlineData(5,-2,true)]
    public void CircleRadiusAndLinePositionUseTheIndependentAngularAndHeightDerivatives(double radius,double x,bool lower)
    {
        // A(u)=(r*cos(u),r*sin(u)), B(v)=(x,v), with an explicitly selected branch.
        double y=(lower?-1:1)*Math.Sqrt(radius*radius-x*x),u=Math.Atan2(y,x),cos=Math.Cos(u),sin=Math.Sin(u);
        var a=new SketchRegionCurveSensitivity(new(x,y),new(-radius*sin,radius*cos),[new(cos,sin),new(0,0)]);
        var b=new SketchRegionCurveSensitivity(new(x,y),new(0,1),[new(0,0),new(1,0)]);
        var actual=SketchRegionIntersectionSensitivity.Differentiate(a,b);
        Assert.InRange(Math.Abs(actual.First[0]-x/(radius*y)),0,1e-12);Assert.InRange(Math.Abs(actual.First[1]+1/y),0,1e-12);
        Assert.InRange(Math.Abs(actual.Second[0]-radius/y),0,1e-12);Assert.InRange(Math.Abs(actual.Second[1]+x/y),0,1e-12);
        for(int i=0;i<2;i++)
        {
            var lhs=new Point2d(a.PositionGradient[i].X+a.NativeDerivative.X*actual.First[i],a.PositionGradient[i].Y+a.NativeDerivative.Y*actual.First[i]);
            var rhs=new Point2d(b.PositionGradient[i].X+b.NativeDerivative.X*actual.Second[i],b.PositionGradient[i].Y+b.NativeDerivative.Y*actual.Second[i]);
            Assert.InRange(double.Hypot(lhs.X-rhs.X,lhs.Y-rhs.Y),0,1e-12);
        }
    }
    [Theory][InlineData("tangent")][InlineData("near-tangent")][InlineData("singular")][InlineData("different-point")][InlineData("different-count")][InlineData("budget")][InlineData("gradient-nan")]
    public void UnprovenIntersectionSensitivitiesFailWithoutInventingAParameterBranch(string fault)
    {
        var a=new SketchRegionCurveSensitivity(new(0,0),new(1,0),[new(1,0)]);var b=new SketchRegionCurveSensitivity(new(0,0),new(0,1),[new(0,1)]);
        if(fault=="tangent")b=b with{NativeDerivative=new(-1,0)};
        if(fault=="near-tangent")b=b with{NativeDerivative=new(1,1e-10)};
        if(fault=="singular")b=b with{NativeDerivative=new(0,0)};
        if(fault=="different-point")b=b with{Position=new(0,.01)};
        if(fault=="different-count")b=b with{PositionGradient=[]};
        if(fault=="budget"){a=a with{PositionGradient=Enumerable.Repeat(new Point2d(1,0),257).ToImmutableArray()};b=b with{PositionGradient=a.PositionGradient};}
        if(fault=="gradient-nan")b=b with{PositionGradient=[new(double.NaN,0)]};
        Assert.Throws<CadValidationException>(()=>SketchRegionIntersectionSensitivity.Differentiate(a,b));
    }
}
