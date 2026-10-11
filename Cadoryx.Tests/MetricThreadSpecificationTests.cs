using Cadoryx.Db;
using Xunit;

namespace Cadoryx.Tests;

public sealed class MetricThreadSpecificationTests
{
    [Theory][InlineData(3,.5,2.5)][InlineData(4,.7,3.3)][InlineData(5,.8,4.2)][InlineData(6,1,5)]
    [InlineData(8,1.25,6.8)][InlineData(10,1.5,8.5)][InlineData(12,1.75,10.2)][InlineData(16,2,14)]
    public void CatalogRetainsExplicitPitchAndPilotAsDifferentDimensions(int nominal,double pitch,double pilot)
    {
        var size=MetricThreadHoles.Size(nominal);Assert.Equal(new(nominal,pitch,pilot),size);
        var spec=MetricThreadHoles.Create(nominal);Assert.Equal(pitch,spec.PitchMm);Assert.True(spec.Through);Assert.Equal(0,spec.DepthMm);
        spec.ValidateForHole(pilot,true,0,HoleHead.Plain,null);
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(nominal,true,0,HoleHead.Plain,null));
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(pilot,true,0,HoleHead.Plain,new(3,ClearanceFit.Normal)));
        Assert.Throws<CadValidationException>(()=>(spec with{PitchMm=pitch+.01}).Validate());
        Assert.Throws<CadValidationException>(()=>(spec with{Hand=(ThreadHand)9}).Validate());
        Assert.Throws<CadValidationException>(()=>(spec with{Representation=(ThreadHoleRepresentation)1}).Validate());
    }
    [Fact] public void HandednessAndEffectiveBlindDepthDoNotSilentlyChangePilotGeometry()
    {
        var spec=MetricThreadHoles.Create(8,ThreadHand.Left,false,5);Assert.Equal("M8 x 1.25 LH",spec.Designation);
        spec.ValidateForHole(6.8,false,6,HoleHead.Plain,null);Assert.Equal(ThreadHoleRepresentation.Symbolic,spec.Representation);
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(6.8,false,4.9,HoleHead.Plain,null));
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(6.8,true,0,HoleHead.Plain,null));
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(6.8,false,6,HoleHead.Counterbore,null));
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(double.NaN,false,6,HoleHead.Plain,null));
        Assert.Throws<CadValidationException>(()=>spec.ValidateForHole(6.8,false,double.NaN,HoleHead.Plain,null));
        foreach(double depth in new[]{double.NaN,double.PositiveInfinity,-1,0})Assert.Throws<CadValidationException>(()=>(spec with{DepthMm=depth}).Validate());
        Assert.Throws<CadValidationException>(()=>MetricThreadHoles.Size(7));
    }
}
