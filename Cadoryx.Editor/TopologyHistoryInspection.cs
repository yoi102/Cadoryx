using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed record TopologyHistoryInspection(DocumentId DocumentId,DocumentStateId StateId,
    TopologyReferenceId ReferenceId,FeatureId FeatureId,HistoryResolution Result)
{
    public bool IsCurrent(DocumentSnapshot snapshot)=>DocumentId==snapshot.Id&&StateId==snapshot.StateId;
    public static async Task<TopologyHistoryInspection> InspectAsync(DocumentSnapshot snapshot,TopologyReference reference,
        FeatureId target,IAssetStore assets,ITopologyHistoryResolver resolver,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();using var hold=new DocumentAssetLease(snapshot,assets);
        var result=await resolver.TraceAsync(snapshot,reference,target,assets,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();return new(snapshot.Id,snapshot.StateId,reference.Id,target,result);
    }
}
