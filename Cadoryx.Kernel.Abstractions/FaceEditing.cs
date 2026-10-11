using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IFaceEditResolver
{
    Task<Vector3d> InspectPlanarFaceAsync(DocumentSnapshot snapshot,ExactTopologySelection face,IAssetStore assets,CancellationToken token=default);
    Task ConfirmFaceEditRecipeAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default);
}
