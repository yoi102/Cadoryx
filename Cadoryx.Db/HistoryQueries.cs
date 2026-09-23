namespace Cadoryx.Db;

public readonly record struct HistoryQueryId(Guid Value) : ICadId
{
    public static HistoryQueryId New()=>new(Guid.NewGuid());
}

/// <summary>A saved diagnostic request. The source is copied so removing its library entry does not erase the query.</summary>
public sealed record HistoryQuery(HistoryQueryId Id,string Name,TopologyReference Source,FeatureId TargetFeatureId,int SchemaVersion=1)
{
    public void Validate(DocumentId document)
    {
        CadGuard.Id(Id);CadGuard.Name(Name);CadGuard.Id(TargetFeatureId);Source.Validate();
        if(SchemaVersion!=1||Source.DocumentId!=document||Source.Policy!=TopologyRebindPolicy.Semantic)
            throw new CadValidationException("Invalid history query contract.");
    }
}
