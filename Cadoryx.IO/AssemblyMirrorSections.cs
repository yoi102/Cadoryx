using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackAssemblyMirrorBody([property:Key(0)] Guid Source,[property:Key(1)] Guid Target,
    [property:Key(2)] string SourceAsset,[property:Key(3)] Guid SourceRevision,[property:Key(4)] string ResultAsset,[property:Key(5)] Guid ResultRevision);
[MessagePackObject]
public sealed record PackAssemblyMirrorSlot([property:Key(0)] Guid Source,[property:Key(1)] Guid Target);
[MessagePackObject]
public sealed record PackAssemblyMirrorDefinition([property:Key(0)] Guid Source,[property:Key(1)] Guid Target,
    [property:Key(2)] PackAssemblyMirrorBody[] Bodies,[property:Key(3)] PackAssemblyMirrorSlot[] Slots);

internal static partial class MessagePackSections
{
    private static PackAssemblyMirrorDefinition Mirror(AssemblyMirrorDefinition m)=>new(m.Source.Value,m.Target.Value,
        m.Bodies.Select(b=>new PackAssemblyMirrorBody(b.Source.Value,b.Target.Value,b.SourceAsset.Sha256,b.SourceRevision.Value,b.ResultAsset.Sha256,b.ResultRevision.Value)).ToArray(),
        m.Slots.Select(s=>new PackAssemblyMirrorSlot(s.Source.Value,s.Target.Value)).ToArray());
    private static AssemblyMirrorDefinition Mirror(PackAssemblyMirrorDefinition m)
    {
        if(m is null||m.Bodies is null||m.Slots is null||m.Bodies.Length>100000||m.Slots.Length>100000||
            m.Bodies.Any(b=>b is null)||m.Slots.Any(s=>s is null))throw new InvalidDataException("Malformed mirror definition map.");
        return new(new(m.Source),new(m.Target),m.Bodies.Select(b=>new AssemblyMirrorBody(new(b.Source),new(b.Target),new(b.SourceAsset),new(b.SourceRevision),new(b.ResultAsset),new(b.ResultRevision))).ToImmutableArray(),
            m.Slots.Select(s=>new AssemblyMirrorSlot(new(s.Source),new(s.Target))).ToImmutableArray());
    }
    public static byte[] UpgradeAssemblyPatterns(ReadOnlyMemory<byte> bytes)
    {
        var pack=Read<PackAssemblyPatterns>(bytes);
        if(pack.Patterns is null||pack.Patterns.Any(p=>p is null||p.Mirrors is not null||(RepetitionKind)p.Kind==RepetitionKind.Mirror))
            throw new InvalidDataException("Legacy assembly array section contains undeclared mirror data.");
        return Serialize(pack with{Patterns=pack.Patterns.Select(p=>p with{Mirrors=[]}).ToArray()});
    }
}
