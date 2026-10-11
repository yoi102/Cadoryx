using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Discovers a native tangent contour on one immutable source. This is read-only evidence, not a modeling commit.</summary>
public interface IContourLocalResolver
{
    Task<ContourChainEvidence> DiscoverContourAsync(DocumentSnapshot snapshot,ExactTopologySelection seed,
        LocalFeatureOperation operation,ExactTopologySelection? supportFace,IAssetStore assets,CancellationToken cancellationToken=default);
    Task ConfirmContourRecipeAsync(ContourLocalRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default);
}
