using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackSketchCurveEndpoints([property:Key(0)] Guid Start,[property:Key(1)] Guid End,
    [property:Key(2)] Guid StartAnchor,[property:Key(3)] Guid EndAnchor);

internal static partial class MessagePackSections
{
    private static PackSketchCurveEndpoints? PackEndpoints(SketchCurveEndpoints? e)=>e is null?null:
        new(e.Start.Value,e.End.Value,e.StartAnchor.Value,e.EndAnchor.Value);
    private static SketchCurveEndpoints? ReadEndpoints(PackSketchCurveEndpoints? e)=>e is null?null:
        new(new(e.Start),new(e.End),new(e.StartAnchor),new(e.EndAnchor));
    internal static ReadOnlyMemory<byte> UpgradeExactEndpointSketches(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>(s.Ellipses??[]).Any(e=>e.Endpoints is not null)||
            (s.Freeforms??[]).Any(e=>e.Endpoints is not null)))
            throw new InvalidDataException("Exact author endpoints in legacy sketch schema.");
        return bytes;
    }
}
