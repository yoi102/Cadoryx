using Xunit;

namespace Cadoryx.AdapterProbes;

// Adapter identity probe only: no STEP, geometry or product test implementation.
public sealed class FilterAdapterProbeTests
{
    public enum Join { Bevel, Miter, Round }

    [Theory]
    [InlineData("spline-g1", false, -.1, Join.Bevel)]
    [InlineData("spline-g1", true, .1, Join.Miter)]
    [InlineData("spline-g2", false, -.1, Join.Round)]
    [InlineData("line", true, .1, Join.Bevel)]
    public void ExactDisplayNameHandlesStringBooleanDoubleAndEnumSiblings(string kind, bool mirror, double distance, Join join)
    {
        Assert.NotEmpty(kind); Assert.Equal(.1, Math.Abs(distance)); Assert.True(Enum.IsDefined(join));
        Assert.Contains(mirror, new[] { false, true });
    }
}
