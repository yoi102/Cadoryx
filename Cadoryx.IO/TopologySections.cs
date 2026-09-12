using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackTopologyReferences([property:Key(0)] PackTopologyReference[] References);
[MessagePackObject]
public sealed record PackTopologyReference([property:Key(0)] Guid Id,[property:Key(1)] Guid Document,
    [property:Key(2)] Guid Feature,[property:Key(3)] Guid Body,[property:Key(4)] Guid OriginRevision,
    [property:Key(5)] int Kind,[property:Key(6)] int Boundary,[property:Key(7)] int? SecondBoundary,
    [property:Key(8)] int Policy,[property:Key(9)] int Version);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeTopology(IEnumerable<TopologyReference> references)=>Serialize(new PackTopologyReferences(
        references.OrderBy(r=>r.Id.Value).Select(r=>new PackTopologyReference(r.Id.Value,r.DocumentId.Value,r.FeatureId.Value,
            r.OutputBodyId.Value,r.OriginRevision.Value,(int)r.Kind,(int)r.Boundary,r.SecondBoundary is {} second?(int)second:null,(int)r.Policy,r.SchemaVersion)).ToArray()));

    internal static ImmutableDictionary<TopologyReferenceId,TopologyReference> DecodeTopology(ReadOnlyMemory<byte> bytes)
    {
        var result=ImmutableDictionary.CreateBuilder<TopologyReferenceId,TopologyReference>();
        foreach(var r in Read<PackTopologyReferences>(bytes).References)
        {
            var reference=new TopologyReference(new(r.Id),new(r.Document),new(r.Feature),new(r.Body),new(r.OriginRevision),
                (TopologyKind)r.Kind,(BoxBoundary)r.Boundary,r.SecondBoundary is {} second?(BoxBoundary)second:null,(TopologyRebindPolicy)r.Policy,r.Version);
            reference.Validate();
            if(result.ContainsKey(reference.Id))throw new InvalidDataException("Duplicate topology reference identity.");
            result.Add(reference.Id,reference);
        }
        return result.ToImmutable();
    }
}
