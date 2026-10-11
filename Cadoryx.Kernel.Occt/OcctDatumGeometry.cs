using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IDatumGeometryResolver
{
    public async Task<DatumSourcePage> InspectDatumSourcesAsync(DocumentSnapshot document,FeatureId id,GeometryRevisionId revision,
        IAssetStore assets,int offset,int limit,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(offset is <0 or >100000||limit is <1 or >50)throw new CadValidationException("Invalid datum source page budget.");
        if(!document.Features.TryGetValue(id,out var feature)||feature.IsStale||feature.IsSuppressed||feature.Result.Revision!=revision)
            throw new CadValidationException("Exact datum source changed. Inspect its current feature revision.");
        using var hold=assets.Acquire(feature.Result.AssetId);
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
            if(map.Topology.Count>100001)throw new CadValidationException("Source exceeds the exact topology index budget.");
            var items=new List<DatumSourceSlot>();
            foreach(var entry in map.Topology.Skip(offset).Take(limit))
            {
                token.ThrowIfCancellationRequested();var index=entry.Selection.Index;
                if(entry.Kind is not (ShapeKind.Face or ShapeKind.Edge))
                {items.Add(new(index,entry.Kind.ToString(),null,null,"Only original faces and edges support analytic datums."));continue;}
                var selected=new ExactTopologySelection(document.Id,id,revision,feature.Result.AssetId,map.Fingerprint,index,
                    entry.Kind==ShapeKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge,HistoryAdapterVersion);
                try{items.Add(new(index,entry.Kind.ToString(),selected,ExtractDatumGeometry(selected,feature.Result,map,token),null));}
                catch(CadValidationException error){items.Add(new(index,entry.Kind.ToString(),selected,null,error.Message));}
            }
            return new DatumSourcePage(map.Topology.Count,items);
        },token).ConfigureAwait(false);
    }
    public async Task<ExactDatumGeometry> ResolveDatumGeometryAsync(DocumentSnapshot document,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)
    {
        selection.Validate();
        if(selection.DocumentId!=document.Id||!document.Features.TryGetValue(selection.FeatureId,out var feature)||feature.IsStale||feature.IsSuppressed||
            feature.Result.Revision!=selection.Revision||feature.Result.AssetId!=selection.Asset)
            throw new CadValidationException("Exact datum source changed. Select its current face or edge explicitly.");
        using var hold=assets.Acquire(feature.Result.AssetId);
        return await Run(()=>ExtractDatumGeometry(selection,feature.Result,assets,token),token).ConfigureAwait(false);
    }
    public async Task ConfirmDatumGeometryAsync(ExactDatumGeometry evidence,IAssetStore assets,CancellationToken token=default)
    {
        evidence.Validate();using var hold=assets.Acquire(evidence.Source.AssetId);
        await Run(()=>
        {
            var actual=ExtractDatumGeometry(evidence.Selection,evidence.Source,assets,token);
            if(actual.Kind!=evidence.Kind||(actual.Point-evidence.Point).Length>1e-8||
                (actual.Direction-evidence.Direction).Length>1e-10||Math.Abs(actual.Radius-evidence.Radius)>1e-8)
                throw new CadValidationException("Stored analytic datum values differ from the exact native asset.");
            return true;
        },token).ConfigureAwait(false);
    }
    private static ExactDatumGeometry ExtractDatumGeometry(ExactTopologySelection selection,GeometryAssetRef source,IAssetStore assets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();selection.Validate();source.Validate();
        if(selection.Asset!=source.AssetId||selection.Revision!=source.Revision||selection.AdapterVersion!=HistoryAdapterVersion)
            throw new CadValidationException("Datum selection and asset identity differ or the adapter is unsupported.");
        using var shape=OcctGeometryBridge.ReadShape(source,assets);using var map=RepairSnapshot.Create(shape);
        return ExtractDatumGeometry(selection,source,map,token);
    }
    private static ExactDatumGeometry ExtractDatumGeometry(ExactTopologySelection selection,GeometryAssetRef source,RepairSnapshot map,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();selection.Validate();source.Validate();
        if(selection.Asset!=source.AssetId||selection.Revision!=source.Revision||selection.AdapterVersion!=HistoryAdapterVersion)
            throw new CadValidationException("Datum selection and asset identity differ or the adapter is unsupported.");
        if(map.Fingerprint!=selection.Fingerprint||selection.FullTopologyIndex>=map.Topology.Count||
            map.Topology[selection.FullTopologyIndex].Kind!=(selection.Kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge))
            throw new CadValidationException("Exact datum selection no longer matches its original BRep.");
        using var subshape=map.CopySubshape(map.Select(selection.FullTopologyIndex));
        ExactDatumKind kind;Vector3d point,direction;double radius=0;
        if(selection.Kind==HistoryShapeKind.Face)
        {
            var surface=subshape.GetFaceSurfaceSnapshot();double u=Mid(surface.FirstUParameter,surface.LastUParameter),v=Mid(surface.FirstVParameter,surface.LastVParameter);
            if(surface.SurfaceType==SurfaceGeometryType.Plane)
            {var at=subshape.EvaluateFace(u,v);point=P(at.Point);direction=P(at.Normal).Normalized();kind=ExactDatumKind.Plane;}
            else if(surface.SurfaceType is SurfaceGeometryType.Cylinder or SurfaceGeometryType.Cone)
            {
                double uspan=surface.LastUParameter-surface.FirstUParameter,vspan=surface.LastVParameter-surface.FirstVParameter;
                if(uspan<0.1||vspan<1e-7)throw new CadValidationException("Analytic surface has a degenerate angular or axial domain.");
                (Vector3d Center,Vector3d Axis,double Radius) Section(double at)=>Circle(
                    P(subshape.EvaluateFace(surface.FirstUParameter+uspan*.2,at).Point),
                    P(subshape.EvaluateFace(surface.FirstUParameter+uspan*.5,at).Point),
                    P(subshape.EvaluateFace(surface.FirstUParameter+uspan*.8,at).Point));
                var first=Section(surface.FirstVParameter+vspan*.25);var second=Section(surface.FirstVParameter+vspan*.75);
                if((second.Center-first.Center).Length<1e-7)throw new CadValidationException("Analytic surface has no unique axial direction.");
                direction=(second.Center-first.Center).Normalized();point=first.Center;radius=first.Radius;
                if(Math.Abs(Math.Abs(direction.Dot(first.Axis))-1)>1e-8||Math.Abs(Math.Abs(direction.Dot(second.Axis))-1)>1e-8)
                    throw new CadValidationException("Analytic cross-sections are inconsistent with their native surface type.");
                kind=surface.SurfaceType==SurfaceGeometryType.Cylinder?ExactDatumKind.Cylinder:ExactDatumKind.Cone;
            }
            else throw new CadValidationException("Select an exact analytic plane, cylinder, cone or circular edge.");
        }
        else
        {
            var curve=subshape.GetEdgeCurveSnapshot();_=Mid(curve.FirstParameter,curve.LastParameter);double span=curve.LastParameter-curve.FirstParameter;
            if(curve.CurveType!=CurveGeometryType.Circle||span<.1)throw new CadValidationException("The selected edge is not a nondegenerate analytic circle.");
            (point,direction,radius)=Circle(P(subshape.EvaluateEdge(curve.FirstParameter+span*.2).Point),P(subshape.EvaluateEdge(curve.FirstParameter+span*.5).Point),P(subshape.EvaluateEdge(curve.FirstParameter+span*.8).Point));kind=ExactDatumKind.Circle;
        }
        token.ThrowIfCancellationRequested();var result=new ExactDatumGeometry(selection,source,kind,point,direction,radius);result.Validate();return result;
    }
}
