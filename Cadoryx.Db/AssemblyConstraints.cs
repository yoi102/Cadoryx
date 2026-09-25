namespace Cadoryx.Db;

public enum AssemblyConstraintKind { Fixed=0, Coincident=1, Distance=2, ParallelAxes=3, Coaxial=4 }
public enum AssemblyConstraintStatus { Disabled=0, Satisfied=1, Unsatisfied=2, MissingInstance=3,
    DefinitionChanged=4, TopologyStale=5, TopologyAnchorUnsupported=6 }

public sealed record AssemblyConstraintEvaluation(AssemblyConstraintStatus Status,double ErrorMm,string Explanation,
    double AngleErrorRad=0);

/// <summary>Definition IDs and exact paths bind an instance without silently selecting a replacement.
/// Optional topology provenance is diagnostic until a geometric mate solver is implemented.</summary>
public sealed record AssemblyConstraint(AssemblyConstraintId Id,string Name,AssemblyConstraintKind Kind,
    OccurrencePath PrimaryPath,DefinitionId PrimaryDefinitionId,
    OccurrencePath? SecondaryPath=null,DefinitionId? SecondaryDefinitionId=null,
    Vector3d PrimaryLocalPoint=default,Vector3d SecondaryLocalPoint=default,
    double TargetDistanceMm=0,RigidTransform3d? FixedWorld=null,
    TopologyReference? PrimaryTopology=null,TopologyReference? SecondaryTopology=null,
    bool IsEnabled=true,int SchemaVersion=1,
    Vector3d PrimaryLocalAxis=default,Vector3d SecondaryLocalAxis=default)
{
    public void Validate(DocumentId document)
    {
        CadGuard.Id(Id);CadGuard.Name(Name);CadGuard.Id(PrimaryDefinitionId);
        PrimaryLocalPoint.Validate();SecondaryLocalPoint.Validate();
        PrimaryLocalAxis.Validate();SecondaryLocalAxis.Validate();
        if(SchemaVersion!=1||!Enum.IsDefined(Kind)||PrimaryPath is null||PrimaryPath.DocumentId!=document||
            PrimaryPath.Slots.IsEmpty||PrimaryPath.Slots.Length>128||!double.IsFinite(TargetDistanceMm)||TargetDistanceMm<0)
            throw new CadValidationException("Invalid assembly constraint.");
        if(Kind==AssemblyConstraintKind.Fixed)
        {
            if(FixedWorld is not {} world||SecondaryPath is not null||SecondaryDefinitionId is not null||TargetDistanceMm!=0)
                throw new CadValidationException("Fixed constraint requires one instance and a world pose.");
            world.Validate();
        }
        else if(SecondaryPath is null||SecondaryDefinitionId is not {} secondary||
                SecondaryPath.DocumentId!=document||SecondaryPath.Slots.IsEmpty||SecondaryPath.Slots.Length>128||
                PrimaryPath.Equals(SecondaryPath)||
                FixedWorld is not null||Kind!=AssemblyConstraintKind.Distance&&TargetDistanceMm!=0)
            throw new CadValidationException("Pair constraint requires two distinct instance paths.");
        else CadGuard.Id(secondary);
        if(Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial)
        {
            if(PrimaryLocalAxis.Length<1e-10||SecondaryLocalAxis.Length<1e-10||
               Math.Abs(PrimaryLocalAxis.Length-1)>1e-8||Math.Abs(SecondaryLocalAxis.Length-1)>1e-8)
                throw new CadValidationException("Assembly axes must be unit directions.");
        }
        else if(PrimaryLocalAxis!=Vector3d.Zero||SecondaryLocalAxis!=Vector3d.Zero)
            throw new CadValidationException("Point relations cannot contain axis directions.");
        foreach(var reference in new[]{PrimaryTopology,SecondaryTopology}.OfType<TopologyReference>())
        {
            reference.Validate();
            if(reference.DocumentId!=document||reference.Policy!=TopologyRebindPolicy.ExactRevision)
                throw new CadValidationException("Assembly topology provenance must be exact and local.");
        }
        if(Kind==AssemblyConstraintKind.Fixed&&SecondaryTopology is not null)
            throw new CadValidationException("Fixed constraint has no second topology anchor.");
    }

    public AssemblyConstraintEvaluation Evaluate(DocumentSnapshot document)=>
        Evaluate(document,document.EnumerateOccurrences().ToDictionary(o=>o.Path));

    public AssemblyConstraintEvaluation Evaluate(DocumentSnapshot document,
        IReadOnlyDictionary<OccurrencePath,CadOccurrence> occurrences)
    {
        Validate(document.Id);
        if(!IsEnabled)return new(AssemblyConstraintStatus.Disabled,0,"Constraint is disabled.");
        if(!occurrences.TryGetValue(PrimaryPath,out var a)||
           SecondaryPath is {} second&&!occurrences.TryGetValue(second,out _))
            return new(AssemblyConstraintStatus.MissingInstance,0,"An exact instance path no longer exists.");
        CadOccurrence? b=SecondaryPath is {} path?occurrences[path]:null;
        if(a.DefinitionId!=PrimaryDefinitionId||b is not null&&b.DefinitionId!=SecondaryDefinitionId)
            return new(AssemblyConstraintStatus.DefinitionChanged,0,"An instance now refers to another definition; reselect it explicitly.");
        if(Kind==AssemblyConstraintKind.Fixed)
        {
            var expected=FixedWorld!.Value;var actual=a.WorldTransform;
            double error=(actual.Translation-expected.Translation).Length;
            var rotation=actual.Rotation;var target=expected.Rotation;
            double dot=Math.Abs(rotation.X*target.X+rotation.Y*target.Y+rotation.Z*target.Z+rotation.W*target.W);
            if(error>1e-7||dot<1-1e-10)
                return new(AssemblyConstraintStatus.Unsatisfied,error,"Fixed world pose has changed.");
        }
        bool Stale(TopologyReference? reference,DefinitionId definition)
        {
            if(reference is null)return false;
            return document.Definitions[definition] is not PartDefinition||
                !document.Features.TryGetValue(reference.FeatureId,out var feature)||feature.PartId!=definition||
                feature.OutputBodyId!=reference.OutputBodyId||feature.Result.Revision!=reference.OriginRevision||feature.IsStale||
                !document.Bodies.TryGetValue(reference.OutputBodyId,out var body)||body.Geometry.Revision!=reference.OriginRevision;
        }
        if(Stale(PrimaryTopology,PrimaryDefinitionId)||b is not null&&Stale(SecondaryTopology,SecondaryDefinitionId!.Value))
            return new(AssemblyConstraintStatus.TopologyStale,0,"An exact topology source is missing or changed; reselect it explicitly.");
        if(PrimaryTopology is not null||SecondaryTopology is not null)
            return new(AssemblyConstraintStatus.TopologyAnchorUnsupported,0,"Topology provenance is recorded but geometric mate solving is not available.");
        if(Kind==AssemblyConstraintKind.Fixed)
            return new(AssemblyConstraintStatus.Satisfied,0,"Fixed world pose is unchanged.");
        if(Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial)
        {
            var directionA=a.WorldTransform.Rotation.Rotate(PrimaryLocalAxis);
            var directionB=b!.WorldTransform.Rotation.Rotate(SecondaryLocalAxis);
            var angular=Math.Acos(Math.Clamp(Math.Abs(directionA.Dot(directionB)),-1,1));
            double radial=0;
            if(Kind==AssemblyConstraintKind.Coaxial)
            {
                var between=b.WorldTransform.Apply(SecondaryLocalPoint)-a.WorldTransform.Apply(PrimaryLocalPoint);
                radial=(between-directionA*between.Dot(directionA)).Length;
            }
            return angular<=1e-7&&radial<=1e-7
                ?new(AssemblyConstraintStatus.Satisfied,radial,"Axes are aligned.",angular)
                :new(AssemblyConstraintStatus.Unsatisfied,radial,"Axis angle or radial offset differs.",angular);
        }
        double distance=(a.WorldTransform.Apply(PrimaryLocalPoint)-b!.WorldTransform.Apply(SecondaryLocalPoint)).Length;
        double residual=Math.Abs(distance-TargetDistanceMm);
        return residual<=1e-7
            ?new(AssemblyConstraintStatus.Satisfied,residual,"Anchor distance is satisfied.")
            :new(AssemblyConstraintStatus.Unsatisfied,residual,"Anchor distance differs from the target.");
    }
}
