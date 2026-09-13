using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum HistoryResolutionStatus { Resolved, Missing, Stale, Ambiguous, Unsupported, WrongContext, Deleted, Generated, LimitExceeded }
/// <summary>Diagnostic-only locator in the adapter's full topology map. Not the adjacency index
/// used by ResolvedSubshape. Consumers must verify the exact asset and adapter before use.</summary>
public sealed record HistoryTarget(GeometryRevisionId Revision,AssetId Asset,int FullTopologyIndex,HistoryShapeKind Kind,string AdapterVersion);
public sealed record HistoryResolution(HistoryResolutionStatus Status,HistoryTarget? Target,int CandidateCount,string Diagnostic)
{
    /// <summary>Unique planned chain length; zero when no unique bounded path was established.</summary>
    public int PathLength {get;init;}
    /// <summary>Segments reduced to a unique verified successor before stopping.</summary>
    public int CompletedSteps {get;init;}
    public FeatureId? StoppedAt {get;init;}
}

public interface ITopologyHistoryResolver
{
    Task<HistoryResolution> TraceAsync(DocumentSnapshot snapshot,TopologyReference source,FeatureId target,
        IAssetStore assets,CancellationToken cancellationToken=default);
}

/// <summary>Conservative reduction of native evidence. Split or shared targets never select a first candidate.</summary>
public static class TopologyHistoryReduction
{
    public static HistoryResolution Resolve(TopologyHistory history,int sourceIndex,HistoryShapeKind kind)
        =>Resolve(history,0,sourceIndex,kind);
    public static HistoryResolution Resolve(TopologyHistory history,int sourceArgument,int sourceIndex,HistoryShapeKind kind)
    {
        history.Validate();
        HistoryResolution Fail(HistoryResolutionStatus s,int n=0)=>new(s,null,n,"HISTORY."+s.ToString().ToUpperInvariant());
        if(sourceArgument<0||sourceArgument>=history.ArgumentCount||sourceIndex<0||sourceIndex>=history.GetSource(sourceArgument).TopologyCount||!Enum.IsDefined(kind))
            return Fail(HistoryResolutionStatus.WrongContext);
        var entries=history.Entries.Where(e=>e.SourceArgument==sourceArgument&&e.SourceIndex==sourceIndex&&e.SourceKind==kind).ToArray();
        if(entries.Length==0||entries.Any(e=>e.Evolution==TopologyEvolution.Unmapped))return Fail(HistoryResolutionStatus.Unsupported);
        if(entries.Any(e=>e.Evolution==TopologyEvolution.Deleted))
            return Fail(entries.Length==1?HistoryResolutionStatus.Deleted:HistoryResolutionStatus.Ambiguous);
        var targets=entries.Where(e=>e.Evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified&&e.ResultKind==kind)
            .Select(e=>e.ResultIndex!.Value).Distinct().ToArray();
        if(targets.Length==0)return Fail(entries.Any(e=>e.Evolution==TopologyEvolution.Generated)?HistoryResolutionStatus.Generated:HistoryResolutionStatus.Unsupported);
        if(targets.Length!=1)return Fail(HistoryResolutionStatus.Ambiguous,targets.Length);
        int index=targets[0];
        if(history.Entries.Any(e=>(e.SourceArgument!=sourceArgument||e.SourceIndex!=sourceIndex)&&e.ResultIndex==index))return Fail(HistoryResolutionStatus.Ambiguous,1);
        return new(HistoryResolutionStatus.Resolved,new(history.ResultRevision,history.ResultAsset,index,kind,history.AdapterVersion),1,"HISTORY.RESOLVED");
    }
}
