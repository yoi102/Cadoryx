using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum RepetitionKind { Mirror=0, Linear=1, Circular=2 }
public enum RepetitionOutput { Compound=0, Fused=1 }
/// <summary>Explicit author decision when native contributors have incompatible presentation assignments.</summary>
public enum RepetitionStylePolicy { VerifiedInheritance=0, WholeBody=1 }
/// <summary>Copies an exact result in part-local coordinates. Copy IDs are provenance, never topology correspondence.</summary>
public sealed record RepetitionRecipe(GeometryAssetRef Source,RepetitionKind Kind,RigidTransform3d Frame,
    int Count,double Step,ImmutableArray<Guid> CopyIds,RepetitionOutput Output=RepetitionOutput.Compound,
    bool IncludeSource=true,bool FullCircle=false,Guid? Datum=null,Guid? DatumRevision=null,
    RepetitionStylePolicy StylePolicy=RepetitionStylePolicy.VerifiedInheritance):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();Frame.Validate();CadGuard.Finite(Step);
        if(!Enum.IsDefined(Kind)||!Enum.IsDefined(Output)||!Enum.IsDefined(StylePolicy)||Count<1||Count>256||CopyIds.IsDefault||CopyIds.Length!=Count||
            CopyIds.Any(id=>id==Guid.Empty)||CopyIds.Distinct().Count()!=Count||
            (Datum is null)!=(DatumRevision is null)||Datum==Guid.Empty||DatumRevision==Guid.Empty)
            throw new CadValidationException("Invalid repetition kind, count or identity.");
        if(Kind==RepetitionKind.Mirror)
        {if(Count!=(IncludeSource?2:1)||Step!=0||FullCircle)throw new CadValidationException("Mirror produces one reflected copy and optionally the source.");}
        else
        {
            if(!IncludeSource||Count<2)throw new CadValidationException("An array count includes its source and must be between 2 and 256.");
            if(Kind==RepetitionKind.Linear&&(FullCircle||Math.Abs(Step)<1e-7||Math.Abs(Step)>1_000_000))
                throw new CadValidationException("Linear spacing must be nonzero and within one million mm.");
            if(Kind==RepetitionKind.Circular&&(FullCircle?Step!=0:Math.Abs(Step)<1e-8||Math.Abs(Step)*(Count-1)>=Math.Tau-1e-8))
                throw new CadValidationException("Circular spacing must produce distinct copies within one turn; use full-circle distribution for a complete ring.");
        }
    }
    public RepetitionRecipe Resize(int count)
    {
        if(count<1||count>256)throw new CadValidationException("Repetition count exceeds its bounds.");
        return this with{Count=count,CopyIds=Enumerable.Range(0,count).Select(i=>i<CopyIds.Length?CopyIds[i]:Guid.NewGuid()).ToImmutableArray()};
    }
    public RepetitionRecipe Refresh(DocumentSnapshot doc,DefinitionId part)
    {
        if(Datum is not {} id)return this;
        return this with{Frame=DatumConstructions.InputFrame(doc,part,id,Kind!=RepetitionKind.Mirror),DatumRevision=DatumConstructions.Revision(doc.Datums,id)};
    }
    public void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        if(feature.Inputs.Length!=1||!doc.Features.TryGetValue(feature.Inputs[0],out var source)||source.PartId!=feature.PartId||
            !feature.IsStale&&(source.Result!=Source||source.IsStale||source.IsSuppressed))
            throw new CadValidationException("Repetition requires its exact current source feature result.");
        var refreshed=feature.IsStale?this:Refresh(doc,feature.PartId);
        if(!feature.IsStale&&(refreshed.DatumRevision!=DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,Frame)))
            throw new CadValidationException("Repetition datum cache is stale.");
    }
}
