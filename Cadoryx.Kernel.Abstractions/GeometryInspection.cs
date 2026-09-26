using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record GeometryInstance(OccurrencePath Path,BodyId BodyId,GeometryAssetRef Geometry,RigidTransform3d WorldTransform);
public sealed record BodyInspection(OccurrencePath Path,BodyId BodyId,double AreaMm2,double? VolumeMm3,
    Vector3d? VolumeCentroidMm,int Faces,int Edges,int Solids);
public sealed record MinimumDistance(double DistanceMm,Vector3d PointOnFirstMm,Vector3d PointOnSecondMm,int EquivalentSolutions);
public sealed record GeometryInspection(ImmutableArray<BodyInspection> Bodies,MinimumDistance? Distance);
public interface IGeometryInspector
{
    Task<GeometryInspection> InspectAsync(IReadOnlyList<GeometryInstance> instances,IAssetStore assets,CancellationToken token=default);
}
