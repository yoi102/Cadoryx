using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum HistoryShapeKind { Face=0, Edge=1, Vertex=2 }
public enum TopologyEvolution { Unchanged=0, Modified=1, Generated=2, Deleted=3, Unmapped=4, Ambiguous=5, GeneratedOnly=6 }
public sealed record TopologyHistoryEntry(int SourceIndex,HistoryShapeKind SourceKind,TopologyEvolution Evolution,
    int? ResultIndex,HistoryShapeKind? ResultKind,int SourceArgument=0,Guid? CopyId=null,bool PathAmbiguous=false);
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
        if(SchemaVersion is not (1 or 2 or 3 or 4)||AdditionalSources.IsDefault||AdditionalSources.Length>255||(SchemaVersion is 1 or 3 or 4)&&!AdditionalSources.IsEmpty||
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
                (SchemaVersion==4?e.CopyId is null||e.CopyId==Guid.Empty:e.CopyId is not null||e.PathAmbiguous)||
                SchemaVersion<3&&e.Evolution is TopologyEvolution.Ambiguous or TopologyEvolution.GeneratedOnly||
                (e.Evolution is TopologyEvolution.Deleted or TopologyEvolution.Unmapped or TopologyEvolution.Ambiguous or TopologyEvolution.GeneratedOnly)==e.ResultIndex.HasValue)
                throw new CadValidationException("Invalid topology history entry.");
        }
        if(Entries.GroupBy(e=>(e.SourceArgument,e.SourceIndex)).Any(g=>g.Select(e=>e.SourceKind).Distinct().Count()!=1)||
            Entries.Where(e=>e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex).Any(g=>g.Select(e=>e.ResultKind).Distinct().Count()!=1))
            throw new CadValidationException("Inconsistent topology history kind.");
    }
    public void ValidateFor(FeatureDefinition feature)
    {
        Validate();
        if(feature.Recipe is not (LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe or ContourLocalRecipe or PlanarPushPullRecipe or DefeatureRecipe or BooleanRecipe or DraftRecipe or HoleRecipe or HolePatternRecipe {Layout.IncludeSource:true} or RepetitionRecipe)||
            feature.Recipe is DefeatureRecipe&&SchemaVersion!=1||
            feature.Recipe is BooleanRecipe&&SchemaVersion!=2||
            (feature.Recipe is HoleRecipe {Head:not HoleHead.Plain} or HolePatternRecipe)&&SchemaVersion!=3||
            SchemaVersion==3&&feature.Recipe is not (HoleRecipe or HolePatternRecipe)||
            (feature.Recipe is RepetitionRecipe)!=(SchemaVersion==4)||
            ResultRevision!=feature.Result.Revision||ResultAsset!=feature.Result.AssetId)
            throw new CadValidationException("Topology history belongs to different geometry.");
        if(feature.Recipe is RepetitionRecipe repetition&&
            (!Entries.Select(e=>e.CopyId!.Value).ToHashSet().SetEquals(repetition.CopyIds)||
             Entries.GroupBy(e=>e.CopyId).Any(g=>!g.Select(e=>(e.SourceIndex,e.SourceKind)).ToHashSet()
                 .SetEquals(Entries.Select(e=>(e.SourceIndex,e.SourceKind))))))
            throw new CadValidationException("Repetition history must account for the exact authored copy identities and every source slot.");
        // A replay template is authoring data. With the seed included, only the seed
        // result is the physical operand whose native topology is consumed.
        var inputs=feature.Recipe is HolePatternRecipe pattern?new[]{pattern.Source}:feature.Recipe.AssetInputs.ToArray();
        if(inputs.Length!=ArgumentCount||feature.Inputs.Length!=ArgumentCount)throw new CadValidationException("Topology history input count differs from feature dependencies.");
        for(int i=0;i<inputs.Length;i++)
        {
            var source=GetSource(i);
            if(source.Revision!=inputs[i].Revision||source.Asset!=inputs[i].AssetId)throw new CadValidationException("Topology history input order or geometry mismatch.");
        }
    }
}
