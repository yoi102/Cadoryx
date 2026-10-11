using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A parent-local array of exact instance slots. It shares definitions, never geometry copies.</summary>
public sealed record AssemblyRepetition(Guid Id,DefinitionId Owner,ComponentSlotId Source,string Name,
    RepetitionKind Kind,RigidTransform3d Frame,int Count,double Step,ImmutableArray<ComponentSlotId> CopySlots,
    bool FullCircle=false,Guid? Datum=null,Guid? DatumRevision=null)
{
    public ImmutableArray<AssemblyMirrorDefinition> Mirrors {get;init;}=[];
    public void Validate()
    {
        CadGuard.Id(Owner);CadGuard.Id(Source);CadGuard.Name(Name);Frame.Validate();CadGuard.Finite(Step);
        if(Id==Guid.Empty||!Enum.IsDefined(Kind)||Mirrors.IsDefault||Count<2||Count>256||
            CopySlots.IsDefault||CopySlots.Length!=Count-1||CopySlots.Contains(Source)||CopySlots.Distinct().Count()!=CopySlots.Length||
            (Datum is null)!=(DatumRevision is null)||Datum==Guid.Empty||DatumRevision==Guid.Empty)
            throw new CadValidationException("Invalid assembly array identity, layout or slot count.");
        if(Kind==RepetitionKind.Mirror&&(Count!=2||Step!=0||FullCircle)||Kind!=RepetitionKind.Mirror&&!Mirrors.IsEmpty)
            throw new CadValidationException("An assembly mirror has one reflected instance and no array spacing.");
        foreach(var slot in CopySlots)CadGuard.Id(slot);
        if(Kind==RepetitionKind.Linear&&(FullCircle||Math.Abs(Step)<1e-7||Math.Abs(Step)>1_000_000)||
            Kind==RepetitionKind.Circular&&(FullCircle?Step!=0:Math.Abs(Step)<1e-8||Math.Abs(Step)*(Count-1)>=Math.Tau-1e-8))
            throw new CadValidationException("Assembly array spacing must generate distinct positions within its bounds.");
    }
    public (AssemblyDefinition Owner,ComponentSlot Seed) Resolve(DocumentSnapshot document)
    {
        if(document.Definitions.GetValueOrDefault(Owner) is not AssemblyDefinition owner||owner.Children.SingleOrDefault(s=>s.Id==Source) is not {} seed)
            throw new CadValidationException("Assembly array owner or source slot is missing. Detach the layout before removing its source.");
        return (owner,seed);
    }
    public AssemblyRepetition Refresh(DocumentSnapshot document)
    {
        var (_,seed)=Resolve(document);if(Datum is not {} id)return this;
        var frame=seed.LocalTransform*DatumConstructions.InputFrame(document,seed.DefinitionId,id,Kind!=RepetitionKind.Mirror);
        return this with{Frame=frame,DatumRevision=DatumConstructions.Revision(document.Datums,id)};
    }
    public RigidTransform3d Placement(int index,RigidTransform3d seed)
    {
        Validate();if(index<1||index>=Count)throw new CadValidationException("Assembly array placement index is outside the layout.");
        if(Kind==RepetitionKind.Mirror)return AssemblyMirrors.Placement(Frame,seed);
        RigidTransform3d transform;
        if(Kind==RepetitionKind.Linear)transform=new(Frame.Rotation.Rotate(new(1,0,0))*(index*Step),Quaterniond.Identity);
        else
        {var rotation=Quaterniond.FromAxisAngle(Frame.Rotation.Rotate(Vector3d.UnitZ),index*(FullCircle?Math.Tau/Count:Step));transform=new(Frame.Translation-rotation.Rotate(Frame.Translation),rotation);}
        return transform*seed;
    }
    public AssemblyRepetition Resize(int count)
    {
        if(count<2||count>256)throw new CadValidationException("Assembly array count must include the source and be between 2 and 256.");
        return this with{Count=count,CopySlots=Enumerable.Range(0,count-1).Select(i=>i<CopySlots.Length?CopySlots[i]:ComponentSlotId.New()).ToImmutableArray()};
    }
}

public static class AssemblyRepetitions
{
    public static bool ControlsPose(DocumentSnapshot document,OccurrencePath path)=>document.AssemblyPatterns.Values.Any(p=>p.CopySlots.Any(path.Slots.Contains));
    public static void ValidateApplied(DocumentSnapshot document,bool mirrorsReady=true)
    {
        if(document.AssemblyPatterns.Count>1024)throw new CadValidationException("Too many assembly arrays.");
        var generated=document.AssemblyPatterns.Values.SelectMany(p=>p.CopySlots).ToArray();
        if(generated.Distinct().Count()!=generated.Length||document.AssemblyPatterns.Values.Select(p=>(p.Owner,p.Source)).Distinct().Count()!=document.AssemblyPatterns.Count)
            throw new CadValidationException("Assembly array slots or source ownership overlap.");
        var targets=document.AssemblyPatterns.Values.SelectMany(p=>p.Mirrors).Select(m=>m.Target).ToArray();
        if(targets.Distinct().Count()!=targets.Length)throw new CadValidationException("Mirror derived definitions are owned by multiple layouts.");
        if(mirrorsReady)AssemblyMirrors.OrderedPatterns(document);
        foreach(var (id,p) in document.AssemblyPatterns)
        {
            p.Validate();if(id!=p.Id||generated.Contains(p.Source))throw new CadValidationException("Invalid assembly array key or generated source slot.");
            if(!mirrorsReady&&p.Kind==RepetitionKind.Mirror)continue;
            AssemblyMirrors.ValidateApplied(document,p);
            var (owner,seed)=p.Resolve(document);var refreshed=p.Refresh(document);
            if(refreshed.DatumRevision!=p.DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,p.Frame))throw new CadValidationException("Assembly array datum cache is stale.");
            for(int i=0;i<p.CopySlots.Length;i++)
            {
                var copy=owner.Children.SingleOrDefault(s=>s.Id==p.CopySlots[i]);
                if(copy is null||copy.DefinitionId!=AssemblyMirrors.OutputDefinition(p,seed)||!DatumPlanes.SameFrame(copy.LocalTransform,p.Placement(i+1,seed.LocalTransform)))
                    throw new CadValidationException("Assembly array output slot differs from its authored layout. Detach before editing a generated pose or definition.");
            }
        }
    }
}
