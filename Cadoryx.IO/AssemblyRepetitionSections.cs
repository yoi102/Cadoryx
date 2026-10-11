using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackAssemblyPatterns([property:Key(0)] PackAssemblyPattern[] Patterns);
[MessagePackObject]
public sealed record PackAssemblyPattern([property:Key(0)] Guid Id,[property:Key(1)] Guid Owner,[property:Key(2)] Guid Source,
    [property:Key(3)] string Name,[property:Key(4)] int Kind,[property:Key(5)] PackTransform Frame,
    [property:Key(6)] int Count,[property:Key(7)] double Step,[property:Key(8)] Guid[] Copies,
    [property:Key(9)] bool FullCircle,[property:Key(10)] Guid? Datum,[property:Key(11)] Guid? DatumRevision,
    [property:Key(12)] PackAssemblyMirrorDefinition[]? Mirrors=null);

internal static partial class MessagePackSections
{
    public static byte[] EncodeAssemblyPatterns(IEnumerable<AssemblyRepetition> patterns)=>Serialize(new PackAssemblyPatterns(patterns.OrderBy(p=>p.Id)
        .Select(p=>new PackAssemblyPattern(p.Id,p.Owner.Value,p.Source.Value,p.Name,(int)p.Kind,T(p.Frame),p.Count,p.Step,
            p.CopySlots.Select(s=>s.Value).ToArray(),p.FullCircle,p.Datum,p.DatumRevision,p.Mirrors.Select(Mirror).ToArray())).ToArray()));
    public static ImmutableDictionary<Guid,AssemblyRepetition> DecodeAssemblyPatterns(ReadOnlyMemory<byte> bytes)
    {
        var pack=Read<PackAssemblyPatterns>(bytes);
        if(pack.Patterns is null||pack.Patterns.Length>1024)throw new InvalidDataException("Invalid assembly array table.");
        return pack.Patterns.Select(p=>
        {
            if(p is null||p.Frame is null||p.Copies is null||p.Copies.Length>255||p.Mirrors is null||p.Mirrors.Length>4096)throw new InvalidDataException("Malformed assembly array.");
            var pattern=new AssemblyRepetition(p.Id,new(p.Owner),new(p.Source),p.Name,(RepetitionKind)p.Kind,T(p.Frame),p.Count,p.Step,
                p.Copies.Select(s=>new ComponentSlotId(s)).ToImmutableArray(),p.FullCircle,p.Datum,p.DatumRevision)
                {Mirrors=p.Mirrors.Select(Mirror).ToImmutableArray()};
            pattern.Validate();return pattern;
        }).ToImmutableDictionary(p=>p.Id);
    }
}
