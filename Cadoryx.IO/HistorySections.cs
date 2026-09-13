using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackHistories([property:Key(0)] PackHistory[] Histories);
[MessagePackObject]
public sealed record PackHistory([property:Key(0)] Guid Feature,[property:Key(1)] Guid SourceRevision,
    [property:Key(2)] string SourceAsset,[property:Key(3)] Guid ResultRevision,[property:Key(4)] string ResultAsset,
    [property:Key(5)] string Adapter,[property:Key(6)] string SourceFingerprint,[property:Key(7)] string ResultFingerprint,
    [property:Key(8)] int SourceCount,[property:Key(9)] int ResultCount,[property:Key(10)] PackHistoryEntry[] Entries,[property:Key(11)] int Version,
    [property:Key(12)] PackHistorySource[]? AdditionalSources=null);
[MessagePackObject]
public sealed record PackHistorySource([property:Key(0)] Guid Revision,[property:Key(1)] string Asset,
    [property:Key(2)] string Fingerprint,[property:Key(3)] int TopologyCount);
[MessagePackObject]
public sealed record PackHistoryEntry([property:Key(0)] int Source,[property:Key(1)] int SourceKind,
    [property:Key(2)] int Evolution,[property:Key(3)] int? Result,[property:Key(4)] int? ResultKind,[property:Key(5)] int SourceArgument=0);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeHistories(IEnumerable<FeatureDefinition> features)=>Serialize(new PackHistories(
        features.Where(f=>f.TopologyHistory is not null).OrderBy(f=>f.Id.Value).Select(f=>
        {
            var h=f.TopologyHistory!;
            return new PackHistory(f.Id.Value,h.SourceRevision.Value,h.SourceAsset.Sha256,h.ResultRevision.Value,h.ResultAsset.Sha256,h.AdapterVersion,
                h.SourceFingerprint,h.ResultFingerprint,h.SourceCount,h.ResultCount,h.Entries.Select(e=>new PackHistoryEntry(e.SourceIndex,(int)e.SourceKind,
                    (int)e.Evolution,e.ResultIndex,e.ResultKind is {} k?(int)k:null,e.SourceArgument)).ToArray(),h.SchemaVersion,
                h.AdditionalSources.Select(s=>new PackHistorySource(s.Revision.Value,s.Asset.Sha256,s.Fingerprint,s.TopologyCount)).ToArray());
        }).ToArray()));

    internal static ImmutableDictionary<FeatureId,FeatureDefinition> AttachHistories(ReadOnlyMemory<byte> bytes,ImmutableDictionary<FeatureId,FeatureDefinition> features)
    {
        var seen=new HashSet<FeatureId>();var result=features.ToBuilder();
        foreach(var h in Read<PackHistories>(bytes).Histories)
        {
            var id=new FeatureId(h.Feature);
            if(!seen.Add(id)||!result.TryGetValue(id,out var feature))throw new InvalidDataException("Duplicate or missing history feature.");
            if(h.AdditionalSources is null)throw new InvalidDataException("Missing history source table.");
            var history=new TopologyHistory(new(h.SourceRevision),new(h.SourceAsset),new(h.ResultRevision),new(h.ResultAsset),h.Adapter,
                h.SourceFingerprint,h.ResultFingerprint,h.SourceCount,h.ResultCount,h.Entries.Select(e=>new TopologyHistoryEntry(e.Source,
                    (HistoryShapeKind)e.SourceKind,(TopologyEvolution)e.Evolution,e.Result,e.ResultKind is {} k?(HistoryShapeKind)k:null,e.SourceArgument)).ToImmutableArray(),h.Version)
            {AdditionalSources=h.AdditionalSources.Select(s=>new TopologyHistorySource(new(s.Revision),new(s.Asset),s.Fingerprint,s.TopologyCount)).ToImmutableArray()};
            history.ValidateFor(feature);result[id]=feature with{TopologyHistory=history};
        }
        return result.ToImmutable();
    }
    internal static byte[] UpgradeHistorySources(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackHistories>(bytes);
        if(old.Histories.Any(h=>h.Version!=1||h.AdditionalSources is {Length:>0}||h.Entries.Any(e=>e.SourceArgument!=0)))
            throw new InvalidDataException("History v1 cannot contain multiple source arguments.");
        return Serialize(new PackHistories(old.Histories.Select(h=>h with{AdditionalSources=[]}).ToArray()));
    }
}
