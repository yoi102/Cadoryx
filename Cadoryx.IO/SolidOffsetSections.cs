using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static SolidOffsetRecipe ReadSolidOffsetRecipe(PackRecipe value)
    {
        if(value.Numbers is not {Length:1}||value.Placement is not null||value.Sources is not {Length:1}||value.Profile is not {Length:0}||value.Operation!=0||
            value.Holes is not null||value.PolygonHoles is not null||value.MixedCurves is not null||value.MixedHoles is not null||value.Islands is not null||
            value.SplineControls is not null||value.SplineHoles is not null||value.SplineKnots is not null||value.SplineHoleKnots is not null||
            value.Repetition is not null||value.Hole is not null||value.Extrusion is not null||value.Guided is not null||value.FaceFeature is not null||
            value.RevolveAxis is not null||value.HolePattern is not null||value.ExtrusionPattern is not null)
            throw new InvalidDataException("Unexpected solid offset payload or companion.");
        var recipe=new SolidOffsetRecipe(G(value.Sources[0]),value.Numbers[0]);recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeSolidOffsetFeatures(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.Kind=="solid-offset"))
            throw new InvalidDataException("Solid offset in legacy feature schema.");
        return bytes;
    }
}
