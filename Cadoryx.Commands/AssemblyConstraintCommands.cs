using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Exact-path assembly anchors. Pair adjustment changes translation only and is never a global mate solver.</summary>
public static class AssemblyConstraintCommands
{
    public static ICadDocumentCommand AddFixed(AssemblyConstraintId id,string name,OccurrencePath path)=>
        new EditDocumentCommand("Fix assembly instance",document=>
    {
        CadGuard.Id(id);CadGuard.Name(name);
        if(document.AssemblyConstraints.ContainsKey(id))throw new CadValidationException("Constraint ID is already in use.");
        var occurrence=Resolve(document,path);
        var constraint=new AssemblyConstraint(id,name,AssemblyConstraintKind.Fixed,path,occurrence.DefinitionId,
            FixedWorld:occurrence.WorldTransform);
        return document with{AssemblyConstraints=document.AssemblyConstraints.Add(id,constraint)};
    });

    public static ICadDocumentCommand AddPair(AssemblyConstraintId id,string name,AssemblyConstraintKind kind,
        OccurrencePath primary,OccurrencePath secondary,Vector3d primaryLocalPoint,Vector3d secondaryLocalPoint,
        double targetDistanceMm)=>new EditDocumentCommand("Add assembly relation",document=>
    {
        CadGuard.Id(id);CadGuard.Name(name);primaryLocalPoint.Validate();secondaryLocalPoint.Validate();
        if(document.AssemblyConstraints.ContainsKey(id))throw new CadValidationException("Constraint ID is already in use.");
        if(kind is not (AssemblyConstraintKind.Coincident or AssemblyConstraintKind.Distance))
            throw new CadValidationException("Only coincident and distance pairs are supported.");
        var a=Resolve(document,primary);var b=Resolve(document,secondary);
        var constraint=new AssemblyConstraint(id,name,kind,primary,a.DefinitionId,secondary,b.DefinitionId,
            primaryLocalPoint,secondaryLocalPoint,targetDistanceMm);
        constraint.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.Add(id,constraint)};
    });

    public static ICadDocumentCommand Remove(AssemblyConstraintId id)=>new EditDocumentCommand("Remove assembly relation",document=>
        document.AssemblyConstraints.ContainsKey(id)?document with
            {AssemblyConstraints=document.AssemblyConstraints.Remove(id)}:throw new CadValidationException("Constraint does not exist."));

    /// <summary>Creates a relation between two independently picked analytic BRep datums.</summary>
    public static ICadDocumentCommand AddDatumPair(AssemblyConstraintId id,string name,AssemblyConstraintKind kind,
        AssemblyDatumReference primary,AssemblyDatumReference secondary,double target=0)=>
        new VerifiedDatumCommand(new EditDocumentCommand("Add geometric assembly relation",document=>
    {
        if(document.AssemblyConstraints.ContainsKey(id)||kind==AssemblyConstraintKind.Fixed)
            throw new CadValidationException("Invalid geometric relation identity or kind.");
        primary.Validate(document.Id);secondary.Validate(document.Id);
        var a=Resolve(document,primary.Path);var b=Resolve(document,secondary.Path);
        if(!primary.IsCurrent(document,primary.Path,a.DefinitionId)||
           !secondary.IsCurrent(document,secondary.Path,b.DefinitionId))
            throw new CadValidationException("A picked BRep datum is stale; reselect it.");
        bool axis=kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial or
            AssemblyConstraintKind.AngleAxes or AssemblyConstraintKind.PlanarMate;
        var relation=new AssemblyConstraint(id,name,kind,primary.Path,a.DefinitionId,
            secondary.Path,b.DefinitionId,primary.LocalPoint,secondary.LocalPoint,
            TargetDistanceMm:kind==AssemblyConstraintKind.Distance?target:0,SchemaVersion:2,
            PrimaryLocalAxis:axis?primary.LocalAxis:Vector3d.Zero,
            SecondaryLocalAxis:axis?secondary.LocalAxis:Vector3d.Zero,
            TargetAngleRad:kind==AssemblyConstraintKind.AngleAxes?target:0,
            PrimaryDatum:primary,SecondaryDatum:secondary);
        relation.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.Add(id,relation)};
    }),[primary,secondary]);

    private sealed class VerifiedDatumCommand(ICadDocumentCommand inner,
        IReadOnlyList<AssemblyDatumReference> datums) : ICadDocumentCommand
    {
        public string Name=>inner.Name;
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            await AssemblyDatumVerification.VerifyAsync(context,datums,token).ConfigureAwait(false);
            return await inner.PrepareAsync(context,token).ConfigureAwait(false);
        }
    }

    public static ICadDocumentCommand ReselectDatums(AssemblyConstraintId id,
        AssemblyDatumReference primary,AssemblyDatumReference secondary)=>
        new VerifiedDatumCommand(new EditDocumentCommand("Reselect assembly BRep datums",document=>
    {
        var original=Get(document,id);
        if(original.Kind==AssemblyConstraintKind.Fixed||!original.PrimaryPath.Equals(primary.Path)||
           !original.SecondaryPath!.Equals(secondary.Path)||
           original.PrimaryDefinitionId!=primary.DefinitionId||
           original.SecondaryDefinitionId!=secondary.DefinitionId)
            throw new CadValidationException("Reselection must keep both exact instance identities.");
        bool axis=original.Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial or
            AssemblyConstraintKind.AngleAxes or AssemblyConstraintKind.PlanarMate;
        var updated=original with{SchemaVersion=2,PrimaryTopology=null,SecondaryTopology=null,
            PrimaryDatum=primary,SecondaryDatum=secondary,
            PrimaryLocalPoint=primary.LocalPoint,SecondaryLocalPoint=secondary.LocalPoint,
            PrimaryLocalAxis=axis?primary.LocalAxis:Vector3d.Zero,
            SecondaryLocalAxis=axis?secondary.LocalAxis:Vector3d.Zero};
        updated.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    }),[primary,secondary]);

    public static ICadDocumentCommand AddAxisPair(AssemblyConstraintId id,string name,AssemblyConstraintKind kind,
        OccurrencePath primary,OccurrencePath secondary,Vector3d primaryPoint,Vector3d secondaryPoint,
        Vector3d primaryAxis,Vector3d secondaryAxis,double targetAngleRad=0)=>
        new EditDocumentCommand("Add assembly datum relation",document=>
    {
        CadGuard.Id(id);CadGuard.Name(name);
        if(document.AssemblyConstraints.ContainsKey(id))throw new CadValidationException("Constraint ID is already in use.");
        if(kind is not (AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial or
            AssemblyConstraintKind.AngleAxes or AssemblyConstraintKind.PlanarMate))
            throw new CadValidationException("Unsupported datum relation kind.");
        primaryPoint.Validate();secondaryPoint.Validate();
        var a=Resolve(document,primary);var b=Resolve(document,secondary);
        var constraint=new AssemblyConstraint(id,name,kind,primary,a.DefinitionId,secondary,b.DefinitionId,
            primaryPoint,secondaryPoint,PrimaryLocalAxis:primaryAxis.Normalized(),
            SecondaryLocalAxis:secondaryAxis.Normalized(),TargetAngleRad:targetAngleRad);
        constraint.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.Add(id,constraint)};
    });

    public static ICadDocumentCommand SetAnchors(AssemblyConstraintId id,Vector3d primaryPoint,Vector3d secondaryPoint)=>
        new EditDocumentCommand("Change assembly anchors",document=>
    {
        var constraint=Get(document,id);
        if(constraint.Kind==AssemblyConstraintKind.Fixed)throw new CadValidationException("Fixed relation has no point pair.");
        if(constraint.PrimaryDatum is not null||constraint.SecondaryDatum is not null)
            throw new CadValidationException("Reselect the BRep datum before changing geometric anchors.");
        primaryPoint.Validate();secondaryPoint.Validate();
        var updated=constraint with{PrimaryLocalPoint=primaryPoint,SecondaryLocalPoint=secondaryPoint};
        updated.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    });

    public static ICadDocumentCommand SetAxes(AssemblyConstraintId id,Vector3d primaryAxis,Vector3d secondaryAxis)=>
        new EditDocumentCommand("Change assembly axes",document=>
    {
        var constraint=Get(document,id);
        if(constraint.Kind is not (AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial or
            AssemblyConstraintKind.AngleAxes or AssemblyConstraintKind.PlanarMate))
            throw new CadValidationException("This relation has no axis pair.");
        if(constraint.PrimaryDatum is not null||constraint.SecondaryDatum is not null)
            throw new CadValidationException("Reselect the BRep datum before changing geometric axes.");
        var updated=constraint with{PrimaryLocalAxis=primaryAxis.Normalized(),
            SecondaryLocalAxis=secondaryAxis.Normalized()};
        updated.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    });

    public static ICadDocumentCommand SetAngle(AssemblyConstraintId id,double angleRad)=>
        new EditDocumentCommand("Change assembly datum angle",document=>
    {
        var constraint=Get(document,id);
        if(constraint.Kind!=AssemblyConstraintKind.AngleAxes||!double.IsFinite(angleRad)||
           angleRad<0||angleRad>Math.PI)
            throw new CadValidationException("Assembly angle must be between 0 and 180 degrees.");
        var updated=constraint with{TargetAngleRad=angleRad};
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    });

    public static ICadDocumentCommand SetEnabled(AssemblyConstraintId id,bool enabled)=>
        new EditDocumentCommand("Enable assembly relation",document=>
    {
        var constraint=Get(document,id);
        return constraint.IsEnabled==enabled?document:document with
            {AssemblyConstraints=document.AssemblyConstraints.SetItem(id,constraint with{IsEnabled=enabled})};
    });

    public static ICadDocumentCommand SetDistance(AssemblyConstraintId id,double distanceMm)=>
        new EditDocumentCommand("Change assembly distance",document=>
    {
        var constraint=Get(document,id);
        if(constraint.Kind!=AssemblyConstraintKind.Distance||!double.IsFinite(distanceMm)||distanceMm<0)
            throw new CadValidationException("Distance target must be finite and nonnegative.");
        return constraint.TargetDistanceMm==distanceMm?document:document with
            {AssemblyConstraints=document.AssemblyConstraints.SetItem(id,constraint with{TargetDistanceMm=distanceMm})};
    });

    /// <summary>Explicitly reselects full instance paths. Topology anchors require separate face/edge reselection.</summary>
    public static ICadDocumentCommand Retarget(AssemblyConstraintId id,OccurrencePath primary,OccurrencePath? secondary=null)=>
        new EditDocumentCommand("Retarget assembly relation",document=>
    {
        var constraint=Get(document,id);
        if(constraint.PrimaryTopology is not null||constraint.SecondaryTopology is not null||
           constraint.PrimaryDatum is not null||constraint.SecondaryDatum is not null)
            throw new CadValidationException("Reselect topology anchors before retargeting this constraint.");
        var a=Resolve(document,primary);
        AssemblyConstraint updated;
        if(constraint.Kind==AssemblyConstraintKind.Fixed)
        {
            if(secondary is not null)throw new CadValidationException("Fixed relation accepts one instance.");
            updated=constraint with{PrimaryPath=primary,PrimaryDefinitionId=a.DefinitionId,FixedWorld=a.WorldTransform};
        }
        else
        {
            if(secondary is null)throw new CadValidationException("Pair relation requires a second instance.");
            var b=Resolve(document,secondary);
            updated=constraint with{PrimaryPath=primary,PrimaryDefinitionId=a.DefinitionId,
                SecondaryPath=secondary,SecondaryDefinitionId=b.DefinitionId};
        }
        updated.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    });

    public static ICadDocumentCommand SetTopologyAnchor(AssemblyConstraintId id,bool primary,TopologyReference? reference)=>
        new EditDocumentCommand("Set exact assembly anchor",document=>
    {
        var constraint=Get(document,id);
        if(!primary&&constraint.Kind==AssemblyConstraintKind.Fixed)
            throw new CadValidationException("Fixed relation has no second anchor.");
        var updated=primary?constraint with{PrimaryTopology=reference}:constraint with{SecondaryTopology=reference};
        updated.Validate(document.Id);
        return document with{AssemblyConstraints=document.AssemblyConstraints.SetItem(id,updated)};
    });

    /// <summary>Adjusts one relation only. Point pairs translate; axis pairs rotate about their anchor,
    /// then coaxial pairs remove radial offset while preserving axial slide.</summary>
    public static ICadDocumentCommand AdjustPair(AssemblyConstraintId id)=>
        new EditDocumentCommand("Adjust assembly pair",document=>AdjustPairSnapshot(document,id));

    internal static DocumentSnapshot AdjustPairSnapshot(DocumentSnapshot document,AssemblyConstraintId id)
    {
        var relation=Get(document,id);
        if(!relation.IsEnabled||relation.Kind==AssemblyConstraintKind.Fixed||
           relation.PrimaryTopology is not null||relation.SecondaryTopology is not null)
            throw new CadValidationException("This relation cannot be adjusted by the point-pair tool.");
        if(relation.Evaluate(document).Status is AssemblyConstraintStatus.MissingInstance or
            AssemblyConstraintStatus.DefinitionChanged or AssemblyConstraintStatus.TopologyStale)
            throw new CadValidationException("Reselect stale assembly paths before adjusting.");
        var a=Resolve(document,relation.PrimaryPath);
        var second=relation.SecondaryPath!;
        var b=Resolve(document,second);
        if(second.Slots.Length<=relation.PrimaryPath.Slots.Length&&
           relation.PrimaryPath.Slots.Take(second.Slots.Length).SequenceEqual(second.Slots))
            throw new CadValidationException("The moving instance contains the reference instance.");
        var placement=OccurrencePlacement.Resolve(document,second);
        if(!placement.CanMoveIndependently)
            throw new CadValidationException("Make the shared parent assembly independent before adjusting this instance.");
        var anchorA=a.WorldTransform.Apply(relation.PrimaryLocalPoint);
        var anchorB=b.WorldTransform.Apply(relation.SecondaryLocalPoint);
        RigidTransform3d nextWorld;
        if(relation.Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial or
            AssemblyConstraintKind.AngleAxes or AssemblyConstraintKind.PlanarMate)
        {
            var directionA=a.WorldTransform.Rotation.Rotate(relation.PrimaryLocalAxis);
            var directionB=b.WorldTransform.Rotation.Rotate(relation.SecondaryLocalAxis);
            var targetDirection=relation.Kind switch
            {
                AssemblyConstraintKind.PlanarMate=>directionA*-1,
                AssemblyConstraintKind.AngleAxes=>AngleTarget(directionA,directionB,relation.TargetAngleRad),
                _=>directionA.Dot(directionB)>=0?directionA:directionA*-1
            };
            var rotation=Align(directionB,targetDirection)*b.WorldTransform.Rotation;
            var translation=anchorB-rotation.Rotate(relation.SecondaryLocalPoint);
            if(relation.Kind==AssemblyConstraintKind.Coaxial)
            {
                var between=anchorB-anchorA;
                translation-=between-directionA*between.Dot(directionA);
            }
            else if(relation.Kind==AssemblyConstraintKind.PlanarMate)
                translation-=directionA*(anchorB-anchorA).Dot(directionA);
            nextWorld=new(translation,rotation);
        }
        else
        {
            var direction=anchorB-anchorA;
            Vector3d target;
            if(relation.Kind==AssemblyConstraintKind.Coincident)target=anchorA;
            else
            {
                if(direction.Length<=1e-10)
                    throw new CadValidationException("Distance direction is undefined; place the instances apart first.");
                target=anchorA+direction/direction.Length*relation.TargetDistanceMm;
            }
            nextWorld=b.WorldTransform with{Translation=b.WorldTransform.Translation+target-anchorB};
        }
        var parentPath=new OccurrencePath(second.DocumentId,second.Slots.RemoveAt(second.Slots.Length-1));
        var parentWorld=parentPath.Slots.IsEmpty?RigidTransform3d.Identity:Resolve(document,parentPath).WorldTransform;
        var local=parentWorld.Inverse()*nextWorld;local.Validate();
        var owner=(AssemblyDefinition)document.Definitions[placement.OwnerId];
        int index=owner.Children.FindIndex(s=>s.Id==placement.Slot.Id);
        var candidate=document with{Definitions=document.Definitions.SetItem(owner.Id,owner with
            {Children=owner.Children.SetItem(index,placement.Slot with{LocalTransform=local})})};
        if(relation.Evaluate(candidate).Status!=AssemblyConstraintStatus.Satisfied)
            throw new CadValidationException("Single-relation adjustment did not satisfy the selected relation.");
        foreach(var other in document.AssemblyConstraints.Values.Where(c=>c.Id!=id&&c.IsEnabled))
            if(other.Evaluate(document).Status==AssemblyConstraintStatus.Satisfied&&
               other.Evaluate(candidate).Status==AssemblyConstraintStatus.Unsatisfied)
                throw new CadValidationException("Adjustment would break another satisfied assembly relation.");
        return candidate;
    }

    private static Vector3d AngleTarget(Vector3d primary,Vector3d secondary,double targetAngle)
    {
        var tangent=secondary-primary*secondary.Dot(primary);
        if(tangent.Length<1e-10)
        {
            var helper=Math.Abs(primary.Z)<0.9?Vector3d.UnitZ:new Vector3d(1,0,0);
            tangent=primary.Cross(helper);
        }
        return primary*Math.Cos(targetAngle)+tangent.Normalized()*Math.Sin(targetAngle);
    }

    private static Quaterniond Align(Vector3d source,Vector3d target)
    {
        double cosine=Math.Clamp(source.Dot(target),-1,1);
        var cross=source.Cross(target);
        double sine=cross.Length;
        if(sine<=1e-12)
        {
            if(cosine>0)return Quaterniond.Identity;
            var helper=Math.Abs(source.Z)<0.9?Vector3d.UnitZ:new Vector3d(1,0,0);
            return Quaterniond.FromAxisAngle(source.Cross(helper),Math.PI);
        }
        return Quaterniond.FromAxisAngle(cross/sine,Math.Atan2(sine,cosine));
    }

    private static AssemblyConstraint Get(DocumentSnapshot document,AssemblyConstraintId id)=>
        document.AssemblyConstraints.TryGetValue(id,out var constraint)?constraint:
            throw new CadValidationException("Constraint does not exist.");

    private static CadOccurrence Resolve(DocumentSnapshot document,OccurrencePath path)
    {
        if(path.DocumentId!=document.Id||path.Slots.IsEmpty)
            throw new CadValidationException("Instance path belongs to another document or is root.");
        return document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(path))??
            throw new CadValidationException("Exact instance path no longer exists.");
    }
}
