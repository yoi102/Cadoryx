namespace Cadoryx.Db;

/// <summary>Signed metric skin/join offset of one whole source solid; no face selection or guessed history.</summary>
public sealed record SolidOffsetRecipe(GeometryAssetRef Source,double Distance):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();CadGuard.Finite(Distance);
        if(Source.Kind!=BodyKind.Solid||Math.Abs(Distance)<=1e-6||Math.Abs(Distance)>100000)
            throw new CadValidationException("Solid offset requires one solid and a signed distance between 0.000001 and 100000 mm in magnitude.");
    }
    public void ValidateApplied(DocumentSnapshot document,FeatureDefinition feature)
    {
        if(feature.Inputs.Length!=1||feature.SketchSource is not null||!document.Features.TryGetValue(feature.Inputs[0],out var source)||source.PartId!=feature.PartId||
            !feature.IsStale&&!feature.IsSuppressed&&(source.IsStale||source.IsSuppressed||source.Result!=Source))
            throw new CadValidationException("Solid offset source is foreign or stale.");
    }
}
