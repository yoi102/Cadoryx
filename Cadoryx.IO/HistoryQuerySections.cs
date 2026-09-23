using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackHistoryQueries([property:Key(0)] PackHistoryQuery[] Queries);
[MessagePackObject]
public sealed record PackHistoryQuery([property:Key(0)] Guid Id,[property:Key(1)] string Name,
    [property:Key(2)] PackTopologyReference Source,[property:Key(3)] Guid Target,[property:Key(4)] int Version);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeHistoryQueries(IEnumerable<HistoryQuery> queries)=>Serialize(new PackHistoryQueries(
        queries.OrderBy(q=>q.Id.Value).Select(q=>new PackHistoryQuery(q.Id.Value,q.Name,PackTopology(q.Source),q.TargetFeatureId.Value,q.SchemaVersion)).ToArray()));

    internal static ImmutableDictionary<HistoryQueryId,HistoryQuery> DecodeHistoryQueries(ReadOnlyMemory<byte> bytes,DocumentId document)
    {
        var result=ImmutableDictionary.CreateBuilder<HistoryQueryId,HistoryQuery>();
        foreach(var item in Read<PackHistoryQueries>(bytes).Queries)
        {
            var query=new HistoryQuery(new(item.Id),item.Name,UnpackTopology(item.Source),new(item.Target),item.Version);
            query.Validate(document);
            if(!result.TryAdd(query.Id,query))throw new InvalidDataException("Duplicate history query identity.");
        }
        return result.ToImmutable();
    }
}
