using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static PackRecipe PackHolePatternRecipe(HolePatternRecipe recipe)
    {
        var layout=R(recipe.Layout);var hole=R(recipe.Template);
        return new("hole-pattern",[],layout.Placement,[G(recipe.Source),G(recipe.Template.Source)],[],0,
            HolePattern:new(recipe.Seed.Value,layout.Repetition!,hole.Hole!,hole.Placement!));
    }
    private static HolePatternRecipe ReadHolePatternRecipe(PackRecipe recipe)
    {
        var p=recipe.HolePattern!;
        if(p.Layout is null||p.Hole is null||p.HoleFrame is null||recipe.Placement is null||recipe.Sources is not {Length:2}||
            recipe.Numbers is not {Length:0}||recipe.Profile is not {Length:0}||recipe.Operation!=0||
            recipe.Repetition is not null||recipe.Hole is not null||recipe.Extrusion is not null||recipe.Guided is not null||
            recipe.FaceFeature is not null||recipe.RevolveAxis is not null||recipe.Holes is not null||recipe.PolygonHoles is not null||
            recipe.MixedCurves is not null||recipe.MixedHoles is not null||recipe.Islands is not null||recipe.SplineControls is not null||recipe.SplineHoles is not null)
            throw new InvalidDataException("Malformed hole replay or hidden recipe companion.");
        var layout=ReadRepetition(new("repetition",[],recipe.Placement,[recipe.Sources[0]],[],0,Repetition:p.Layout));
        var hole=ReadHole(new("hole",[],p.HoleFrame,[recipe.Sources[1]],[],0,Hole:p.Hole));
        var result=new HolePatternRecipe(new(p.Seed),hole,layout);result.Validate();return result;
    }
    internal static ReadOnlyMemory<byte> UpgradeHolePatterns(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.HolePattern is not null||f.Recipe.Kind=="hole-pattern"))
            throw new InvalidDataException("Hole replay payload cannot occur in features v28 or earlier.");
        return bytes;
    }
}
