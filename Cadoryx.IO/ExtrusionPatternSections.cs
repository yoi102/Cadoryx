using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static PackRecipe PackExtrusionPatternRecipe(ExtrusionPatternRecipe recipe)
    {
        var layout=R(recipe.Layout);var template=R(recipe.Template);
        return new("extrusion-pattern",[],layout.Placement,recipe.AssetInputs.Select(G).ToArray(),[],0,
            ExtrusionPattern:new(recipe.Seed.Value,layout.Repetition!,template with{Sources=[]}));
    }
    private static ExtrusionPatternRecipe ReadExtrusionPatternRecipe(PackRecipe recipe)
    {
        var p=recipe.ExtrusionPattern!;var template=p.Template;
        if(p.Layout is null||template is null||template.Extrusion is null||template.Sources is not {Length:0}||
            !template.Kind.StartsWith("extrude",StringComparison.Ordinal)||recipe.Placement is null||
            recipe.Sources.Length!=(template.Extrusion.Face is null?2:3)||recipe.Numbers is not {Length:0}||recipe.Profile is not {Length:0}||recipe.Operation!=0||
            recipe.Repetition is not null||recipe.Hole is not null||recipe.HolePattern is not null||recipe.Extrusion is not null||recipe.Guided is not null||
            recipe.FaceFeature is not null||recipe.RevolveAxis is not null||recipe.Holes is not null||recipe.PolygonHoles is not null||
            recipe.MixedCurves is not null||recipe.MixedHoles is not null||recipe.Islands is not null||recipe.SplineControls is not null||recipe.SplineHoles is not null||
            template.ExtrusionPattern is not null||template.HolePattern is not null||template.Repetition is not null||template.Hole is not null||
            template.Guided is not null||template.FaceFeature is not null||template.RevolveAxis is not null)
            throw new InvalidDataException("Malformed extrusion replay or hidden recipe companion.");
        var layout=ReadRepetition(new("repetition",[],recipe.Placement,[recipe.Sources[0]],[],0,Repetition:p.Layout));
        var extrusion=(ExtrudeRecipe)R(template with{Sources=recipe.Sources.Skip(1).ToArray()});
        var result=new ExtrusionPatternRecipe(new(p.Seed),extrusion,layout);result.Validate();return result;
    }
    internal static ReadOnlyMemory<byte> UpgradeExtrusionPatterns(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.ExtrusionPattern is not null||f.Recipe.Kind=="extrusion-pattern"))
            throw new InvalidDataException("Extrusion replay payload cannot occur in features v29 or earlier.");
        return bytes;
    }
}
