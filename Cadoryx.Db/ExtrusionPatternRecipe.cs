using System.Text.Json.Serialization;

namespace Cadoryx.Db;

/// <summary>Replays an authored add/remove-material extrusion in each explicit layout position on one solid.</summary>
public sealed record ExtrusionPatternRecipe(FeatureId Seed,ExtrudeRecipe Template,RepetitionRecipe Layout):GeometryRecipe
{
    [JsonIgnore] public GeometryAssetRef Source=>Layout.Source;
    public override IEnumerable<GeometryAssetRef> AssetInputs=>new[]{Source}.Concat(Template.AssetInputs);
    public override void Validate()
    {
        CadGuard.Id(Seed);Template.Validate();Layout.Validate();
        if(Template.Options is not {Operation:ExtrusionOperation.Fuse or ExtrusionOperation.Cut,Source:{Kind:BodyKind.Solid}}||
            Template.Options.Termination==ExtrusionTermination.FiniteSurface||
            Layout.StylePolicy!=RepetitionStylePolicy.VerifiedInheritance||Layout.Output!=RepetitionOutput.Fused||Source.Kind!=BodyKind.Solid)
            throw new CadValidationException("Extrusion replay requires an authored add/remove-material operation on one solid.");
    }
    public bool SameTemplate(ExtrudeRecipe other)=>Template.Distance==other.Distance&&Template.Placement==other.Placement&&
        Template.Options==other.Options&&SketchProfileReference.SameProfile(Template.Profile,other.Profile);
    public ExtrusionPatternRecipe Refresh(DocumentSnapshot document,DefinitionId part)
    {
        if(!document.Features.TryGetValue(Seed,out var seed)||seed.PartId!=part||seed.Recipe is not ExtrudeRecipe {Options:{Operation:ExtrusionOperation.Fuse or ExtrusionOperation.Cut}} extrusion)
            throw new CadValidationException("Extrusion replay requires its exact authored extrusion seed in the same part.");
        if(seed.IsStale||seed.IsSuppressed)throw new DatumSourceUnavailableException();
        return this with{Template=extrusion,Layout=(Layout with{Source=seed.Result}).Refresh(document,part)};
    }
    public void ValidateApplied(DocumentSnapshot document,FeatureDefinition feature)
    {
        Validate();
        if(feature.Inputs.Length!=1||feature.Inputs[0]!=Seed||feature.SketchSource is not null||
            !document.Features.TryGetValue(Seed,out var seed)||seed.PartId!=feature.PartId||seed.Recipe is not ExtrudeRecipe extrusion)
            throw new CadValidationException("Extrusion replay seed identity or ownership differs from its dependency.");
        Layout.ValidateApplied(document,feature);
        if(!feature.IsStale&&!SameTemplate(extrusion))throw new CadValidationException("Extrusion replay template differs from its exact current seed recipe.");
    }
}
