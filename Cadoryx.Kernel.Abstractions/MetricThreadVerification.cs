using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Reproves a current modeled thread result from its exact retained source and versioned profile.</summary>
public interface IMetricThreadVerifier
{
    Task ConfirmModeledThreadAsync(GeometryRecipe recipe,GeometryAssetRef result,IAssetStore assets,CancellationToken token=default);
}

/// <summary>Runs native thread work independently from the calling kernel's serial queue.
/// Returned geometry owns its asset lease; implementations must honor cancellation.</summary>
public interface IMetricThreadExecutor
{
    Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token);
    Task<DrawingHoleEvidence> InspectAsync(HoleRecipe recipe,GeometryAssetRef current,DrawingTopologyReference selected,
        IAssetStore assets,CancellationToken token);
}
