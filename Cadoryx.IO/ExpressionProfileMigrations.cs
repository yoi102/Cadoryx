using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeExpressionProfileFeatures(ReadOnlyMemory<byte> bytes)
    {
        void Curves(PackMixedCurve[]? curves)
        {if((curves??[]).Any(c=>c.Expression is not null))throw new InvalidDataException("Expression profiles cannot appear in a legacy feature schema.");}
        void Island(PackIsland island)
        {
            Curves(island.MixedCurves);foreach(var hole in island.MixedHoles??[])Curves(hole);
            foreach(var child in island.Islands??[])Island(child);
        }
        void Recipe(PackRecipe recipe)
        {
            Curves(recipe.MixedCurves);foreach(var hole in recipe.MixedHoles??[])Curves(hole);
            foreach(var island in recipe.Islands??[])Island(island);
            if(recipe.ExtrusionPattern is {} pattern)Recipe(pattern.Template);
            if(recipe.Guided?.Sweep is {} sweep){Recipe(sweep.Section);Curves(sweep.Path);}
            if(recipe.Guided?.Loft is {} loft)foreach(var channel in loft.Channels)foreach(var profile in channel.Profiles)Recipe(profile);
        }
        foreach(var feature in Read<PackFeaturesV3>(bytes).Features)
        {
            var r=feature.Recipe;Recipe(new(r.Kind,r.Numbers,r.Placement,[],r.Profile,r.Operation,r.Holes,r.PolygonHoles,r.MixedCurves,r.MixedHoles,r.Islands,
                r.SplineControls,r.SplineHoles,r.Repetition,r.Hole,r.Extrusion,r.Guided,r.FaceFeature,r.RevolveAxis,r.HolePattern,r.ExtrusionPattern,
                r.SplineKnots,r.SplineHoleKnots));
        }
        return bytes;
    }
}
