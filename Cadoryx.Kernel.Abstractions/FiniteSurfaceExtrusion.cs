using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum ExtrusionSurfaceKind { Plane=0,Sphere=1,Cylinder=2 }
public sealed record ExactSurfaceLimit(ExactTopologySelection Selection,GeometryAssetRef Geometry,ExtrusionSurfaceKind Kind);
public interface IFiniteSurfaceExtrusionResolver
{
    Task<IReadOnlyList<ExactSurfaceLimit>> EnumerateSurfaceLimitsAsync(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token=default);
    Task ConfirmFiniteSurfaceExtrusionAsync(ExtrudeRecipe recipe,GeometryAssetRef result,IAssetStore assets,CancellationToken token=default);
}
