using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackFeatureBindings([property:Key(0)] PackFeatureBinding[] Bindings);
[MessagePackObject]
public sealed record PackFeatureBinding([property:Key(0)] Guid Feature,[property:Key(1)] PackTopologyReference Origin,
    [property:Key(2)] Guid Target,[property:Key(3)] Guid Revision,[property:Key(4)] string Asset,
    [property:Key(5)] int Index,[property:Key(6)] string Adapter,[property:Key(7)] int Version);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeFeatureBindings(IEnumerable<FeatureDefinition> features)=>Serialize(new PackFeatureBindings(
        features.Where(f=>f.TopologyBinding is not null).OrderBy(f=>f.Id.Value).Select(f=>
        {
            var b=f.TopologyBinding!;
            return new PackFeatureBinding(f.Id.Value,PackTopology(b.Origin),b.TargetFeatureId.Value,b.TargetRevision.Value,
                b.TargetAsset.Sha256,b.FullTopologyIndex,b.AdapterVersion,b.SchemaVersion);
        }).ToArray()));

    internal static ImmutableDictionary<FeatureId,FeatureDefinition> AttachFeatureBindings(ReadOnlyMemory<byte> bytes,
        ImmutableDictionary<FeatureId,FeatureDefinition> features,DocumentId document)
    {
        var result=features.ToBuilder();var seen=new HashSet<FeatureId>();
        foreach(var item in Read<PackFeatureBindings>(bytes).Bindings)
        {
            var id=new FeatureId(item.Feature);
            if(!seen.Add(id)||!result.TryGetValue(id,out var feature)||feature.Recipe is not HistoryFilletRecipe)
                throw new InvalidDataException("Duplicate or unknown feature binding.");
            var binding=new FeatureTopologyBinding(UnpackTopology(item.Origin),new(item.Target),new(item.Revision),new(item.Asset),
                item.Index,item.Adapter,item.Version);
            binding.Validate(document);result[id]=feature with{TopologyBinding=binding};
        }
        if(result.Values.Any(f=>f.Recipe is HistoryFilletRecipe&&f.TopologyBinding is null))
            throw new InvalidDataException("History fillet is missing its binding.");
        return result.ToImmutable();
    }
}
