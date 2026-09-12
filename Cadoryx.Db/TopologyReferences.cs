namespace Cadoryx.Db;

public readonly record struct TopologyReferenceId(Guid Value) : ICadId
{
    public static TopologyReferenceId New()=>new(Guid.NewGuid());
}
public enum TopologyKind { Face=0, Edge=1 }
public enum BoxBoundary { XMin=0, XMax=1, YMin=2, YMax=3, ZMin=4, ZMax=5 }
public enum TopologyRebindPolicy { ExactRevision=0, Semantic=1 }

/// <summary>Definition-space reference. Occurrence paths belong to the consumer, not a shared part.
/// Origin is provenance, never a subshape enumeration index or an asset retention request.</summary>
public sealed record TopologyReference(TopologyReferenceId Id,DocumentId DocumentId,FeatureId FeatureId,
    BodyId OutputBodyId,GeometryRevisionId OriginRevision,TopologyKind Kind,BoxBoundary Boundary,
    BoxBoundary? SecondBoundary=null,TopologyRebindPolicy Policy=TopologyRebindPolicy.Semantic,int SchemaVersion=1)
{
    public void Validate()
    {
        CadGuard.Id(Id);CadGuard.Id(DocumentId);CadGuard.Id(FeatureId);CadGuard.Id(OutputBodyId);CadGuard.Id(OriginRevision);
        if(SchemaVersion!=1||!Enum.IsDefined(Kind)||!Enum.IsDefined(Boundary)||!Enum.IsDefined(Policy))
            throw new CadValidationException("Unsupported topology reference contract.");
        if(Kind==TopologyKind.Face&&SecondBoundary is not null || Kind==TopologyKind.Edge&&
            (SecondBoundary is not {} second||!Enum.IsDefined(second)||(int)Boundary/2>=(int)second/2))
            throw new CadValidationException("An edge requires two ordered boundaries on distinct axes.");
    }
    public static TopologyReference Box(DocumentSnapshot snapshot,FeatureId feature,BoxBoundary boundary,
        BoxBoundary? second=null,TopologyRebindPolicy policy=TopologyRebindPolicy.Semantic)
    {
        if(!snapshot.Features.TryGetValue(feature,out var f)||f.Recipe is not BoxRecipe)
            throw new CadValidationException("Box semantics require a box feature.");
        var value=new TopologyReference(TopologyReferenceId.New(),snapshot.Id,f.Id,f.OutputBodyId,f.Result.Revision,
            second is null?TopologyKind.Face:TopologyKind.Edge,boundary,second,policy);
        value.Validate();return value;
    }
}
