using System.Collections.Immutable;
using Cadoryx.Db;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExactCubicSplineDefinitionTests
{
    private static CubicSplineDefinition Curve()=>new([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4]);
    private static SketchCurve2d Native(CubicSplineDefinition s)=>SketchCurve2d.BSpline(
        s.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),s.Knots,s.Multiplicities,3);
    private static void Near(double expected,double actual,double epsilon=1e-8)=>Assert.InRange(actual,expected-epsilon,expected+epsilon);
    [Fact] public void NonmonotoneControlPolygonNonuniformKnotsAndDerivativesAgreeWithNativeCurve()
    {
        var curve=Curve();curve.Validate();var native=Native(curve);
        foreach(double t in new[]{0d,.01,.199999,.2,.200001,.37,.9,1})
        {
            var a=curve.Evaluate(t);var b=native.Evaluate(t);
            Near(b.Point.X,a.Point.X);Near(b.Point.Y,a.Point.Y);
            Near(b.FirstDerivative.X,a.Derivative.X);Near(b.FirstDerivative.Y,a.Derivative.Y);
        }
    }
    [Theory] [InlineData(0,.2)] [InlineData(.2,1)] [InlineData(.13,.87)] [InlineData(.2,.8)] [InlineData(0,1)]
    public void TrimInsertsExactKnotsAndMatchesOriginalCurveAndNormalizedDerivatives(double first,double last)
    {
        var curve=Curve();var trim=curve.Trim(first,last);trim.Validate();var native=Native(curve).Trim(first,last);
        foreach(double t in new[]{0d,.17,.5,.81,1})
        {
            var original=curve.Evaluate(first+(last-first)*t);var actual=trim.Evaluate(t);var reference=native.Evaluate(t);
            Near(original.Point.X,actual.Point.X);Near(original.Point.Y,actual.Point.Y);
            Near(reference.Point.X,actual.Point.X);Near(reference.Point.Y,actual.Point.Y);
            Near(original.Derivative.X*(last-first),actual.Derivative.X);Near(original.Derivative.Y*(last-first),actual.Derivative.Y);
        }
    }
    [Fact] public void ReverseAndRepeatedTrimPreserveExactCurve()
    {
        var curve=Curve();var reverse=curve.Reverse();
        foreach(double t in new[]{0d,.2,.43,.7,1})
        {var a=curve.Evaluate(t);var b=reverse.Evaluate(1-t);Near(a.Point.X,b.Point.X);Near(a.Point.Y,b.Point.Y);Near(a.Derivative.X,-b.Derivative.X);}
        var once=curve.Trim(.1,.9).Trim(.25,.75);var direct=curve.Trim(.3,.7);
        foreach(double t in new[]{0d,.25,.5,1}){Near(direct.Evaluate(t).Point.X,once.Evaluate(t).Point.X);Near(direct.Evaluate(t).Point.Y,once.Evaluate(t).Point.Y);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EndPolynomialContinuationRetainsEveryOldCurvePointAndDerivative(bool start)
    {
        var curve=Curve();var extended=curve.ExtendEndpoint(start,.3);
        foreach(double t in new[]{0d,.05,.2,.7,.99,1})
        {
            var a=curve.Evaluate(t);var b=extended.Evaluate((t+(start?.3:0))/1.3);
            Near(a.Point.X,b.Point.X);Near(a.Point.Y,b.Point.Y);Near(a.Derivative.X*1.3,b.Derivative.X);Near(a.Derivative.Y*1.3,b.Derivative.Y);
        }
        var native=Native(extended);
        foreach(double t in new[]{0d,.1,.9,1}){var a=extended.Evaluate(t);var b=native.Evaluate(t);Near(b.Point.X,a.Point.X);Near(b.Point.Y,a.Point.Y);}
        var end=start?extended.Evaluate(0).Point:extended.Evaluate(1).Point;
        Assert.True(double.Hypot(end.X-curve.Controls[start?0:^1].X,end.Y-curve.Controls[start?0:^1].Y)>1e-3);
    }
    [Fact] public void CurveValidationAllowsCrossingAndCoincidentEndpointsForLaterExplicitRegionDiagnostics()
    {
        CubicSplineDefinition.Uniform([new(0,0),new(10,10),new(-10,10),new(0,0)]).Validate();
        Assert.Throws<CadValidationException>(()=>(Curve() with{Multiplicities=[4,2,4]}).Validate());
        Assert.Throws<CadValidationException>(()=>(Curve() with{Knots=[0,0,1]}).Validate());
        Assert.Throws<CadValidationException>(()=>(Curve() with{Controls=[new(0,0),new(0,0),new(0,0),new(0,0)]}).Validate());
    }
    [Fact] public void NonunitKnotDomainAndC0JointKeepNativeParameterization()
    {
        var curve=Curve() with{Knots=[-3,-2,2]};curve.Validate();var native=Native(curve);
        foreach(double t in new[]{0d,.2,.8,1}){var a=curve.Evaluate(t);var b=native.Evaluate(t);Near(b.Point.X,a.Point.X);Near(b.FirstDerivative.Y*(curve.Knots[^1]-curve.Knots[0]),a.Derivative.Y);}
        var trimmed=curve.Trim(.14,.9);var reversed=trimmed.Reverse();
        Near(trimmed.Evaluate(.2).Point.Y,reversed.Evaluate(.8).Point.Y);
    }
}
