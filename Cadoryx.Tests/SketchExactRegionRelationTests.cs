using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExactRegionRelationTests
{
    private static ImmutableArray<SketchBoundaryCurve> Ellipse(double center,double radius)
    {
        var e=new SketchEllipseGeometry(new(center,0),new(radius,0),new(0,radius/2),.7,Math.Tau);
        return [new(e.At(0),e.At(1)){Ellipse=e}];
    }
    [Theory] [InlineData(0,2,ExactRegionRelation.Contains)] [InlineData(0,12,ExactRegionRelation.Contained)]
    [InlineData(20,2,ExactRegionRelation.Disjoint)] [InlineData(8,2,ExactRegionRelation.ContactOrOverlap)]
    [InlineData(9,4,ExactRegionRelation.ContactOrOverlap)] [InlineData(0,10,ExactRegionRelation.ContactOrOverlap)]
    public void ConicRegionsUseExactBrepContainmentAndContact(double center,double radius,ExactRegionRelation expected)
    {
        var a=Ellipse(0,10);var b=Ellipse(center,radius);var kernel=new OcctGeometryKernel();Assert.Equal(expected,kernel.CompareRegions(a,b));
        var reverse=b.Reverse().Select(c=>c.Reversed()).ToImmutableArray();Assert.Equal(expected,kernel.CompareRegions(a,reverse));
        Assert.Throws<CadValidationException>(()=>SketchMixedProfile.StrictlyContains(a,b));
    }
    [Fact] public void PolygonAndFiniteEllipseSegmentClassifyWithoutDisplaySampling()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Finite",RigidTransform3d.Identity));d.AddEllipse(new(new(0,0),new(5,0),new(0,2),.7,2.2));
        var curves=SketchProfileBuilder.Mixed(d.Value,[d.Value.Ellipses[0].Id]).BoundaryCurves;
        var box=SketchMixedProfile.Polygon([new(-10,-10),new(10,-10),new(10,10),new(-10,10)]);
        Assert.Equal(ExactRegionRelation.Contains,new OcctGeometryKernel().CompareRegions(box,curves));
        var corrupt=curves.SetItem(0,curves[0] with{Start=new(100,100)});Assert.Throws<CadValidationException>(()=>new OcctGeometryKernel().CompareRegions(box,corrupt));
    }
}
