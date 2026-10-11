using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record ExactPlanarLimit(ExactTopologySelection Selection,GeometryAssetRef Geometry);
public interface IExtrusionLimitResolver
{
    Task<IReadOnlyList<ExactPlanarLimit>> EnumeratePlanarLimitsAsync(DocumentSnapshot document,IAssetStore assets,CancellationToken token=default);
}
