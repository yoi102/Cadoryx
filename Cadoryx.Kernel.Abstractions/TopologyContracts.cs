using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum TopologyResolutionStatus { Resolved=0, Missing=1, Stale=2, Ambiguous=3, Unsupported=4, WrongContext=5 }
/// <summary>A diagnostic locator for this exact BRep and adapter version only. Never persist it.
/// A native modeling consumer must resolve again against its own live shape graph.</summary>
public sealed record ResolvedSubshape(GeometryRevisionId Revision,AssetId AssetId,TopologyKind Kind,int Index,string KernelVersion);
public sealed record TopologyResolution(TopologyReferenceId ReferenceId,TopologyResolutionStatus Status,
    ResolvedSubshape? Target,int CandidateCount,string Diagnostic)
{
    public static TopologyResolution Candidates(TopologyReference reference,GeometryAssetRef geometry,string kernelVersion,IReadOnlyList<int> indices)
        =>indices.Count switch
        {
            0=>new(reference.Id,TopologyResolutionStatus.Missing,null,0,"TOPOLOGY.MISSING"),
            1=>new(reference.Id,TopologyResolutionStatus.Resolved,new(geometry.Revision,geometry.AssetId,reference.Kind,indices[0],kernelVersion),1,"TOPOLOGY.RESOLVED"),
            _=>new(reference.Id,TopologyResolutionStatus.Ambiguous,null,indices.Count,"TOPOLOGY.AMBIGUOUS")
        };
}
/// <summary>Optional kernel capability; no topological naming is implied by IGeometryKernel.</summary>
public interface ITopologyResolver
{
    Task<TopologyResolution> ResolveAsync(DocumentSnapshot snapshot,TopologyReference reference,IAssetStore assets,CancellationToken cancellationToken=default);
}
