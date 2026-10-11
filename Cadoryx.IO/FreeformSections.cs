using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackFreeformGeometry([property:Key(0)] int Kind,[property:Key(1)] double[][] Poles,
    [property:Key(2)] double[] Weights,[property:Key(3)] double[] Knots,[property:Key(4)] int[] Multiplicities,
    [property:Key(5)] int Degree,[property:Key(6)] bool Periodic,[property:Key(7)] double FirstParameter,
    [property:Key(8)] double LastParameter,[property:Key(9)] bool Reversed);
[MessagePackObject]
public sealed record PackSketchFreeform([property:Key(0)] Guid Id,[property:Key(1)] PackFreeformGeometry Geometry,
    [property:Key(2)] bool Construction,[property:Key(3)] PackSketchCurveEndpoints? Endpoints=null);

internal static partial class MessagePackSections
{
    private static PackFreeformGeometry? PackFreeform(SketchFreeformGeometry? g)=>g is null?null:
        new((int)g.Kind,g.Poles.Select(p=>new[]{p.X,p.Y}).ToArray(),g.Weights.ToArray(),g.Knots.ToArray(),g.Multiplicities.ToArray(),
            g.Degree,g.Periodic,g.FirstParameter,g.LastParameter,g.Reversed);
    private static SketchFreeformGeometry? ReadFreeform(PackFreeformGeometry? g)
    {
        if(g is null)return null;
        if(g.Poles is not {Length:>=2 and <=SketchFreeformGeometry.MaximumPoles}||g.Weights is null||g.Knots is null||g.Multiplicities is null||
           g.Weights.Length>SketchFreeformGeometry.MaximumPoles||g.Knots.Length>SketchFreeformGeometry.MaximumPoles+26||g.Multiplicities.Length!=g.Knots.Length)
            throw new InvalidDataException("Invalid copied freeform tables.");
        var result=new SketchFreeformGeometry((SketchFreeformKind)g.Kind,g.Poles.Select(ReadMixedPoint).ToImmutableArray(),g.Weights.ToImmutableArray(),
            g.Knots.ToImmutableArray(),g.Multiplicities.ToImmutableArray(),g.Degree,g.Periodic,g.FirstParameter,g.LastParameter,g.Reversed);
        result.Validate();return result;
    }
    internal static ReadOnlyMemory<byte> UpgradeFreeformSketches(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Freeforms is not null))
            throw new InvalidDataException("Copied freeform curve in legacy sketch schema.");
        return bytes;
    }
    private static void RequireLegacyPeriodicInterval(PackFreeformGeometry? g)
    {
        if(g is {Periodic:true}&&g.Knots is {Length:>=2}&&(g.FirstParameter<g.Knots[0]||g.LastParameter>g.Knots[^1]))
            throw new InvalidDataException("Periodic interval outside its basis domain in legacy curve schema.");
    }
    internal static ReadOnlyMemory<byte> UpgradePeriodicSketchIntervals(ReadOnlyMemory<byte> bytes)
    {
        foreach(var sketch in Read<PackSketches>(bytes).Sketches)foreach(var curve in sketch.Freeforms??[])
            RequireLegacyPeriodicInterval(curve.Geometry);
        return bytes;
    }
}
