using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum HistoryShapeKind { Face=0, Edge=1, Vertex=2 }
public enum TopologyEvolution { Unchanged=0, Modified=1, Generated=2, Deleted=3, Unmapped=4 }
public sealed record TopologyHistoryEntry(int SourceIndex,HistoryShapeKind SourceKind,TopologyEvolution Evolution,
    int? ResultIndex,HistoryShapeKind? ResultKind,int SourceArgument=0);
public sealed record TopologyHistorySource(GeometryRevisionId Revision,AssetId Asset,string Fingerprint,int TopologyCount);

/// <summary>Indices belong only to these exact assets and the declared adapter's full topology map.
/// This is algorithm evidence, not a persistent semantic name.</summary>
public sealed record TopologyHistory(GeometryRevisionId SourceRevision,AssetId SourceAsset,
    GeometryRevisionId ResultRevision,AssetId ResultAsset,string AdapterVersion,
    string SourceFingerprint,string ResultFingerprint,int SourceCount,int ResultCount,
    ImmutableArray<TopologyHistoryEntry> Entries,int SchemaVersion=1)
{
    /// <summary>Operands 1..N, in recipe order. The original source fields are operand zero.</summary>
    public ImmutableArray<TopologyHistorySource> AdditionalSources {get;init;}=[];
    public int ArgumentCount=>1+AdditionalSources.Length;
    public TopologyHistorySource GetSource(int argument)=>argument==0?new(SourceRevision,SourceAsset,SourceFingerprint,SourceCount):
        argument>0&&argument<=AdditionalSources.Length?AdditionalSources[argument-1]:throw new CadValidationException("Invalid history argument index.");
    public void Validate()
    {
        CadGuard.Id(SourceRevision);CadGuard.Id(ResultRevision);SourceAsset.Validate();ResultAsset.Validate();CadGuard.Name(AdapterVersion);
        static bool Hash(string s)=>s is {Length:64}&&s.All(Uri.IsHexDigit);
        if(SchemaVersion is not (1 or 2)||AdditionalSources.IsDefault||AdditionalSources.Length>255||SchemaVersion==1&&!AdditionalSources.IsEmpty||
            !Hash(SourceFingerprint)||!Hash(ResultFingerprint)||SourceCount is <1 or >100000||ResultCount is <1 or >100000||
            Entries.IsDefaultOrEmpty||Entries.Length>100000||Entries.Distinct().Count()!=Entries.Length)
            throw new CadValidationException("Invalid topology history contract.");
        foreach(var source in AdditionalSources)
        {
            if(source is null)throw new CadValidationException("Null history source.");
            CadGuard.Id(source.Revision);source.Asset.Validate();
            if(!Hash(source.Fingerprint)||source.TopologyCount is <1 or >100000)throw new CadValidationException("Invalid history source.");
        }
        if(SchemaVersion==2&&Enumerable.Range(0,ArgumentCount).Any(argument=>!Entries.Any(e=>e.SourceArgument==argument)))
            throw new CadValidationException("History must account for every source argument.");
        foreach(var e in Entries)
        {
            if(e.SourceArgument<0||e.SourceArgument>=ArgumentCount||e.SourceIndex<0||e.SourceIndex>=GetSource(e.SourceArgument).TopologyCount||!Enum.IsDefined(e.SourceKind)||!Enum.IsDefined(e.Evolution)||
                e.ResultIndex.HasValue!=e.ResultKind.HasValue||e.ResultIndex is {} i&&(i<0||i>=ResultCount)||
                e.ResultKind is {} k&&!Enum.IsDefined(k)||
                (e.Evolution is TopologyEvolution.Deleted or TopologyEvolution.Unmapped)==e.ResultIndex.HasValue)
                throw new CadValidationException("Invalid topology history entry.");
        }
        if(Entries.GroupBy(e=>(e.SourceArgument,e.SourceIndex)).Any(g=>g.Select(e=>e.SourceKind).Distinct().Count()!=1)||
            Entries.Where(e=>e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex).Any(g=>g.Select(e=>e.ResultKind).Distinct().Count()!=1))
            throw new CadValidationException("Inconsistent topology history kind.");
    }
    public void ValidateFor(FeatureDefinition feature)
    {
        Validate();
        if(feature.Recipe is not (LocalFeatureRecipe or BooleanRecipe)||
            feature.Recipe is BooleanRecipe&&SchemaVersion!=2||
            ResultRevision!=feature.Result.Revision||ResultAsset!=feature.Result.AssetId)
            throw new CadValidationException("Topology history belongs to different geometry.");
        var inputs=feature.Recipe.AssetInputs.ToArray();
        if(inputs.Length!=ArgumentCount||feature.Inputs.Length!=ArgumentCount)throw new CadValidationException("Topology history input count differs from feature dependencies.");
        for(int i=0;i<inputs.Length;i++)
        {
            var source=GetSource(i);
            if(source.Revision!=inputs[i].Revision||source.Asset!=inputs[i].AssetId)throw new CadValidationException("Topology history input order or geometry mismatch.");
        }
    }
}
