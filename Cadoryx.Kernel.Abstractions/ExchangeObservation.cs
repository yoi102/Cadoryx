using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Observed traversal and native scalar properties, not a topology
/// identity mapping or proof of complete geometric equivalence.</summary>
public sealed record ExchangeOccurrenceObservation(int Order,int Depth,string Name,string DefinitionName,
    bool Assembly,int DefinitionGroup,RigidTransform3d WorldTransform,int BodyCount);
public sealed record ExchangeModelObservation(int BodyInstances,int PartDefinitions,ImmutableArray<int> PartReuseCounts,
    Bounds3d BoundsMm,double SurfaceAreaMm2,double KnownSolidVolumeMm3,int SolidBodies,int OtherBodies,
    int TopologyFaces,int TopologyEdges,ImmutableArray<uint> OverallColorPalette,ImmutableArray<uint> SourceStyleColorPalette,ImmutableArray<uint> EffectiveColorPalette,
    ImmutableArray<ExchangeOccurrenceObservation> Occurrences);
public interface IExchangeObservationService
{
    Task<ExchangeModelObservation> ObserveExchangeAsync(DocumentSnapshot snapshot,IAssetStore assets,
        bool visibleOnly=true,CancellationToken token=default);
}
