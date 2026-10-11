using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record DrawingProjectionResult(ImmutableArray<DrawingStroke> Strokes,
    Point2d Minimum,Point2d Maximum);

/// <summary>Explicit source-aligned line family; spacing is in projected model millimeters.</summary>
public sealed record SectionHatchPattern(double AngleDegrees,double SpacingMm,bool Enabled=true);

/// <summary>True BRep hidden-line projection. Consumers retain only 2D drawing data.</summary>
public interface ITechnicalDrawingKernel
{
    /// <param name="sectionHatchSpacingMm">Spacing in projected model millimeters; callers convert the desired paper spacing by the view scale.</param>
    Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,
        DrawingViewKind kind,Vector3d? sectionNormal,double sectionOffsetMm,
        IAssetStore assets,CancellationToken token=default,double sectionHatchSpacingMm=2.5,
        IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns=null);
}

/// <summary>Owned independent native computation. Must never call the invoking
/// kernel's serial queue; cancellation must stop the owned native operation.</summary>
public interface INativeDrawingExecutor:ITechnicalDrawingKernel{}
