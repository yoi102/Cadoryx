using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackExternalParts([property:Key(0)] PackExternalPart[] Links);

[MessagePackObject]
public sealed record PackExternalPart(
    [property:Key(0)] Guid TargetPart,[property:Key(1)] Guid SourceDocument,
    [property:Key(2)] Guid SourcePart,[property:Key(3)] Guid SourceState,
    [property:Key(4)] string SourceSha256,[property:Key(5)] string SourcePath,
    [property:Key(6)] string? AbsolutePathHint,[property:Key(7)] string LocalFingerprint);

internal static partial class MessagePackSections
{
    public static byte[] EncodeExternalParts(IEnumerable<ExternalPartLink> links)=>Serialize(new PackExternalParts(
        links.OrderBy(link=>link.TargetPartId.Value).Select(link=>new PackExternalPart(
            link.TargetPartId.Value,link.SourceDocumentId.Value,link.SourcePartId.Value,
            link.SourceStateId.Value,link.SourceSha256,link.SourcePath,
            link.AbsolutePathHint,link.LocalFingerprint)).ToArray()));

    public static ImmutableDictionary<DefinitionId,ExternalPartLink> DecodeExternalParts(ReadOnlyMemory<byte> bytes)
    {
        var packed=Read<PackExternalParts>(bytes);
        if(packed.Links is null||packed.Links.Length>4096)
            throw new InvalidDataException("Malformed external part table.");
        return packed.Links.Select(link=>
        {
            if(link is null)throw new InvalidDataException("Null external part link.");
            return new ExternalPartLink(new(link.TargetPart),new(link.SourceDocument),new(link.SourcePart),
                new(link.SourceState),link.SourceSha256,link.SourcePath,link.AbsolutePathHint,
                link.LocalFingerprint);
        }).ToImmutableDictionary(link=>link.TargetPartId);
    }
}
