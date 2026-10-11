using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchFreeformGeometryTests
{
    internal static SketchFreeformGeometry Geometry(int kind)=>kind switch
    {
        0=>new(SketchFreeformKind.Bezier,[new(8,0),new(12,6),new(18,0)],[1,.65,1.2],[],[],2,false,.2,.85),
        1=>new(SketchFreeformKind.Bezier,[new(8,0),new(10,4),new(12,7),new(14,6),new(16,3),new(18,0)],[1,.7,1.1,.8,1.2,1],[],[],5,false,0,1),
        2=>new(SketchFreeformKind.BSpline,[new(8,0),new(10,4),new(12,7),new(14,6),new(16,3),new(18,0)],
            [1,.7,1.1,.8,1.2,1],[0,.35,1],[5,1,5],4,false,.1,.9),
        3=>new(SketchFreeformKind.BSpline,[new(8,0),new(12,4),new(16,0),new(12,-4)],
            [1,.7,1.2,.8],[0,1,2,3,4],[1,1,1,1,1],3,true,0,4),
        4=>new(SketchFreeformKind.BSpline,[new(8,0),new(10,4),new(12,7),new(16,0),new(12,-4),new(10,-2)],
            [1,.7,1.1,.8,1.2,1],[0,1,2,3],[2,2,2,2],3,true,.2,2.8),
        _=>new(SketchFreeformKind.BSpline,[new(8,0),new(10,4),new(12,7),new(14,6),new(16,3),new(18,0)],
            [1,.7,1.1,.8,1.2,1],[0,1,2,3,4,5,6,7,8,9],[1,1,1,1,1,1,1,1,1,1],3,false,3.2,5.8)
    };
    [Theory]
    [InlineData(0,false)][InlineData(0,true)][InlineData(1,false)][InlineData(1,true)]
    [InlineData(2,false)][InlineData(2,true)][InlineData(3,false)][InlineData(3,true)]
    [InlineData(4,false)][InlineData(4,true)][InlineData(5,false)][InlineData(5,true)]
    public void ManagedRationalEvaluationAndDerivativesMatchNativeFiniteCurve(int kind,bool reverse)
    {
        var g=Geometry(kind) with{Reversed=reverse};g.Validate();
        var full=g.Kind==SketchFreeformKind.Bezier?SketchCurve2d.Bezier(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Weights):
            SketchCurve2d.BSpline(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,g.Degree,g.Periodic,g.Weights);
        double width=full.LastParameter-full.FirstParameter;
        var finite=full.Trim((g.FirstParameter-full.FirstParameter)/width,(g.LastParameter-full.FirstParameter)/width);
        if(reverse)finite=finite.Reverse();
        var exact=new ExactSketchCurve(g.At(0),g.At(1),Freeform:g);var kernel=new OcctGeometryKernel();
        foreach(double t in new[]{0d,.01,.13,.37,.61,.89,.99,1})
        {
            var native=finite.Evaluate(t);var managed=g.Evaluate(t);var actual=kernel.Evaluate(exact,t);
            Near(new(native.Point.X,native.Point.Y),managed.Point);
            Near(new(native.FirstDerivative.X*(g.LastParameter-g.FirstParameter),native.FirstDerivative.Y*(g.LastParameter-g.FirstParameter)),managed.Derivative);
            Near(managed.Point,actual.Point);Near(managed.Derivative,actual.NormalizedDerivative);
        }
    }
    [Theory][InlineData("weight")][InlineData("degree")][InlineData("interval")][InlineData("multiplicity")][InlineData("periodic")][InlineData("collapsed")]
    public void InvalidFreeformValuesAreRejected(string change)
    {
        var g=Geometry(2);g=change switch
        {
            "weight"=>g with{Weights=g.Weights.SetItem(1,0)},"degree"=>g with{Degree=3},
            "interval"=>g with{FirstParameter=-.1},"multiplicity"=>g with{Multiplicities=[5,4,5]},
            "periodic"=>g with{Periodic=true},_=>g with{Poles=g.Poles.Select(_=>new Point2d(1,1)).ToImmutableArray()}
        };
        Assert.Throws<CadValidationException>(g.Validate);
    }
    internal static void Near(Point2d expected,Point2d actual)=>Assert.InRange(double.Hypot(expected.X-actual.X,expected.Y-actual.Y),0,1e-7);
}
