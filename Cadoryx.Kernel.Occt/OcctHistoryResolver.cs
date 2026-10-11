using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : ITopologyHistoryResolver,IExactTopologyResolver,IExactTopologyHistoryResolver,IRepetitionTopologyHistoryResolver
{
    public Task ConfirmAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,IAssetStore assets,
        CancellationToken cancellationToken=default)=>Run(()=>
    {
        selection.Validate();cancellationToken.ThrowIfCancellationRequested();
        if(selection.DocumentId!=snapshot.Id||!snapshot.Features.TryGetValue(selection.FeatureId,out var feature)||
            feature.IsStale||feature.Result.Revision!=selection.Revision||feature.Result.AssetId!=selection.Asset||
            selection.AdapterVersion!=HistoryAdapterVersion)
            throw new CadValidationException("Exact topology selection is stale; pick it again.");
        using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);
        using var map=RepairSnapshot.Create(shape);
        if(map.Fingerprint!=selection.Fingerprint||selection.FullTopologyIndex>=map.Topology.Count||
            map.Topology[selection.FullTopologyIndex].Kind!=(selection.Kind==HistoryShapeKind.Edge?ShapeKind.Edge:ShapeKind.Face))
            throw new CadValidationException("Exact topology selection no longer matches this BRep.");
        return true;
    },cancellationToken);

    public Task<HistoryResolution> TraceAsync(DocumentSnapshot snapshot,TopologyReference reference,FeatureId target,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();reference.Validate();
        Task<HistoryResolution> Fail(HistoryResolutionStatus status,string? code=null)=>Task.FromResult(HistoryFailure(status,code));
        if(reference.DocumentId!=snapshot.Id)return Fail(HistoryResolutionStatus.WrongContext);
        if(!snapshot.Features.TryGetValue(reference.FeatureId,out var source)||!snapshot.Features.ContainsKey(target))return Fail(HistoryResolutionStatus.Missing);
        if(source.OutputBodyId!=reference.OutputBodyId)return Fail(HistoryResolutionStatus.WrongContext);
        if(source.IsStale||reference.Policy==TopologyRebindPolicy.ExactRevision&&reference.OriginRevision!=source.Result.Revision)return Fail(HistoryResolutionStatus.Stale);
        if(source.Recipe is not BoxRecipe box)return Fail(HistoryResolutionStatus.Unsupported);
        if(source.Inputs.IsDefault||!source.Inputs.IsEmpty)return Fail(HistoryResolutionStatus.WrongContext,"INVALID_SOURCE");
        var kind=reference.Kind==TopologyKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge;
        return TraceCoreAsync(snapshot,source.Id,target,kind,map=>
        {
            var candidates=FindSemantic(map,box,reference.Kind,reference.Boundary,reference.SecondBoundary);
            return candidates.Length==1?(candidates[0],null):(-1,HistoryFailure(candidates.Length==0?HistoryResolutionStatus.Missing:HistoryResolutionStatus.Ambiguous));
        },assets,cancellationToken);
    }

    public Task<HistoryResolution> TraceExactAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,FeatureId target,
        IAssetStore assets,CancellationToken cancellationToken=default)
        =>TraceExactCoreAsync(snapshot,selection,target,assets,cancellationToken,null);

    public Task<HistoryResolution> TraceCopyAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,FeatureId target,
        FeatureId repetition,Guid copyId,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(copyId==Guid.Empty||!snapshot.Features.TryGetValue(repetition,out var feature)||feature.IsStale||
            feature.Recipe is not RepetitionRecipe recipe||!recipe.CopyIds.Contains(copyId))
            return Task.FromResult(HistoryFailure(HistoryResolutionStatus.WrongContext,"COPY_IDENTITY"));
        return TraceExactCoreAsync(snapshot,selection,target,assets,cancellationToken,new(repetition,copyId));
    }

    private sealed record RepetitionPath(FeatureId Feature,Guid CopyId);
    private Task<HistoryResolution> TraceExactCoreAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,FeatureId target,
        IAssetStore assets,CancellationToken cancellationToken,RepetitionPath? copyPath)
    {
        cancellationToken.ThrowIfCancellationRequested();selection.Validate();
        Task<HistoryResolution> Fail(HistoryResolutionStatus status,string? code=null)=>Task.FromResult(HistoryFailure(status,code));
        if(selection.DocumentId!=snapshot.Id)return Fail(HistoryResolutionStatus.WrongContext);
        if(!snapshot.Features.TryGetValue(selection.FeatureId,out var source)||!snapshot.Features.ContainsKey(target))return Fail(HistoryResolutionStatus.Missing);
        if(source.IsStale||source.Result.Revision!=selection.Revision||source.Result.AssetId!=selection.Asset)return Fail(HistoryResolutionStatus.Stale);
        if(selection.AdapterVersion!=HistoryAdapterVersion)return Fail(HistoryResolutionStatus.Unsupported,"UNVERIFIED_SOURCE_ADAPTER");
        return TraceCoreAsync(snapshot,source.Id,target,selection.Kind,map=>
        {
            if(map.Fingerprint!=selection.Fingerprint)return (-1,HistoryFailure(HistoryResolutionStatus.Stale));
            if(selection.FullTopologyIndex>=map.Topology.Count||
               map.Topology[selection.FullTopologyIndex].Kind!=NativeHistoryKind(selection.Kind))
                return (-1,HistoryFailure(HistoryResolutionStatus.WrongContext,"SOURCE_KIND_MISMATCH"));
            return (selection.FullTopologyIndex,null);
        },assets,cancellationToken,requireReplay:true,copyPath);
    }

    private static HistoryResolution HistoryFailure(HistoryResolutionStatus status,string? code=null)=>
        new(status,null,0,"HISTORY."+(code??status.ToString().ToUpperInvariant()));
    private static ShapeKind NativeHistoryKind(HistoryShapeKind kind)=>kind switch
        {HistoryShapeKind.Face=>ShapeKind.Face,HistoryShapeKind.Edge=>ShapeKind.Edge,_=>ShapeKind.Vertex};

    private async Task<HistoryResolution> TraceCoreAsync(DocumentSnapshot snapshot,FeatureId sourceId,FeatureId target,
        HistoryShapeKind kind,Func<RepairSnapshot,(int Index,HistoryResolution? Failure)> locate,
        IAssetStore assets,CancellationToken cancellationToken,bool requireReplay=false,RepetitionPath? copyPath=null)
    {
        var source=snapshot.Features[sourceId];
        var plan=TopologyHistoryPaths.Plan(snapshot,source.Id,target,cancellationToken);
        if(plan.Failure is {} failure)return failure;
        if(copyPath is not null&&plan.Steps.Count(s=>s.Feature==copyPath.Feature)!=1)
            return HistoryFailure(HistoryResolutionStatus.WrongContext,"COPY_NOT_ON_PATH");
        // Own exact assets across queue waits, including outputs consumed by later features.
        using var hold=new DocumentAssetLease(snapshot,assets);
        var traced=await Run(()=>
        {
            int completed=0,index=-1,mapCount=0;long slots=0,entries=0;
            RepairSnapshot? previous=null;
            HistoryResolution Annotate(HistoryResolution result,FeatureId step)=>result with
            {PathLength=plan.Steps.Length,CompletedSteps=completed,StoppedAt=result.Status==HistoryResolutionStatus.Resolved?null:step};
            RepairSnapshot Read(GeometryAssetRef geometry)
            {cancellationToken.ThrowIfCancellationRequested();using var shape=OcctGeometryBridge.ReadShape(geometry,assets);return RepairSnapshot.Create(shape);}
            try
            {
                foreach(var step in plan.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var feature=snapshot.Features[step.Feature];var history=feature.TopologyHistory;
                    HistoryResolution Stop(HistoryResolutionStatus status,string? code=null)=>Annotate(HistoryFailure(status,code),feature.Id);
                    if(feature.IsStale)return Stop(HistoryResolutionStatus.Stale);
                    if(feature.Recipe is not (LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe or ContourLocalRecipe or PlanarPushPullRecipe or BooleanRecipe or DraftRecipe or HoleRecipe or HolePatternRecipe {Layout.IncludeSource:true} or RepetitionRecipe)||history is null)return Stop(HistoryResolutionStatus.Unsupported,"MISSING_HISTORY");
                    if(history.ResultRevision!=feature.Result.Revision||history.ResultAsset!=feature.Result.AssetId)return Stop(HistoryResolutionStatus.Stale);
                    try{history.ValidateFor(feature);}
                    catch(CadValidationException){return Stop(HistoryResolutionStatus.Unsupported,"INVALID_HISTORY");}
                    bool composed=history.SchemaVersion==3;
                    bool multicopy=history.SchemaVersion==4;
                    bool adapterVerified=multicopy?IsRepetitionHistoryAdapter(history.AdapterVersion):
                        composed?history.AdapterVersion==ComposedHoleHistoryAdapterVersion:
                        feature.Recipe is BooleanRecipe?history.AdapterVersion==BooleanHistoryAdapterVersion:
                        history.AdapterVersion==HistoryAdapterVersion||history.AdapterVersion==PreviousAxisHistoryAdapterVersion||history.AdapterVersion==LegacyHistoryAdapterVersion;
                    if(!adapterVerified)return Stop(HistoryResolutionStatus.Unsupported,"UNVERIFIED_ADAPTER");
                    entries+=history.Entries.Length;
                    mapCount+=1+history.ArgumentCount-(previous is null?0:1);
                    // The legacy semantic entrance must also replay a composed map.
                    // A final flattened map alone cannot prove its internal stages.
                    bool replayRequired=requireReplay||composed||multicopy;
                    if(replayRequired)mapCount+=multicopy?4*((RepetitionRecipe)feature.Recipe).Count:
                        composed?8*DrillingStageCount(feature.Recipe):history.ArgumentCount+2;
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
                            if(upstream.IsStale||upstream.Result.Revision!=evidence.Revision||upstream.Result.AssetId!=evidence.Asset)return Stop(HistoryResolutionStatus.Stale);
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
                                    if(map.Topology[entry.SourceIndex].Kind!=NativeHistoryKind(entry.SourceKind)||
                                        entry.ResultIndex is {} i&&resultMap.Topology[i].Kind!=NativeHistoryKind(entry.ResultKind!.Value))
                                        return Stop(HistoryResolutionStatus.Unsupported,"KIND_MISMATCH");
                                }
                                if(map.Topology.Any(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex&&!covered.Contains(t.Selection.Index)))
                                    return Stop(HistoryResolutionStatus.Unsupported,"INCOMPLETE_COVERAGE");
                                if(a==step.SourceArgument&&previous is null)
                                {
                                    var located=locate(map);
                                    if(located.Failure is {} problem)return Annotate(problem,feature.Id);
                                    index=located.Index;
                                }
                            }
                            finally{if(!carry)map.Dispose();}
                        }
                        if(replayRequired)
                        {
                            try
                            {
                                if(!ConfirmHistoryReplay(feature.Recipe,history,assets,cancellationToken))
                                    return Stop(HistoryResolutionStatus.Unsupported,"NATIVE_REPLAY_MISMATCH");
                            }
                            catch(Exception error) when(error is CadValidationException or OcctException or InvalidOperationException)
                            {return Stop(HistoryResolutionStatus.Unsupported,"NATIVE_REPLAY_FAILED");}
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var resolution=copyPath?.Feature==feature.Id
                            ?TopologyHistoryReduction.ResolveCopy(history,copyPath.CopyId,index,kind)
                            :TopologyHistoryReduction.Resolve(history,step.SourceArgument,index,kind);
                        if(resolution.Status!=HistoryResolutionStatus.Resolved)return Annotate(resolution,feature.Id);
                        completed++;index=resolution.Target!.FullTopologyIndex;
                        if(completed==plan.Steps.Length)
                        {
                            if(copyPath is not null)resolution=resolution with{VerifiedSelection=new(snapshot.Id,feature.Id,feature.Result.Revision,
                                feature.Result.AssetId,resultMap.Fingerprint,index,kind,HistoryAdapterVersion)};
                            return Annotate(resolution,feature.Id);
                        }
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

    private bool ConfirmHistoryReplay(GeometryRecipe recipe,TopologyHistory expected,IAssetStore assets,CancellationToken token)
    {
        // Re-run the declared native operation on the exact saved operands. Fingerprints and
        // slot coverage alone cannot prove a stored map whose same-kind targets were swapped.
        using var replay=recipe switch
        {
            LocalFeatureRecipe local=>EvaluateLocal(local,assets,token),
            HistoryFilletRecipe fillet=>EvaluateHistoryFillet(fillet,assets,token),
            HistoryChamferRecipe chamfer=>EvaluateHistoryChamfer(chamfer,assets,token),
            ContourLocalRecipe contour=>EvaluateContourLocal(contour,assets,token),
            PlanarPushPullRecipe pushPull=>EvaluatePlanarPushPull(pushPull,assets,token),
            HoleRecipe hole=>EvaluateHole(hole,assets,token),
            HolePatternRecipe {Layout.IncludeSource:true} pattern=>EvaluateHolePattern(pattern,assets,token),
            DraftRecipe draft=>EvaluateDraft(draft,assets,token),
            BooleanRecipe boolean=>EvaluateBoolean(boolean,assets,token),
            RepetitionRecipe repetition=>EvaluateRepetition(repetition,assets,token),
            _=>throw new CadValidationException("Native history replay is unavailable for this recipe.")
        };
        return SameNativeHistoryReplay(replay.TopologyHistory,expected);
    }
    private static bool SameNativeHistoryReplay(TopologyHistory? actual,TopologyHistory expected)=>
        actual is not null&&actual.SchemaVersion==expected.SchemaVersion&&
            (actual.AdapterVersion==expected.AdapterVersion||expected.SchemaVersion==4&&IsRepetitionHistoryAdapter(actual.AdapterVersion)&&IsRepetitionHistoryAdapter(expected.AdapterVersion))&&
            actual.SourceRevision==expected.SourceRevision&&actual.SourceAsset==expected.SourceAsset&&
            actual.SourceFingerprint==expected.SourceFingerprint&&actual.ResultFingerprint==expected.ResultFingerprint&&
            actual.SourceCount==expected.SourceCount&&actual.ResultCount==expected.ResultCount&&
            actual.AdditionalSources.SequenceEqual(expected.AdditionalSources)&&
            actual.Entries.Length==expected.Entries.Length&&actual.Entries.ToHashSet().SetEquals(expected.Entries);
    private static int DrillingStageCount(GeometryRecipe recipe)=>recipe switch
    {
        HoleRecipe hole=>hole.Head==HoleHead.Plain?1:2,
        HolePatternRecipe pattern=>(pattern.Layout.Count-1)*(pattern.Template.Head==HoleHead.Plain?1:2),
        _=>1
    };
}
