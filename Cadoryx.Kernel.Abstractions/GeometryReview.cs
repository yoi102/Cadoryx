using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record CuttingPlane(Vector3d Normal,double OffsetMm)
{
    public void Validate()
    {
        Normal.Validate();
        if(Math.Abs(Normal.Length-1)>1e-10||!double.IsFinite(OffsetMm)||Math.Abs(OffsetMm)>1e9)
            throw new CadValidationException("Section normal must be a unit vector and offset must be finite within ±1e9 mm.");
    }
}
public enum BodyPairRelation { Separated,Touching,Contained,Interfering }
public sealed record BodyPairFinding(GeometryInstance First,GeometryInstance Second,BodyPairRelation Relation,double DistanceMm,double OverlapVolumeMm3);
public sealed record InterferenceReport(ImmutableArray<BodyPairFinding> Pairs,double ToleranceMm);
public interface IGeometryReviewKernel
{
    Task<GeometryResult> SectionAsync(IReadOnlyList<GeometryInstance> instances,CuttingPlane plane,IAssetStore assets,CancellationToken token=default);
    Task<InterferenceReport> CheckInterferenceAsync(IReadOnlyList<GeometryInstance> instances,double toleranceMm,IAssetStore assets,CancellationToken token=default);
}

public static class GeometryInstanceGuard
{
    public static void Validate(DocumentSnapshot snapshot,IReadOnlyList<GeometryInstance> inputs)
    {
        var paths=snapshot.EnumerateOccurrences().ToDictionary(o=>o.Path);
        foreach(var input in inputs)
        {
            if(input.Path.DocumentId!=snapshot.Id||!paths.TryGetValue(input.Path,out var occurrence)||
                !snapshot.Bodies.TryGetValue(input.BodyId,out var body)||body.PartId!=occurrence.DefinitionId||
                body.Geometry!=input.Geometry||occurrence.WorldTransform!=input.WorldTransform||
                body.Producer is {} producer&&snapshot.Features[producer].IsStale)
                throw new CadValidationException("Review input no longer matches this document instance and geometry.");
        }
    }
}
