namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeCurvedRevolves(ReadOnlyMemory<byte> bytes)
    {
        foreach(var feature in Read<PackFeaturesV3>(bytes).Features)
        {
            var r=feature.Recipe;
            if(r.Kind!="revolve"&&!r.Kind.StartsWith("revolve-",StringComparison.Ordinal))continue;
            if(r.Kind is not ("revolve" or "revolve-circle" or "revolve-arc" or "revolve-mixed")||
                r.Holes is {Length:>0}||r.PolygonHoles is {Length:>0}||r.MixedHoles is {Length:>0}||
                r.Islands is {Length:>0}||r.SplineHoles is {Length:>0}||r.SplineControls is not null||
                r.MixedCurves?.Any(c=>c.BezierControl is not null||c.SplineControls is not null)==true)
                throw new InvalidDataException("Curve or holed revolution requires features v31 or later.");
        }
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeRevolveAxes(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.RevolveAxis is not null))
            throw new InvalidDataException("A revolution axis payload cannot occur in features v27 or earlier.");
        return bytes;
    }
}
