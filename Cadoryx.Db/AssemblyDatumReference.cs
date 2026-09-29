namespace Cadoryx.Db;

public enum AssemblyDatumGeometry { PlaneFace, CylinderFace, CircleEdge }

/// <summary>One analytic datum on an exact, immutable body BRep. The full-map index is meaningful
/// only together with the asset, revision, fingerprint and owning occurrence path.</summary>
public sealed record AssemblyDatumReference(OccurrencePath Path,DefinitionId DefinitionId,BodyId BodyId,
    FeatureId? FeatureId,GeometryRevisionId Revision,AssetId Asset,string Fingerprint,
    int FullTopologyIndex,AssemblyDatumGeometry Geometry,Vector3d LocalPoint,
    Vector3d LocalAxis,double RadiusMm,int SchemaVersion=1)
{
    public void Validate(DocumentId document)
    {
        if(Path is null||Path.DocumentId!=document||Path.Slots.IsEmpty||Path.Slots.Length>128||
           SchemaVersion!=1||!Enum.IsDefined(Geometry)||FullTopologyIndex is <0 or >100000||
           Fingerprint is not {Length:64}||!Fingerprint.All(Uri.IsHexDigit)||
           !double.IsFinite(RadiusMm)||RadiusMm<0||
           Geometry!=AssemblyDatumGeometry.PlaneFace&&RadiusMm<=0)
            throw new CadValidationException("Invalid assembly datum evidence.");
        CadGuard.Id(DefinitionId);CadGuard.Id(BodyId);CadGuard.Id(Revision);Asset.Validate();
        if(FeatureId is {} feature)CadGuard.Id(feature);
        LocalPoint.Validate();LocalAxis.Validate();
        if(Math.Abs(LocalAxis.Length-1)>1e-8)
            throw new CadValidationException("Assembly datum direction must be unit length.");
    }
    public bool IsCurrent(DocumentSnapshot document,OccurrencePath path,DefinitionId definition)
    {
        if(!Path.Equals(path)||DefinitionId!=definition||
           document.Definitions.GetValueOrDefault(definition) is not PartDefinition part||
           !part.Bodies.Contains(BodyId)||!document.Bodies.TryGetValue(BodyId,out var body)||
           body.Geometry.Revision!=Revision||body.Geometry.AssetId!=Asset)return false;
        return FeatureId is not {} id||document.Features.TryGetValue(id,out var feature)&&
            feature.PartId==definition&&feature.OutputBodyId==BodyId&&!feature.IsStale&&
            feature.Result.Revision==Revision&&feature.Result.AssetId==Asset;
    }
}
