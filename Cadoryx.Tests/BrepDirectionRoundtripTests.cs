using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class BrepDirectionRoundtripTests
{
    private const string Header="DBRep_DrawableShape\n\nCASCADE Topology V3, (c) Open Cascade\nCurves 1\n";
    private const string Circle="2 10 20 30 0 0 1 1 0 0 0 1 0 2\nTShapes 0\n";
    [Theory][InlineData("2.7755575615628907e-17",true)][InlineData("1e-12",false)]
    public void OnlyBoundedAxisRoundoffIsAccepted(string delta,bool expected)
    {
        Assert.Equal(expected,BrepDirectionRoundtrip.Matches(Header+Circle,Header+Circle.Replace("1 0 0 0 1 0","1 "+delta+" 0 0 1 0")));
    }
    [Theory][InlineData("origin")][InlineData("radius")][InlineData("connectivity")][InlineData("version")][InlineData("nan")][InlineData("non-unit")][InlineData("unknown")]
    public void GeometryAndTopologyChangesCannotHideAsRoundoff(string mutation)
    {
        string before=Header+Circle;string after=mutation switch
        {
            "origin"=>before.Replace("10 20","10.000000000000002 20"),
            "radius"=>before.Replace("0 2\n","0 2.0000000000000004\n"),
            "connectivity"=>before.Replace("TShapes 0","TShapes 1"),
            "version"=>before.Replace("Topology V3","Topology V4"),
            "nan"=>before.Replace("1 0 0 0 1 0","1 NaN 0 0 1 0"),
            "non-unit"=>before.Replace("1 0 0 0 1 0","0.9 0 0 0 1 0"),
            _=>before.Replace("Curves 1","Unknown 1").Replace("1 0 0 0 1 0","1 1e-17 0 0 1 0")
        };
        Assert.False(BrepDirectionRoundtrip.Matches(before,after));
    }
    [Fact] public void MultilineSplinePayloadCannotBeReinterpretedAsCircle()
    {
        string before=Header+"7 0 0 3 6\n"+Circle;
        Assert.False(BrepDirectionRoundtrip.Matches(before,before.Replace("1 0 0 0 1 0","1 1e-17 0 0 1 0")));
    }
}
