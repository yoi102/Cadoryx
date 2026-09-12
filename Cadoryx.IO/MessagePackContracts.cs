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
    [property: Key(7)] double AngularTolerance);

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
    [property: Key(1)] bool ByLayer);

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
    [property: Key(5)] int Operation);

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
