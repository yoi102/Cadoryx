using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackFeatureBindings([property:Key(0)] PackFeatureBinding[] Bindings);
[MessagePackObject]
public sealed record PackFeatureBindingsV1([property:Key(0)] PackFeatureBindingV1[] Bindings);
[MessagePackObject]
public sealed record PackFeatureBindingV1([property:Key(0)] Guid Feature,[property:Key(1)] PackTopologyReference Origin,
    [property:Key(2)] Guid Target,[property:Key(3)] Guid Revision,[property:Key(4)] string Asset,
    [property:Key(5)] int Index,[property:Key(6)] string Adapter,[property:Key(7)] int Version);
[MessagePackObject]
public sealed record PackExactTopologySelection([property:Key(0)] Guid Document,[property:Key(1)] Guid Feature,
    [property:Key(2)] Guid Revision,[property:Key(3)] string Asset,[property:Key(4)] string Fingerprint,
    [property:Key(5)] int Index,[property:Key(6)] int Kind,[property:Key(7)] string Adapter,[property:Key(8)] int Version);
[MessagePackObject]
public sealed record PackFeatureBinding([property:Key(0)] Guid Feature,[property:Key(1)] PackTopologyReference? Origin,
    [property:Key(2)] Guid Target,[property:Key(3)] Guid Revision,[property:Key(4)] string Asset,
    [property:Key(5)] int Index,[property:Key(6)] string Adapter,[property:Key(7)] int Version,
    [property:Key(8)] PackExactTopologySelection? ExactEdge=null,[property:Key(9)] PackExactTopologySelection? SupportFace=null);

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeExactFeatureBindings(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeatureBindingsV1>(bytes);
        return Serialize(new PackFeatureBindings(old.Bindings.Select(b=>new PackFeatureBinding(b.Feature,b.Origin,b.Target,
            b.Revision,b.Asset,b.Index,b.Adapter,b.Version)).ToArray()));
    }
    internal static byte[] EncodeFeatureBindings(IEnumerable<FeatureDefinition> features)=>Serialize(new PackFeatureBindings(
        features.Where(f=>f.TopologyBinding is not null).OrderBy(f=>f.Id.Value).Select(f=>
        {
            var b=f.TopologyBinding!;
            return new PackFeatureBinding(f.Id.Value,b.Origin is {} origin?PackTopology(origin):null,b.TargetFeatureId.Value,b.TargetRevision.Value,
                b.TargetAsset.Sha256,b.FullTopologyIndex,b.AdapterVersion,b.SchemaVersion,
                b.ExactEdge is {} edge?PackExact(edge):null,b.SupportFace is {} face?PackExact(face):null);
        }).ToArray()));

    internal static ImmutableDictionary<FeatureId,FeatureDefinition> AttachFeatureBindings(ReadOnlyMemory<byte> bytes,
        ImmutableDictionary<FeatureId,FeatureDefinition> features,DocumentId document)
    {
        var result=features.ToBuilder();var seen=new HashSet<FeatureId>();
        foreach(var item in Read<PackFeatureBindings>(bytes).Bindings)
        {
            var id=new FeatureId(item.Feature);
            if(!seen.Add(id)||!result.TryGetValue(id,out var feature)||feature.Recipe is not (HistoryFilletRecipe or HistoryChamferRecipe))
                throw new InvalidDataException("Duplicate or unknown feature binding.");
            var binding=new FeatureTopologyBinding(item.Origin is {} origin?UnpackTopology(origin):null,new(item.Target),new(item.Revision),new(item.Asset),
                item.Index,item.Adapter,item.Version)
                {ExactEdge=item.ExactEdge is {} edge?UnpackExact(edge):null,SupportFace=item.SupportFace is {} face?UnpackExact(face):null};
            binding.Validate(document);result[id]=feature with{TopologyBinding=binding};
        }
        if(result.Values.Any(f=>f.Recipe is HistoryFilletRecipe or HistoryChamferRecipe&&f.TopologyBinding is null))
            throw new InvalidDataException("Bound local feature is missing its binding.");
        return result.ToImmutable();
    }
    private static PackExactTopologySelection PackExact(ExactTopologySelection s)=>new(s.DocumentId.Value,s.FeatureId.Value,
        s.Revision.Value,s.Asset.Sha256,s.Fingerprint,s.FullTopologyIndex,(int)s.Kind,s.AdapterVersion,s.SchemaVersion);
    private static ExactTopologySelection UnpackExact(PackExactTopologySelection s)
    {
        var value=new ExactTopologySelection(new(s.Document),new(s.Feature),new(s.Revision),new(s.Asset),s.Fingerprint,
            s.Index,(HistoryShapeKind)s.Kind,s.Adapter,s.Version);
        value.Validate();return value;
    }
}
