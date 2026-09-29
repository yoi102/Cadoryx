using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record DrawingProjectionResult(ImmutableArray<DrawingStroke> Strokes,
    Point2d Minimum,Point2d Maximum);

/// <summary>True BRep hidden-line projection. Consumers retain only 2D drawing data.</summary>
public interface ITechnicalDrawingKernel
{
    Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,
        DrawingViewKind kind,Vector3d? sectionNormal,double sectionOffsetMm,
        IAssetStore assets,CancellationToken token=default);
}
