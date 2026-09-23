namespace Cadoryx.Db;

/// <summary>A user's confirmed edge selection for one exact upstream output. The full-map index is
/// only usable with the recorded revision/asset and a fresh verified history trace.</summary>
public sealed record FeatureTopologyBinding(TopologyReference Origin,FeatureId TargetFeatureId,
    GeometryRevisionId TargetRevision,AssetId TargetAsset,int FullTopologyIndex,string AdapterVersion,int SchemaVersion=1)
{
    public void Validate(DocumentId document)
    {
        Origin.Validate();CadGuard.Id(TargetFeatureId);CadGuard.Id(TargetRevision);TargetAsset.Validate();CadGuard.Name(AdapterVersion);
        if(SchemaVersion!=1||Origin.DocumentId!=document||Origin.Policy!=TopologyRebindPolicy.Semantic||
            Origin.Kind!=TopologyKind.Edge||Origin.FeatureId==TargetFeatureId||FullTopologyIndex is <0 or >100000)
            throw new CadValidationException("Invalid feature topology binding.");
    }
}

/// <summary>One fillet on a verified edge of an exact upstream BRep. The index never rebinds by itself.</summary>
public sealed record HistoryFilletRecipe(GeometryAssetRef Source,int FullTopologyIndex,double Radius) : GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();CadGuard.Positive(Radius);
        if(FullTopologyIndex is <0 or >100000)throw new CadValidationException("Invalid bound edge index.");
    }
}
