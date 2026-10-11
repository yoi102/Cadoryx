using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IDrawingTopologyResolver,IDrawingTopologyHistoryResolver
{
    public async Task<DrawingTopologyPage> InspectDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,
        BodyId bodyId,GeometryRevisionId revision,IAssetStore assets,int offset,int limit,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(offset is <0 or >100000||limit is <1 or >50)throw new CadValidationException("Invalid drawing source page budget.");
        var occurrence=document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(path));
        if(occurrence is null||document.Definitions[occurrence.DefinitionId] is not PartDefinition part||!part.Bodies.Contains(bodyId)||
            !document.Bodies.TryGetValue(bodyId,out var body)||body.Geometry.Revision!=revision||body.Geometry.Kind==BodyKind.Empty||
            body.Producer is {} producer&&(document.Features[producer].IsStale||document.Features[producer].IsSuppressed))
            throw new CadValidationException("Exact drawing source is missing, foreign or changed.");
        using var hold=assets.Acquire(body.Geometry.AssetId);
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
            if(map.Topology.Count>100001)throw new CadValidationException("Drawing source exceeds the topology index budget.");
            var items=new List<DrawingTopologySlot>();
            foreach(var entry in map.Topology.Skip(offset).Take(limit))
            {
                token.ThrowIfCancellationRequested();var index=entry.Selection.Index;
                if(entry.Kind is not (ShapeKind.Face or ShapeKind.Edge))
                {items.Add(new(index,entry.Kind.ToString(),null,"Select an original face or edge."));continue;}
                try{items.Add(new(index,entry.Kind.ToString(),BuildDrawingAttachment(document.Id,path,part.Id,body.Id,body.Producer,body.Geometry,map,index,token),null));}
                catch(Exception e) when(e is CadValidationException or OcctException){items.Add(new(index,entry.Kind.ToString(),null,e.Message));}
            }
            return new DrawingTopologyPage(map.Topology.Count,items);
        },token).ConfigureAwait(false);
    }
    public async Task<DrawingTopologyReference> ResolveDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,
        BodyId bodyId,int fullTopologyIndex,string fingerprint,IAssetStore assets,CancellationToken token=default)
    {
        if(!document.Bodies.TryGetValue(bodyId,out var sourceBody))throw new CadValidationException("Drawing attachment body is missing.");
        using var hold=assets.Acquire(sourceBody.Geometry.AssetId);
        return await Run(()=>
    {
        var occurrence=document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(path))??
            throw new CadValidationException("Drawing attachment occurrence is missing.");
        if(document.Definitions[occurrence.DefinitionId] is not PartDefinition part||!part.Bodies.Contains(bodyId)||
           !document.Bodies.TryGetValue(bodyId,out var body)||body.Producer is {} producer&&document.Features[producer].IsStale)
            throw new CadValidationException("Drawing attachment body is missing or stale.");
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
        if(map.Fingerprint!=fingerprint||fullTopologyIndex<0||fullTopologyIndex>=map.Topology.Count)
            throw new CadValidationException("The original BRep changed; explicitly reselect the attachment.");
        var kind=map.Topology[fullTopologyIndex].Kind;
        if(kind is not (ShapeKind.Face or ShapeKind.Edge))throw new CadValidationException("Select an exact face or edge.");
        return BuildDrawingAttachment(document.Id,path,occurrence.DefinitionId,body.Id,body.Producer,body.Geometry,map,fullTopologyIndex,token);
        },token).ConfigureAwait(false);
    }

    public async Task<DrawingAttachmentHistoryResolution> ResolveDrawingHistoryAsync(DocumentSnapshot document,
        DrawingTopologyReference author,DrawingSource explicitTarget,IAssetStore assets,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();author.Validate(document.Id);
        if(author.HistoryOrigin is not null)throw new CadValidationException("Trace from the immutable authored attachment, not a derived slot.");
        DrawingAttachmentHistoryResolution Fail(HistoryResolutionStatus status,string code)=>new(author,null,HistoryFailure(status,code));
        if(!explicitTarget.Path.Equals(author.Path))return Fail(HistoryResolutionStatus.WrongContext,"ATTACHMENT_OCCURRENCE");
        var occurrence=document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(author.Path));
        if(occurrence?.DefinitionId!=author.DefinitionId)return Fail(HistoryResolutionStatus.WrongContext,"ATTACHMENT_DEFINITION");
        if(author.FeatureId is not {} sourceId||explicitTarget.Feature is not {} targetId)
            return Fail(HistoryResolutionStatus.Unsupported,"ATTACHMENT_FEATURE_REQUIRED");
        if(!document.Features.TryGetValue(sourceId,out var source)||!document.Features.TryGetValue(targetId,out var target))
            return Fail(HistoryResolutionStatus.Missing,"ATTACHMENT_FEATURE_MISSING");
        if(source.PartId!=author.DefinitionId||source.OutputBodyId!=author.BodyId||target.PartId!=author.DefinitionId||target.OutputBodyId!=explicitTarget.Body)
            return Fail(HistoryResolutionStatus.WrongContext,"ATTACHMENT_FEATURE_CONTEXT");
        if(source.IsStale||source.Result.Revision!=author.Revision||source.Result.AssetId!=author.Asset)
            return Fail(HistoryResolutionStatus.Stale,"ATTACHMENT_ORIGIN_CHANGED");
        if(target.IsStale||target.Result.Revision!=explicitTarget.Revision||target.Result.AssetId!=explicitTarget.Asset||
           !document.Bodies.TryGetValue(explicitTarget.Body,out var targetBody)||targetBody.Producer!=targetId||targetBody.Geometry!=target.Result||
           document.Definitions[author.DefinitionId] is not PartDefinition part||!part.Bodies.Contains(explicitTarget.Body)||
           occurrence.WorldTransform!=explicitTarget.WorldTransform)
            return Fail(HistoryResolutionStatus.Stale,"ATTACHMENT_TARGET_CHANGED");
        using var hold=new DocumentAssetLease(document,assets);
        // The consumed original body may no longer exist. Its immutable producer result must
        // still reproduce every original anchor field before the stored slot can enter a chain.
        var original=await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(source.Result,assets);using var map=RepairSnapshot.Create(shape);
            if(map.Fingerprint!=author.Fingerprint||author.FullTopologyIndex>=map.Topology.Count||
               map.Topology[author.FullTopologyIndex].Kind!=NativeHistoryKind(author.Kind==TopologyKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge))return false;
            try{return BuildDrawingAttachment(document.Id,author.Path,author.DefinitionId,author.BodyId,author.FeatureId,source.Result,map,author.FullTopologyIndex,token)==author;}
            catch(CadValidationException){return false;}
        },token).ConfigureAwait(false);
        if(!original)return Fail(HistoryResolutionStatus.Stale,"ATTACHMENT_ORIGIN_EVIDENCE");
        var selected=new ExactTopologySelection(document.Id,sourceId,author.Revision,author.Asset,author.Fingerprint,
            author.FullTopologyIndex,author.Kind==TopologyKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge,HistoryAdapterVersion);
        var history=await TraceExactAsync(document,selected,targetId,assets,token).ConfigureAwait(false);
        if(history.Status!=HistoryResolutionStatus.Resolved||history.Target is not {} resolved)return new(author,null,history);
        var current=await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(target.Result,assets);using var map=RepairSnapshot.Create(shape);
            return BuildDrawingAttachment(document.Id,author.Path,author.DefinitionId,explicitTarget.Body,targetId,target.Result,map,resolved.FullTopologyIndex,token);
        },token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();return new(author,current,history);
    }

    private async Task ConfirmDrawingAttachmentAsync(DocumentSnapshot document,DrawingTopologyReference reference,IAssetStore assets,CancellationToken token)
    {
        if(reference.HistoryOrigin is {} author)
        {
            var occurrence=document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(reference.Path))??
                throw new CadValidationException("Attachment occurrence is missing.");
            var target=new DrawingSource(reference.Path,reference.BodyId,reference.FeatureId,reference.Revision,reference.Asset,occurrence.WorldTransform);
            var resolved=await ResolveDrawingHistoryAsync(document,author,target,assets,token).ConfigureAwait(false);
            if(resolved.Current!=reference with{HistoryOrigin=null}||resolved.History.Status!=HistoryResolutionStatus.Resolved)
                throw new CadValidationException("Manufacturing attachment no longer has one proven original history chain: "+resolved.History.Diagnostic);
        }
        else
        {
            var actual=await ResolveDrawingTopologyAsync(document,reference.Path,reference.BodyId,reference.FullTopologyIndex,reference.Fingerprint,assets,token).ConfigureAwait(false);
            if(actual!=reference)throw new CadValidationException("Attachment differs from its original BRep evidence.");
        }
    }

    private static DrawingTopologyReference BuildDrawingAttachment(DocumentId document,OccurrencePath path,DefinitionId definition,
        BodyId body,FeatureId? feature,GeometryAssetRef geometryAsset,RepairSnapshot map,int fullTopologyIndex,CancellationToken token)
    {
        var kind=map.Topology[fullTopologyIndex].Kind;
        if(kind is not (ShapeKind.Face or ShapeKind.Edge))throw new CadValidationException("Select an exact face or edge.");
        using var selected=map.CopySubshape(map.Select(fullTopologyIndex));
        var geometry=kind==ShapeKind.Face?selected.GetFaceSurfaceSnapshot().SurfaceType switch
        {
            SurfaceGeometryType.Plane=>DrawingAttachmentGeometry.PlaneFace,
            SurfaceGeometryType.Cylinder=>DrawingAttachmentGeometry.CylinderFace,
            _=>DrawingAttachmentGeometry.GeneralFace
        }:selected.GetEdgeCurveSnapshot().CurveType switch
        {
            CurveGeometryType.Line=>DrawingAttachmentGeometry.LineEdge,
            CurveGeometryType.Circle=>DrawingAttachmentGeometry.CircleEdge,
            _=>DrawingAttachmentGeometry.GeneralEdge
        };
        var edges=kind==ShapeKind.Face?selected.GetSubShapes(ShapeKind.Edge):new[]{selected};
        try
        {
            for(int index=0;index<edges.Length;index++)
            {
                token.ThrowIfCancellationRequested();
                double parameter;Vector3d anchor;
                try
                {
                    var range=edges[index].GetEdgeCurveSnapshot();
                    if(!double.IsFinite(range.FirstParameter)||!double.IsFinite(range.LastParameter)||range.LastParameter<=range.FirstParameter)continue;
                    parameter=range.FirstParameter+(range.LastParameter-range.FirstParameter)/2;
                    anchor=P(edges[index].EvaluateEdge(parameter).Point);anchor.Validate();
                }
                catch(OcctException){continue;}
                var result=new DrawingTopologyReference(path,definition,body,feature,
                    geometryAsset.Revision,geometryAsset.AssetId,map.Fingerprint,fullTopologyIndex,
                    kind==ShapeKind.Face?TopologyKind.Face:TopologyKind.Edge,geometry,kind==ShapeKind.Face?index:-1,parameter,anchor);
                result.Validate(document);return result;
            }
            throw new CadValidationException("Selected topology has no finite nondegenerate boundary attachment.");
        }
        finally{if(kind==ShapeKind.Face)foreach(var edge in edges)edge.Dispose();}
    }
}
