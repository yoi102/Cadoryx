using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackEllipseGeometry([property:Key(0)] double[] Center,[property:Key(1)] double[] Cosine,
    [property:Key(2)] double[] Sine,[property:Key(3)] double StartAngle,[property:Key(4)] double SweepAngle);
[MessagePackObject]
public sealed record PackSketchEllipse([property:Key(0)] Guid Id,[property:Key(1)] PackEllipseGeometry Geometry,
    [property:Key(2)] bool Construction,[property:Key(3)] PackSketchCurveEndpoints? Endpoints=null);
[MessagePackObject]
public sealed record PackSketchNativeProjection([property:Key(0)] Guid TargetEntity,[property:Key(1)] PackExactTopologySelection Selection,
    [property:Key(2)] PackGeometry Source,[property:Key(3)] PackTransform Plane,[property:Key(4)] Guid[] Anchors);

internal static partial class MessagePackSections
{
    private static PackEllipseGeometry? PackEllipse(SketchEllipseGeometry? ellipse)=>ellipse is {} e?
        new([e.Center.X,e.Center.Y],[e.Cosine.X,e.Cosine.Y],[e.Sine.X,e.Sine.Y],e.StartAngle,e.SweepAngle):null;
    private static SketchEllipseGeometry? ReadEllipse(PackEllipseGeometry? ellipse)
    {
        if(ellipse is null)return null;
        var result=new SketchEllipseGeometry(ReadMixedPoint(ellipse.Center),ReadMixedPoint(ellipse.Cosine),ReadMixedPoint(ellipse.Sine),ellipse.StartAngle,ellipse.SweepAngle);
        result.Validate();return result;
    }
    internal static ReadOnlyMemory<byte> UpgradeEllipseSketches(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Ellipses is not null||s.NativeProjections is not null))throw new InvalidDataException("Exact conic or edge projection in legacy sketch schema.");
        return bytes;
    }
}
