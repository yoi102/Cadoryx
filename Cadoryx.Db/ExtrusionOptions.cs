namespace Cadoryx.Db;

public enum ExtrusionSide { Positive=0, Negative=1, Symmetric=2, TwoSides=3 }
public enum ExtrusionTermination { Distance=0, Through=1, Plane=2, PlanarFace=3, FiniteSurface=4 }
public enum ExtrusionOperation { NewBody=0, Fuse=1, Cut=2 }
public enum ExtrusionSurfaceBranch { Near=0, Far=1 }
/// <summary>Additional extrusion semantics. A symmetric Distance is total thickness; TwoSides uses Distance forward and Backward behind.</summary>
public sealed record ExtrusionOptions(ExtrusionSide Side=ExtrusionSide.Positive,double Backward=0,
    ExtrusionOperation Operation=ExtrusionOperation.NewBody,GeometryAssetRef? Source=null,
    ExtrusionTermination Termination=ExtrusionTermination.Distance,RigidTransform3d? LimitPlane=null,
    Guid? Datum=null,Guid? DatumRevision=null,ExactTopologySelection? LimitFace=null,GeometryAssetRef? LimitGeometry=null,
    ExactTopologySelection? SupportFace=null,ExtrusionSurfaceBranch? SurfaceBranch=null)
{
    public IEnumerable<GeometryAssetRef> AssetInputs
    {get{if(Source is {} source)yield return source;if(LimitGeometry is {} limit)yield return limit;}}
    public void Validate(double distance)
    {
        CadGuard.Finite(Backward,distance);Source?.Validate();LimitGeometry?.Validate();LimitFace?.Validate();LimitPlane?.Validate();SupportFace?.Validate();
        if(!Enum.IsDefined(Side)||!Enum.IsDefined(Operation)||!Enum.IsDefined(Termination)||
            (Operation==ExtrusionOperation.NewBody)!=(Source is null)||
            (Termination==ExtrusionTermination.Plane)!=(LimitPlane is not null)||
            (Termination is ExtrusionTermination.PlanarFace or ExtrusionTermination.FiniteSurface)!=(LimitFace is not null)||
            (LimitFace is null)!=(LimitGeometry is null)||LimitFace is {Kind:not HistoryShapeKind.Face}||
            (Termination==ExtrusionTermination.FiniteSurface)!=(SupportFace is not null)||
            (Termination==ExtrusionTermination.FiniteSurface)!=(SurfaceBranch is not null)||
            SupportFace is {Kind:not HistoryShapeKind.Face}||SurfaceBranch is {} branch&&!Enum.IsDefined(branch)||
            SupportFace is {} support&&(Source is null||support.Asset!=Source.AssetId||support.Revision!=Source.Revision)||
            Termination==ExtrusionTermination.FiniteSurface&&Operation==ExtrusionOperation.NewBody||
            (Datum is null)!=(DatumRevision is null)||Datum==Guid.Empty||DatumRevision==Guid.Empty||
            Datum is not null&&Termination!=ExtrusionTermination.Plane||
            Termination!=ExtrusionTermination.Distance&&distance!=0||
            Termination==ExtrusionTermination.Through&&Operation!=ExtrusionOperation.Cut||
            Side==ExtrusionSide.TwoSides&&(Termination!=ExtrusionTermination.Distance||Backward<=1e-6||Backward>1_000_000)||
            Side!=ExtrusionSide.TwoSides&&Backward!=0||
            Termination is ExtrusionTermination.Plane or ExtrusionTermination.PlanarFace or ExtrusionTermination.FiniteSurface&&Side is not (ExtrusionSide.Positive or ExtrusionSide.Negative))
            throw new CadValidationException("Invalid extrusion direction, operation, termination or reference payload.");
        if(Termination==ExtrusionTermination.Distance&&(distance<=1e-6||distance>1_000_000))
            throw new CadValidationException("Extrusion distance is outside its bounds.");
    }
    public ExtrusionOptions Refresh(DocumentSnapshot doc,DefinitionId part)
    {
        if(Datum is not {} id)return this;
        if(!doc.Datums.Planes.TryGetValue(id,out var plane)||plane.PartId!=part)
            throw new CadValidationException("Extrusion limit datum is missing or belongs to another part.");
        if(plane.IsStale)throw new DatumSourceUnavailableException();
        return this with{LimitPlane=plane.Frame,DatumRevision=plane.Revision};
    }
    public bool HasCurrentFace(DocumentSnapshot doc)=>(LimitFace is not {} face||
        doc.Features.TryGetValue(face.FeatureId,out var feature)&&!feature.IsStale&&!feature.IsSuppressed&&feature.Result==LimitGeometry&&
        feature.Result.Revision==face.Revision&&feature.Result.AssetId==face.Asset)&&
        (SupportFace is not {} support||doc.Features.TryGetValue(support.FeatureId,out var target)&&!target.IsStale&&!target.IsSuppressed&&
            target.Result==Source&&target.Result.AssetId==support.Asset&&target.Result.Revision==support.Revision);
    public void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        if(feature.Inputs.Length!=(Source is null?0:1))throw new CadValidationException("Extrusion target dependency differs from its operation.");
        if(Source is {} source&&(!doc.Features.TryGetValue(feature.Inputs[0],out var target)||target.PartId!=feature.PartId||
            !feature.IsStale&&(target.Result!=source||target.IsStale||target.IsSuppressed)))
            throw new CadValidationException("Extrusion target identity is not current.");
        if(LimitFace is {} face&&(face.DocumentId!=doc.Id||!doc.Features.TryGetValue(face.FeatureId,out var limit)||limit.PartId!=feature.PartId||
            face.Asset!=LimitGeometry!.AssetId||face.Revision!=LimitGeometry.Revision||!feature.IsStale&&!HasCurrentFace(doc)))
            throw new CadValidationException("Extrusion limit face is stale or in another context. Reselect explicitly.");
        if(SupportFace is {} support&&(support.DocumentId!=doc.Id||feature.Inputs.SingleOrDefault()!=support.FeatureId||
            !doc.Features.TryGetValue(support.FeatureId,out var supportTarget)||supportTarget.PartId!=feature.PartId||
            support.Asset!=Source!.AssetId||support.Revision!=Source.Revision||!feature.IsStale&&!HasCurrentFace(doc)))
            throw new CadValidationException("Extrusion starting support is stale or belongs to another exact target. Reselect explicitly.");
        var current=feature.IsStale?this:Refresh(doc,feature.PartId);
        if(!feature.IsStale&&(current.DatumRevision!=DatumRevision||
            LimitPlane is {} a&&current.LimitPlane is {} b&&!DatumPlanes.SameFrame(a,b)))
            throw new CadValidationException("Extrusion limit datum cache is stale.");
    }
}
