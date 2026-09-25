namespace Cadoryx.Db;

/// <summary>A user's confirmed edge selection for one exact upstream output. The full-map index is
/// only usable with the recorded revision/asset and a fresh verified history trace.</summary>
public sealed record ExactTopologySelection(DocumentId DocumentId,FeatureId FeatureId,GeometryRevisionId Revision,
    AssetId Asset,string Fingerprint,int FullTopologyIndex,HistoryShapeKind Kind,string AdapterVersion,int SchemaVersion=1)
{
    public void Validate()
    {
        CadGuard.Id(DocumentId);CadGuard.Id(FeatureId);CadGuard.Id(Revision);Asset.Validate();CadGuard.Name(AdapterVersion);
        if(SchemaVersion!=1||Fingerprint is not {Length:64}||!Fingerprint.All(Uri.IsHexDigit)||
            FullTopologyIndex is <0 or >100000||Kind is not (HistoryShapeKind.Edge or HistoryShapeKind.Face))
            throw new CadValidationException("Invalid exact topology selection.");
    }
}

public sealed record FeatureTopologyBinding(TopologyReference? Origin,FeatureId TargetFeatureId,
    GeometryRevisionId TargetRevision,AssetId TargetAsset,int FullTopologyIndex,string AdapterVersion,int SchemaVersion=1)
{
    public ExactTopologySelection? ExactEdge {get;init;}
    public ExactTopologySelection? SupportFace {get;init;}
    public void Validate(DocumentId document)
    {
        CadGuard.Id(TargetFeatureId);CadGuard.Id(TargetRevision);TargetAsset.Validate();CadGuard.Name(AdapterVersion);
        if(SchemaVersion is not (1 or 2)||FullTopologyIndex is <0 or >100000)
            throw new CadValidationException("Invalid feature topology binding.");
        if(Origin is {} origin)
        {
            origin.Validate();
            if(origin.DocumentId!=document||origin.Policy!=TopologyRebindPolicy.Semantic||origin.Kind!=TopologyKind.Edge||
                origin.FeatureId==TargetFeatureId||ExactEdge is not null||(SchemaVersion==1&&SupportFace is not null))
                throw new CadValidationException("Invalid semantic feature binding.");
        }
        else if(ExactEdge is {} edge)
        {
            edge.Validate();
            if(SchemaVersion!=2||edge.DocumentId!=document||edge.FeatureId!=TargetFeatureId||
                edge.Revision!=TargetRevision||edge.Asset!=TargetAsset||edge.FullTopologyIndex!=FullTopologyIndex||
                edge.AdapterVersion!=AdapterVersion||edge.Kind!=HistoryShapeKind.Edge)
                throw new CadValidationException("Invalid exact feature binding.");
        }
        else throw new CadValidationException("Feature binding has no selected edge.");
        if(SupportFace is {} face)
        {
            face.Validate();
            if(face.DocumentId!=document||face.FeatureId!=TargetFeatureId||face.Revision!=TargetRevision||
                face.Asset!=TargetAsset||face.Kind!=HistoryShapeKind.Face||face.AdapterVersion!=AdapterVersion||
                Origin is null&&face.Fingerprint!=ExactEdge!.Fingerprint)
                throw new CadValidationException("Invalid chamfer support face.");
        }
    }
}

/// <summary>One fillet on a verified edge of an exact upstream BRep. The index never rebinds by itself.</summary>
public sealed record HistoryFilletRecipe(GeometryAssetRef Source,int FullTopologyIndex,double Radius,double? EndRadius=null) : GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();CadGuard.Positive(Radius);
        if(EndRadius is {} end)CadGuard.Positive(end);
        if(FullTopologyIndex is <0 or >100000)throw new CadValidationException("Invalid bound edge index.");
    }
}

/// <summary>One edge and one user-confirmed adjacent support face in an exact upstream BRep.</summary>
public sealed record HistoryChamferRecipe(GeometryAssetRef Source,int FullTopologyIndex,int SupportFaceIndex,
    double Distance,double? SecondDistance=null) : GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();CadGuard.Positive(Distance);
        if(SecondDistance is {} second)CadGuard.Positive(second);
        if(FullTopologyIndex is <0 or >100000||SupportFaceIndex is <0 or >100000)
            throw new CadValidationException("Invalid bound chamfer selection.");
    }
}
