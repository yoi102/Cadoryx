using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    public const string LegacyHistoryAdapterVersion="OcctSharp 8.0.1-preview.26 / full-topology-brep-v1";
    public const string PreviousAxisHistoryAdapterVersion="OcctSharp 8.0.1-preview.26 / full-topology-brep-axis-v2";
    public const string HistoryAdapterVersion="OcctSharp 8.0.1-preview.28.cadoryx.h2b2.2 / full-topology-brep-axis-v2";

    private GeometryResult EvaluateLocal(LocalFeatureRecipe local,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(local.Source,assets);
        using var source=RepairSnapshot.Create(shape);
        var selected=local.Edges().Select(pair=>
        {
            var edges=FindSemantic(source,local.Box,TopologyKind.Edge,pair.First,pair.Second);
            if(edges.Length!=1)throw new CadValidationException("Local edge is missing or ambiguous. Reselect it.");
            return(pair.First,Index:edges[0]);
        }).ToArray();
        LocalFeatureResult Build()
        {
            if(local.Operation==Cadoryx.Db.LocalFeatureOperation.Fillet)
                return ContourFilletRecipe.Create(source,selected.Select(edge=>local.EndRadius is {} end
                    ?FilletContourProgram.FromLaw(source.Select(edge.Index),ScalarLawDefinition.Linear(new(0,1),local.Size,end))
                    :FilletContourProgram.Constant(source.Select(edge.Index),local.Size)).ToArray()).Build(source);
            var chamfers=selected.Select(edge=>
            {
                var faces=FindSemantic(source,local.Box,TopologyKind.Face,edge.First,null);
                if(faces.Length!=1)throw new CadValidationException("Chamfer support face is missing or ambiguous.");
                return local.SecondDistance is {} second
                    ?new ChamferContourProgram(source.Select(edge.Index),source.Select(faces[0]),ChamferDimensions.TwoDistances,local.Size,second)
                    :new ChamferContourProgram(source.Select(edge.Index),source.Select(faces[0]),ChamferDimensions.Symmetric,local.Size);
            }).ToArray();
            return ContourChamferRecipe.Create(source,chamfers).Build(source);
        }
        return FinishLocal(local.Source,source,Build(),assets,token);
    }

    private GeometryResult EvaluateHistoryFillet(HistoryFilletRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        using var source=RepairSnapshot.Create(shape);
        if(recipe.FullTopologyIndex>=source.Topology.Count||source.Topology[recipe.FullTopologyIndex].Kind!=ShapeKind.Edge)
            throw new CadValidationException("Bound edge is not present in the exact target BRep.");
        var edge=source.Select(recipe.FullTopologyIndex);
        var program=recipe.EndRadius is {} end
            ?FilletContourProgram.FromLaw(edge,ScalarLawDefinition.Linear(new(0,1),recipe.Radius,end))
            :FilletContourProgram.Constant(edge,recipe.Radius);
        return FinishLocal(recipe.Source,source,
            ContourFilletRecipe.Create(source,[program]).Build(source),assets,token);
    }

    private GeometryResult EvaluateHistoryChamfer(HistoryChamferRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        using var source=RepairSnapshot.Create(shape);
        if(recipe.FullTopologyIndex>=source.Topology.Count||source.Topology[recipe.FullTopologyIndex].Kind!=ShapeKind.Edge||
            recipe.SupportFaceIndex>=source.Topology.Count||source.Topology[recipe.SupportFaceIndex].Kind!=ShapeKind.Face)
            throw new CadValidationException("Bound chamfer edge or support face is missing.");
        var edge=source.Select(recipe.FullTopologyIndex);
        var face=source.Select(recipe.SupportFaceIndex);
        var program=recipe.SecondDistance is {} second
            ?new ChamferContourProgram(edge,face,ChamferDimensions.TwoDistances,recipe.Distance,second)
            :new ChamferContourProgram(edge,face,ChamferDimensions.Symmetric,recipe.Distance);
        return FinishLocal(recipe.Source,source,ContourChamferRecipe.Create(source,[program]).Build(source),assets,token);
    }

    private GeometryResult FinishLocal(GeometryAssetRef sourceGeometry,RepairSnapshot source,LocalFeatureResult operation,IAssetStore assets,CancellationToken token)
    {
        using(operation)
        {
        token.ThrowIfCancellationRequested();
        using var stored=OcctGeometryBridge.StoreShape(operation.RequireShape(),assets);
        using var reopened=OcctGeometryBridge.ReadShape(stored.Geometry,assets);
        using var result=RepairSnapshot.Create(reopened);
        using var live=RepairSnapshot.Create(operation.RequireShape());
        GeometryResult WithoutHistory(string reason)
        {
            token.ThrowIfCancellationRequested();
            return new(stored.Geometry,assets.Acquire(stored.Geometry.AssetId),[new("HISTORY.UNSUPPORTED",reason)]);
        }
        // BRep may normalize representation (notably chamfer p-curves). Verify each
        // full-map slot after normalization; no nearest match or index fallback.
        if(!live.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)).SequenceEqual(result.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex))))
            return WithoutHistory("BRep roundtrip changed the topology map; automatic history propagation is disabled.");
        foreach(var t in live.Topology)
        {
            token.ThrowIfCancellationRequested();
            using var a=live.CopySubshape(t.Selection);using var b=result.CopySubshape(result.Select(t.Selection.Index));
            if(!BrepDirectionRoundtrip.Matches(CanonicalBrep(a),CanonicalBrep(b)))return WithoutHistory("A topology locator could not be verified after BRep axis normalization; automatic history propagation is disabled.");
        }
        if(operation.Diagnostics.HasComposedHistory||!operation.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution))
            throw new CadValidationException("Unsupported local history provenance.");
        var entries=ImmutableArray.CreateBuilder<TopologyHistoryEntry>();
        foreach(var h in operation.History)
        {
            TopologyEvolution? evolution=h.Kind switch
            {
                LocalFeatureHistoryKind.Unchanged=>TopologyEvolution.Unchanged,LocalFeatureHistoryKind.Modified=>TopologyEvolution.Modified,
                LocalFeatureHistoryKind.Generated=>TopologyEvolution.Generated,LocalFeatureHistoryKind.Deleted=>TopologyEvolution.Deleted,
                LocalFeatureHistoryKind.Unmapped=>TopologyEvolution.Unmapped,_=>null
            };
            if(evolution is null)continue;
            if(h.Source is not {} s||s.PlanId!=operation.PlanId||s.ArgumentIndex!=0||s.TopologyIndex<0||s.TopologyIndex>=source.Topology.Count||source.Topology[s.TopologyIndex].Kind!=s.Kind)
                throw new CadValidationException("Invalid native history source.");
            var target=h.ResultTopologyIndex;
            if(target is {} index&&(index<0||index>=result.Topology.Count))throw new CadValidationException("Invalid native history result.");
            // Native generated shapes that do not reach the final map cannot be followed.
            var relation=target is null&&evolution is TopologyEvolution.Generated or TopologyEvolution.Modified or TopologyEvolution.Unchanged?TopologyEvolution.Unmapped:evolution.Value;
            entries.Add(new(s.TopologyIndex,HistoryKind(s.Kind),relation,target,target is {} i?HistoryKind(result.Topology[i].Kind):null));
        }
        foreach(var t in source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
            if(!entries.Any(e=>e.SourceIndex==t.Selection.Index))entries.Add(new(t.Selection.Index,HistoryKind(t.Kind),TopologyEvolution.Unmapped,null,null));
        var history=new TopologyHistory(sourceGeometry.Revision,sourceGeometry.AssetId,stored.Geometry.Revision,stored.Geometry.AssetId,
            HistoryAdapterVersion,source.Fingerprint,result.Fingerprint,source.Topology.Count,result.Topology.Count,entries.Distinct().ToImmutableArray());
        history.Validate();token.ThrowIfCancellationRequested();
        return new(stored.Geometry,assets.Acquire(stored.Geometry.AssetId)){TopologyHistory=history};
        }
    }

    private static string CanonicalBrep(Shape shape)
    {
        using var files=new KernelFiles();var path=files.PathFor("normalized.brep");ShapeExchange.WriteBrep(shape,path);
        using var normalized=ShapeExchange.ReadBrep(path);ShapeExchange.WriteBrep(normalized,path);return File.ReadAllText(path);
    }
    private static HistoryShapeKind HistoryKind(ShapeKind kind)=>kind switch
    {ShapeKind.Face=>HistoryShapeKind.Face,ShapeKind.Edge=>HistoryShapeKind.Edge,ShapeKind.Vertex=>HistoryShapeKind.Vertex,_=>throw new CadValidationException("Unsupported history subshape kind.")};
    private static int[] FindSemantic(RepairSnapshot snapshot,BoxRecipe box,TopologyKind kind,BoxBoundary first,BoxBoundary? second)
        =>snapshot.Topology.Where(t=>
        {
            if(t.Kind!=(kind==TopologyKind.Face?ShapeKind.Face:ShapeKind.Edge))return false;
            using var subshape=snapshot.CopySubshape(t.Selection);return BoxTopology.Matches(subshape,box,kind,first,second);
        }).Select(t=>t.Selection.Index).ToArray();
}
