using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Resolution is derived state. Never cache it across a document state change.</summary>
public sealed record TopologyInspection(DocumentId DocumentId,DocumentStateId StateId,ImmutableArray<TopologyResolution> Results)
{
    public bool IsCurrent(DocumentSnapshot snapshot)=>DocumentId==snapshot.Id&&StateId==snapshot.StateId;
}
public static class TopologyReferenceInspection
{
    public static async Task<TopologyInspection> InspectAsync(DocumentSnapshot snapshot,IAssetStore assets,ITopologyResolver resolver,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hold=new DocumentAssetLease(snapshot,assets);
        var results=ImmutableArray.CreateBuilder<TopologyResolution>();
        foreach(var reference in snapshot.TopologyReferences.Values.OrderBy(r=>r.Id.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await resolver.ResolveAsync(snapshot,reference,assets,cancellationToken).ConfigureAwait(false));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(snapshot.Id,snapshot.StateId,results.ToImmutable());
    }
}
