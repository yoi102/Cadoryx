using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExactSketchGeometryServiceTests
{
    private static IExactSketchGeometry Kernel()=>new OcctGeometryKernel();
    private static CubicSplineDefinition Cubic()=>new([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4]);
    private static void Near(double expected,double actual,double epsilon=1e-7)=>Assert.InRange(actual,expected-epsilon,expected+epsilon);
    [Fact] public void ExactNonuniformIntersectionAndProjectionUseTheAuthoredFiniteCurve()
    {
        var kernel=Kernel();var geometry=Cubic().Trim(.1,.9);var curve=ExactSketchCurve.Cubic(geometry);
        var hits=kernel.Intersect(curve,new(new(0,4),new(30,4)));var hit=Assert.Single(hits);Assert.False(hit.IsTangent);
        Near(4,hit.Point.Y);var value=kernel.Evaluate(curve,hit.FirstParameter);Near(hit.Point.X,value.Point.X);
        var candidates=kernel.Project(curve,hit.Point);double minimum=candidates.Min(p=>p.Distance);
        var projection=Assert.Single(candidates.Where(p=>p.Distance<=minimum+1e-7));Near(0,projection.Distance);Near(hit.FirstParameter,projection.Parameter);
        var managed=geometry.Evaluate(.37);var evaluated=kernel.Evaluate(curve,.37);
        Near(managed.Point.X,evaluated.Point.X);Near(managed.Derivative.X,evaluated.NormalizedDerivative.X);
    }
    [Fact] public void MultipleCrossingsAndTangentContactsAreCopiedWithoutChoosingAnIdentity()
    {
        var kernel=Kernel();var bezier=new QuadraticBezierRegion(new(-2,0),new(0,4),new(2,0));
        var curve=new ExactSketchCurve(bezier.Start,bezier.End,Bezier:bezier);
        var hits=kernel.Intersect(curve,new(new(-3,1),new(3,1)));
        Assert.Equal(2,hits.Length);Assert.All(hits,h=>Assert.False(h.IsTangent));
        var tangent=Assert.Single(kernel.Intersect(curve,new(new(-3,2),new(3,2))));Assert.True(tangent.IsTangent);
        Near(.5,tangent.FirstParameter);
    }
    [Fact] public void CoincidentIntervalsRejectInsteadOfInventingOneCrossing()
    {
        var kernel=Kernel();var curve=ExactSketchCurve.Cubic(Cubic());
        Assert.Throws<CadValidationException>(()=>kernel.Intersect(curve,curve));
        Assert.Throws<CadValidationException>(()=>kernel.Intersect(new(new(0,0),new(10,0)),new(new(2,0),new(8,0))));
    }
    [Fact] public void OpenReorderedAndCrossingRegionsReject()
    {
        var kernel=Kernel();Assert.Throws<CadValidationException>(()=>kernel.InspectRegion([new(new(0,0),new(1,0))]));
        Assert.Throws<CadValidationException>(()=>kernel.InspectRegion([new(new(0,0),new(2,2)),new(new(2,2),new(0,2)),
            new(new(0,2),new(2,0)),new(new(2,0),new(0,0))]));
    }
    [Fact] public void ClosedCircularBoundaryUsesOneExactEdge()
    {
        var arc=new SketchArcGeometry(new(0,0),2,0,Math.Tau);var result=Kernel().InspectRegion([new(arc.At(0),arc.At(1),arc)]);
        Near(Math.PI*4,result.Area,1e-6);Assert.Equal(1,result.EdgeCount);
    }
    [Fact] public void NonunitNativeDomainsReturnNormalizedDerivatives()
    {
        var geometry=Cubic() with{Knots=[-3,-2,2]};var curve=ExactSketchCurve.Cubic(geometry);var kernel=Kernel();
        foreach(double t in new[]{0d,.2,.7,1}){var expected=geometry.Evaluate(t);var actual=kernel.Evaluate(curve,t);
            Near(expected.Point.X,actual.Point.X);Near(expected.Derivative.X,actual.NormalizedDerivative.X);}
        var line=new ExactSketchCurve(new(0,0),new(12,16));var at=kernel.Evaluate(line,.4);
        Near(12,at.NormalizedDerivative.X);Near(16,at.NormalizedDerivative.Y);
    }
    [Fact] public void FiniteProjectionIncludesExactEndpointsAndPreservesEqualNearestAmbiguity()
    {
        var kernel=Kernel();var line=new ExactSketchCurve(new(0,0),new(5,0));
        var endpoint=kernel.Project(line,new(8,1)).MinBy(p=>p.Distance);Near(1,endpoint.Parameter);Near(Math.Sqrt(10),endpoint.Distance);
        var bezier=new QuadraticBezierRegion(new(-2,0),new(0,4),new(2,0));
        var curve=new ExactSketchCurve(bezier.Start,bezier.End,Bezier:bezier);
        var candidates=kernel.Project(curve,new(0,-10));double minimum=candidates.Min(p=>p.Distance);
        Assert.Equal(2,candidates.Count(p=>p.Distance<=minimum+1e-7));
    }
}
