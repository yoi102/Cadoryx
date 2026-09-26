using MessagePack;
namespace Cadoryx.IO;

// Persisted field numbers are part of the file format: never reorder, recycle, or renumber keys.
[MessagePackObject]
public sealed record PackDocument(
    [property: Key(0)] Guid Id,
    [property: Key(1)] Guid StateId,
    [property: Key(2)] string Name,
    [property: Key(3)] Guid Root,
    [property: Key(4)] int Unit,
    [property: Key(5)] int Decimals,
    [property: Key(6)] double LinearTolerance,
    [property: Key(7)] double AngularTolerance,
    [property: Key(8)] bool GridVisible,
    [property: Key(9)] double GridSpacingMm,
    [property: Key(10)] bool GridSnap,
    [property: Key(11)] uint BackgroundTopArgb,
    [property: Key(12)] uint BackgroundBottomArgb,
    [property: Key(13)] bool OriginVisible,
    [property: Key(14)] int OriginStyle,
    [property: Key(15)] double OriginSizeMm,
    [property: Key(16)] int WorkPlaneKind = 0,
    [property: Key(17)] double WorkPlaneOffsetMm = 0,
    [property: Key(18)] PackAssemblyConstraint[]? AssemblyConstraints = null);

[MessagePackObject]
public sealed record PackAssemblyConstraint(
    [property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] int Kind,
    [property:Key(3)] Guid[] PrimarySlots,[property:Key(4)] Guid PrimaryDefinition,
    [property:Key(5)] Guid[]? SecondarySlots,[property:Key(6)] Guid? SecondaryDefinition,
    [property:Key(7)] double[] PrimaryPoint,[property:Key(8)] double[] SecondaryPoint,
    [property:Key(9)] double DistanceMm,[property:Key(10)] PackTransform? FixedWorld,
    [property:Key(11)] PackTopologyReference? PrimaryTopology,
    [property:Key(12)] PackTopologyReference? SecondaryTopology,
    [property:Key(13)] bool Enabled,[property:Key(14)] int Version,
    [property:Key(15)] double[]? PrimaryAxis=null,[property:Key(16)] double[]? SecondaryAxis=null,
    [property:Key(17)] double TargetAngleRad=0);

[MessagePackObject]
public sealed record PackStructure(
    [property: Key(0)] PackDefinition[] Definitions,
    [property: Key(1)] PackBody[] Bodies);

[MessagePackObject]
public sealed record PackFeatures(
    [property: Key(0)] PackFeature[] Features);

[MessagePackObject]
public sealed record PackPresentation(
    [property: Key(0)] PackLayer[] Layers,
    [property: Key(1)] PackMaterial[] Materials);

[MessagePackObject]
public sealed record PackDefinition(
    [property: Key(0)] Guid Id,
    [property: Key(1)] string Name,
    [property: Key(2)] bool Assembly,
    [property: Key(3)] Guid[] Bodies,
    [property: Key(4)] Guid[] Features,
    [property: Key(5)] PackSlot[] Children);

[MessagePackObject]
public sealed record PackSlot(
    [property: Key(0)] Guid Id,
    [property: Key(1)] Guid DefinitionId,
    [property: Key(2)] string Name,
    [property: Key(3)] PackTransform Transform,
    [property: Key(4)] bool Visible,
    [property: Key(5)] PackAppearance? Appearance);

[MessagePackObject]
public sealed record PackTransform(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z,
    [property: Key(3)] double Qx,
    [property: Key(4)] double Qy,
    [property: Key(5)] double Qz,
    [property: Key(6)] double Qw);

[MessagePackObject]
public sealed record PackGeometry(
    [property: Key(0)] string Hash,
    [property: Key(1)] Guid Revision,
    [property: Key(2)] int Kind,
    [property: Key(3)] double[] Min,
    [property: Key(4)] double[] Max,
    [property: Key(5)] double Volume,
    [property: Key(6)] string? ContextHash,
    [property: Key(7)] string? Entry);

[MessagePackObject]
public sealed record PackAppearance(
    [property: Key(0)] uint Argb,
    [property: Key(1)] bool ByLayer,
    [property: Key(2)] bool PreserveSourceStyles = false);

[MessagePackObject]
public sealed record PackBody(
    [property: Key(0)] Guid Id,
    [property: Key(1)] Guid Part,
    [property: Key(2)] string Name,
    [property: Key(3)] PackGeometry Geometry,
    [property: Key(4)] Guid? Producer,
    [property: Key(5)] Guid Layer,
    [property: Key(6)] PackAppearance Appearance,
    [property: Key(7)] bool Visible,
    [property: Key(8)] Guid? Material);

[MessagePackObject]
public sealed record PackFeature(
    [property: Key(0)] Guid Id,
    [property: Key(1)] Guid Part,
    [property: Key(2)] string Name,
    [property: Key(3)] PackRecipe Recipe,
    [property: Key(4)] Guid[] Inputs,
    [property: Key(5)] Guid Output,
    [property: Key(6)] PackGeometry Result,
    [property: Key(7)] int Version,
    [property: Key(8)] PackOutputMetadata? OutputMetadata=null);

[MessagePackObject]
public sealed record PackOutputMetadata(
    [property: Key(0)] string Name,
    [property: Key(1)] Guid Layer,
    [property: Key(2)] PackAppearance Appearance,
    [property: Key(3)] bool Visible,
    [property: Key(4)] Guid? Material);

[MessagePackObject]
public sealed record PackRecipe(
    [property: Key(0)] string Kind,
    [property: Key(1)] double[] Numbers,
    [property: Key(2)] PackTransform? Placement,
    [property: Key(3)] PackGeometry[] Sources,
    [property: Key(4)] double[][] Profile,
    [property: Key(5)] int Operation,
    [property: Key(6)] PackCircularHole[]? Holes=null,
    [property: Key(7)] PackPolygonHole[]? PolygonHoles=null,
    [property: Key(8)] PackMixedCurve[]? MixedCurves=null,
    [property: Key(9)] PackMixedCurve[][]? MixedHoles=null,
    [property: Key(10)] PackIsland[]? Islands=null,
    [property: Key(11)] double[][]? SplineControls=null);

[MessagePackObject]
public sealed record PackCircularHole([property:Key(0)] double X,[property:Key(1)] double Y,[property:Key(2)] double Radius);
[MessagePackObject]
public sealed record PackPolygonHole([property:Key(0)] double[][] Vertices);
[MessagePackObject]
public sealed record PackMixedCurve([property:Key(0)] double[] Start,[property:Key(1)] double[] End,
    [property:Key(2)] double[]? Middle);
[MessagePackObject]
public sealed record PackIsland([property:Key(0)] double[][] Points,[property:Key(1)] PackCircularHole? Circle=null,
    [property:Key(2)] PackMixedCurve[]? MixedCurves=null);

[MessagePackObject]
public sealed record PackLayer(
    [property: Key(0)] Guid Id,
    [property: Key(1)] string Name,
    [property: Key(2)] uint Argb,
    [property: Key(3)] bool Visible,
    [property: Key(4)] bool Locked);

[MessagePackObject]
public sealed record PackMaterial(
    [property: Key(0)] Guid Id,
    [property: Key(1)] string Name,
    [property: Key(2)] double Density);
