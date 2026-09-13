using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : ITopologyHistoryResolver
{
    public Task<HistoryResolution> TraceAsync(DocumentSnapshot snapshot,TopologyReference reference,FeatureId target,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        reference.Validate();
        // Own the exact geometry across queue waits and native work, including source outputs consumed by a child.
        return TraceHeld();
        async Task<HistoryResolution> TraceHeld()
        {
            using var hold=new DocumentAssetLease(snapshot,assets);
            return await Run(()=>
            {
                HistoryResolution Fail(HistoryResolutionStatus s)=>new(s,null,0,"HISTORY."+s.ToString().ToUpperInvariant());
                if(reference.DocumentId!=snapshot.Id)return Fail(HistoryResolutionStatus.WrongContext);
                if(!snapshot.Features.TryGetValue(reference.FeatureId,out var source)||!snapshot.Features.TryGetValue(target,out var feature))return Fail(HistoryResolutionStatus.Missing);
                if(source.OutputBodyId!=reference.OutputBodyId)return Fail(HistoryResolutionStatus.WrongContext);
                if(reference.Policy==TopologyRebindPolicy.ExactRevision&&reference.OriginRevision!=source.Result.Revision)return Fail(HistoryResolutionStatus.Stale);
                if(source.Recipe is not BoxRecipe box||feature.Recipe is not (LocalFeatureRecipe or BooleanRecipe)||feature.TopologyHistory is not {} history)
                    return Fail(HistoryResolutionStatus.Unsupported);
                int argument=feature.Inputs.IndexOf(source.Id);
                if(argument<0)return Fail(HistoryResolutionStatus.Unsupported);
                history.ValidateFor(feature);
                bool adapterVerified=feature.Recipe is BooleanRecipe?history.AdapterVersion==BooleanHistoryAdapterVersion:
                    history.AdapterVersion==HistoryAdapterVersion||history.AdapterVersion==PreviousAxisHistoryAdapterVersion||history.AdapterVersion==LegacyHistoryAdapterVersion;
                if(!adapterVerified)return Fail(HistoryResolutionStatus.Unsupported);
                using var targetShape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var targetMap=RepairSnapshot.Create(targetShape);
                if(targetMap.Fingerprint!=history.ResultFingerprint||targetMap.Topology.Count!=history.ResultCount)return Fail(HistoryResolutionStatus.Stale);
                static ShapeKind NativeKind(HistoryShapeKind kind)=>kind switch{HistoryShapeKind.Face=>ShapeKind.Face,HistoryShapeKind.Edge=>ShapeKind.Edge,_=>ShapeKind.Vertex};
                var sourceMaps=new List<RepairSnapshot>();
                try
                {
                    for(int a=0;a<history.ArgumentCount;a++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var evidence=history.GetSource(a);
                        if(!snapshot.Features.TryGetValue(feature.Inputs[a],out var upstream))return Fail(HistoryResolutionStatus.Missing);
                        if(upstream.Result.Revision!=evidence.Revision||upstream.Result.AssetId!=evidence.Asset)return Fail(HistoryResolutionStatus.Stale);
                        using var shape=OcctGeometryBridge.ReadShape(upstream.Result,assets);var map=RepairSnapshot.Create(shape);sourceMaps.Add(map);
                        if(map.Fingerprint!=evidence.Fingerprint||map.Topology.Count!=evidence.TopologyCount)return Fail(HistoryResolutionStatus.Stale);
                        var covered=history.Entries.Where(e=>e.SourceArgument==a).Select(e=>e.SourceIndex).ToHashSet();
                        if(map.Topology.Any(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex&&!covered.Contains(t.Selection.Index)))
                            return Fail(HistoryResolutionStatus.Unsupported);
                    }
                    foreach(var entry in history.Entries)
                        if(sourceMaps[entry.SourceArgument].Topology[entry.SourceIndex].Kind!=NativeKind(entry.SourceKind)||entry.ResultIndex is {} i&&targetMap.Topology[i].Kind!=NativeKind(entry.ResultKind!.Value))
                            return Fail(HistoryResolutionStatus.Unsupported);
                    var candidates=FindSemantic(sourceMaps[argument],box,reference.Kind,reference.Boundary,reference.SecondBoundary);
                    cancellationToken.ThrowIfCancellationRequested();
                    if(candidates.Length!=1)return Fail(candidates.Length==0?HistoryResolutionStatus.Missing:HistoryResolutionStatus.Ambiguous);
                    return TopologyHistoryReduction.Resolve(history,argument,candidates[0],reference.Kind==TopologyKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge);
                }
                finally{foreach(var map in sourceMaps)map.Dispose();}
            },cancellationToken).ConfigureAwait(false);
        }
    }
}
