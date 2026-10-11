using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class BoundedContactCoordinateTests
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static readonly Type CoordinateType=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.BoundedContactCoordinate")!;
    private static object Bounds(double lower,double upper)=>Activator.CreateInstance(CoordinateType,Flags,null,[lower,upper],null)!;
    private static double Coordinate(object bounds,double seed)=>(double)CoordinateType.GetMethod("Coordinate",Flags)!.Invoke(bounds,[seed])!;
    private static (bool Valid,double Value,double[] Gradient) Evaluate(object bounds,double coordinate)
    {
        object?[] args=[coordinate,1,3,null];
        bool valid=(bool)CoordinateType.GetMethod("TryEvaluate",Flags)!.Invoke(bounds,args)!;
        if(!valid)return(false,default,[]);
        var scalar=args[3]!;return(true,(double)scalar.GetType().GetProperty("Value")!.GetValue(scalar)!,
            (double[])scalar.GetType().GetProperty("Gradient")!.GetValue(scalar)!);
    }
    [Theory]
    [InlineData(0,1,.07)][InlineData(0,1,.91)][InlineData(.1,.9,.37)][InlineData(.3,.4,.31)]
    [InlineData(0,.5,.25)][InlineData(.5,1,.75)][InlineData(.499999999999,.500000000001,.5)]
    [InlineData(0,1,1e-100)]
    public void InteriorSeedRoundTripsAndAllGradientCoordinatesMatchIndependentDifferences(double lower,double upper,double seed)
    {
        var bounds=Bounds(lower,upper);double q=Coordinate(bounds,seed);var value=Evaluate(bounds,q);
        Assert.True(value.Valid);Assert.True(value.Value>lower&&value.Value<upper&&value.Value>0&&value.Value<1);
        // The inverse logarithm's relative conditioning grows with |q| near an endpoint.
        Assert.InRange(Math.Abs(value.Value-seed),0,4*2.220446049250313e-16*Math.Max(1,Math.Abs(q))*seed);
        Assert.Equal(0,value.Gradient[0]);Assert.Equal(0,value.Gradient[2]);Assert.True(value.Gradient[1]>0);
        // A central difference independently checks d(parameter)/d(coordinate).
        // Avoid differencing a forward value when the narrow interval has only a few ULPs.
        if(upper-lower>1e-9&&seed>1e-50)
        {
            const double h=1e-4;double observed=(Evaluate(bounds,q+h).Value-Evaluate(bounds,q-h).Value)/(2*h);
            Assert.InRange(Math.Abs(observed-value.Gradient[1]),0,1e-8*Math.Max(1e-8,value.Gradient[1]));
        }
    }
    [Theory]
    [InlineData(-.1,1)][InlineData(0,1.1)][InlineData(.5,.5)][InlineData(.6,.4)]
    [InlineData(double.NaN,1)][InlineData(0,double.PositiveInfinity)]
    public void InvalidAllowedIntervalIsRejected(double lower,double upper)=>
        Assert.IsType<CadValidationException>(Assert.Throws<TargetInvocationException>(()=>Bounds(lower,upper)).InnerException);
    [Theory][InlineData(.1)][InlineData(.9)][InlineData(0d)][InlineData(1d)][InlineData(double.NaN)]
    public void SeedOutsideStrictAllowedIntervalIsRejected(double seed)=>
        Assert.IsType<CadValidationException>(Assert.Throws<TargetInvocationException>(()=>Coordinate(Bounds(.1,.9),seed)).InnerException);
    [Theory][InlineData(1000d)][InlineData(-1000d)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void OverflowAndEndpointRoundingFailWithoutClamping(double coordinate)
    {Assert.False(Evaluate(Bounds(.1,.9),coordinate).Valid);Assert.False(Evaluate(Bounds(0,1),coordinate).Valid);}
}
