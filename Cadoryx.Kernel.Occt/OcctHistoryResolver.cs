using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : ITopologyHistoryResolver
{
    public async Task<HistoryResolution> TraceAsync(DocumentSnapshot snapshot,TopologyReference reference,FeatureId target,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();reference.Validate();
        HistoryResolution Fail(HistoryResolutionStatus status,string? code=null)=>new(status,null,0,"HISTORY."+(code??status.ToString().ToUpperInvariant()));
        if(reference.DocumentId!=snapshot.Id)return Fail(HistoryResolutionStatus.WrongContext);
        if(!snapshot.Features.TryGetValue(reference.FeatureId,out var source)||!snapshot.Features.ContainsKey(target))return Fail(HistoryResolutionStatus.Missing);
        if(source.OutputBodyId!=reference.OutputBodyId)return Fail(HistoryResolutionStatus.WrongContext);
        if(reference.Policy==TopologyRebindPolicy.ExactRevision&&reference.OriginRevision!=source.Result.Revision)return Fail(HistoryResolutionStatus.Stale);
        if(source.Recipe is not BoxRecipe box)return Fail(HistoryResolutionStatus.Unsupported);
        if(source.Inputs.IsDefault||!source.Inputs.IsEmpty)return Fail(HistoryResolutionStatus.WrongContext,"INVALID_SOURCE");
        var plan=TopologyHistoryPaths.Plan(snapshot,source.Id,target,cancellationToken);
        if(plan.Failure is {} failure)return failure;
        // Own exact assets across queue waits, including outputs consumed by later features.
        using var hold=new DocumentAssetLease(snapshot,assets);
        var traced=await Run(()=>
        {
            int completed=0,index=-1,mapCount=0;long slots=0,entries=0;
            var kind=reference.Kind==TopologyKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge;
            RepairSnapshot? previous=null;
            HistoryResolution Annotate(HistoryResolution result,FeatureId step)=>result with
            {PathLength=plan.Steps.Length,CompletedSteps=completed,StoppedAt=result.Status==HistoryResolutionStatus.Resolved?null:step};
            static ShapeKind NativeKind(HistoryShapeKind k)=>k switch{HistoryShapeKind.Face=>ShapeKind.Face,HistoryShapeKind.Edge=>ShapeKind.Edge,_=>ShapeKind.Vertex};
            RepairSnapshot Read(GeometryAssetRef geometry)
            {cancellationToken.ThrowIfCancellationRequested();using var shape=OcctGeometryBridge.ReadShape(geometry,assets);return RepairSnapshot.Create(shape);}
            try
            {
                foreach(var step in plan.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var feature=snapshot.Features[step.Feature];var history=feature.TopologyHistory;
                    HistoryResolution Stop(HistoryResolutionStatus status,string? code=null)=>Annotate(Fail(status,code),feature.Id);
                    if(feature.Recipe is not (LocalFeatureRecipe or BooleanRecipe)||history is null)return Stop(HistoryResolutionStatus.Unsupported,"MISSING_HISTORY");
                    if(history.ResultRevision!=feature.Result.Revision||history.ResultAsset!=feature.Result.AssetId)return Stop(HistoryResolutionStatus.Stale);
                    try{history.ValidateFor(feature);}
                    catch(CadValidationException){return Stop(HistoryResolutionStatus.Unsupported,"INVALID_HISTORY");}
                    bool adapterVerified=feature.Recipe is BooleanRecipe?history.AdapterVersion==BooleanHistoryAdapterVersion:
                        history.AdapterVersion==HistoryAdapterVersion||history.AdapterVersion==PreviousAxisHistoryAdapterVersion||history.AdapterVersion==LegacyHistoryAdapterVersion;
                    if(!adapterVerified)return Stop(HistoryResolutionStatus.Unsupported,"UNVERIFIED_ADAPTER");
                    entries+=history.Entries.Length;
                    mapCount+=1+history.ArgumentCount-(previous is null?0:1);
                    slots+=history.ResultCount;
                    for(int a=0;a<history.ArgumentCount;a++)
                        if(previous is null||a!=step.SourceArgument)slots+=history.GetSource(a).TopologyCount;
                    if(mapCount>TopologyHistoryPaths.MaxNativeMaps||slots>TopologyHistoryPaths.MaxTopologySlots||entries>TopologyHistoryPaths.MaxHistoryEntries)
                        return Stop(HistoryResolutionStatus.LimitExceeded,"EVIDENCE_BUDGET");
                    RepairSnapshot? resultMap=Read(feature.Result);
                    try
                    {
                        if(resultMap.Fingerprint!=history.ResultFingerprint||resultMap.Topology.Count!=history.ResultCount)return Stop(HistoryResolutionStatus.Stale);
                        var byArgument=history.Entries.ToLookup(e=>e.SourceArgument);
                        for(int a=0;a<history.ArgumentCount;a++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();var evidence=history.GetSource(a);
                            var upstream=snapshot.Features[feature.Inputs[a]];
                            if(upstream.Result.Revision!=evidence.Revision||upstream.Result.AssetId!=evidence.Asset)return Stop(HistoryResolutionStatus.Stale);
                            // Carry the SAME verified result map into the next input, never reinterpret an
                            // adjacency index. Other operands are loaded/checked one at a time and disposed.
                            bool carry=a==step.SourceArgument&&previous is not null;
                            var map=carry?previous!:Read(upstream.Result);
                            try
                            {
                                if(map.Fingerprint!=evidence.Fingerprint||map.Topology.Count!=evidence.TopologyCount)return Stop(HistoryResolutionStatus.Stale);
                                var covered=new HashSet<int>();
                                foreach(var entry in byArgument[a])
                                {
                                    cancellationToken.ThrowIfCancellationRequested();covered.Add(entry.SourceIndex);
                                    if(map.Topology[entry.SourceIndex].Kind!=NativeKind(entry.SourceKind)||
                                        entry.ResultIndex is {} i&&resultMap.Topology[i].Kind!=NativeKind(entry.ResultKind!.Value))
                                        return Stop(HistoryResolutionStatus.Unsupported,"KIND_MISMATCH");
                                }
                                if(map.Topology.Any(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex&&!covered.Contains(t.Selection.Index)))
                                    return Stop(HistoryResolutionStatus.Unsupported,"INCOMPLETE_COVERAGE");
                                if(a==step.SourceArgument&&previous is null)
                                {
                                    var candidates=FindSemantic(map,box,reference.Kind,reference.Boundary,reference.SecondBoundary);
                                    if(candidates.Length!=1)return Stop(candidates.Length==0?HistoryResolutionStatus.Missing:HistoryResolutionStatus.Ambiguous);
                                    index=candidates[0];
                                }
                            }
                            finally{if(!carry)map.Dispose();}
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var resolution=TopologyHistoryReduction.Resolve(history,step.SourceArgument,index,kind);
                        if(resolution.Status!=HistoryResolutionStatus.Resolved)return Annotate(resolution,feature.Id);
                        completed++;index=resolution.Target!.FullTopologyIndex;
                        if(completed==plan.Steps.Length)return Annotate(resolution,feature.Id);
                        previous?.Dispose();previous=resultMap;resultMap=null;
                    }
                    finally{resultMap?.Dispose();}
                }
                throw new InvalidOperationException("A successful history plan must contain a segment.");
            }
            finally{previous?.Dispose();}
        },cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();return traced;
    }
}
