using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    public const string BooleanHistoryAdapterVersion="OcctSharp 8.0.1-preview.28.cadoryx.h2b2.2 / boolean-full-topology-brep-axis-v1";

    private GeometryResult EvaluateBoolean(BooleanRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        var sources=new List<RepairSnapshot>();
        try
        {
            foreach(var input in recipe.Inputs)
            {
                token.ThrowIfCancellationRequested();
                using var shape=OcctGeometryBridge.ReadShape(input,assets);
                sources.Add(RepairSnapshot.Create(shape));
            }
            using var operation=BooleanHistoryModeling.Build((TopologyBooleanOperation)recipe.Operation,sources);
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
            if(!VerifyHistoryRoundtrip(live,result,token))
                return WithoutHistory("Boolean BRep roundtrip could not preserve verified topology locators.");
            if(sources.Any(s=>!s.Topology.Any(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex)))
                return WithoutHistory("An empty Boolean input has no trackable source topology.");
            if(operation.Diagnostics.HasComposedHistory||!operation.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution))
                return WithoutHistory("Boolean source/result copy correspondence could not be verified.");
            var entries=ImmutableArray.CreateBuilder<TopologyHistoryEntry>();
            foreach(var h in operation.History)
            {
                token.ThrowIfCancellationRequested();
                if(h.Source is not {} s||s.PlanId!=operation.PlanId||s.ArgumentIndex<0||s.ArgumentIndex>=sources.Count||
                    s.TopologyIndex<0||s.TopologyIndex>=sources[s.ArgumentIndex].Topology.Count||
                    sources[s.ArgumentIndex].Topology[s.TopologyIndex].Kind!=s.Kind)
                    throw new CadValidationException("Invalid native Boolean source locator.");
                var evolution=h.Kind switch
                {
                    LocalFeatureHistoryKind.Unchanged=>TopologyEvolution.Unchanged,
                    LocalFeatureHistoryKind.Modified=>TopologyEvolution.Modified,
                    LocalFeatureHistoryKind.Generated=>TopologyEvolution.Generated,
                    LocalFeatureHistoryKind.Deleted=>TopologyEvolution.Deleted,
                    LocalFeatureHistoryKind.Unmapped=>TopologyEvolution.Unmapped,
                    _=>throw new CadValidationException("Unknown native Boolean relation.")
                };
                var target=h.ResultTopologyIndex;
                if(target is {} index&&(index<0||index>=result.Topology.Count))throw new CadValidationException("Invalid native Boolean target locator.");
                if(target is null&&evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified or TopologyEvolution.Generated)
                    evolution=TopologyEvolution.Unmapped;
                entries.Add(new(s.TopologyIndex,HistoryKind(s.Kind),evolution,target,
                    target is {} i?HistoryKind(result.Topology[i].Kind):null,s.ArgumentIndex));
            }
            for(int argument=0;argument<sources.Count;argument++)
                foreach(var t in sources[argument].Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
                    if(!entries.Any(e=>e.SourceArgument==argument&&e.SourceIndex==t.Selection.Index))
                        throw new CadValidationException("Native Boolean history omitted a source topology slot.");
            if(entries.Count>100000)return WithoutHistory("Boolean history exceeds the document evidence budget.");
            var first=recipe.Inputs[0];
            var history=new TopologyHistory(first.Revision,first.AssetId,stored.Geometry.Revision,stored.Geometry.AssetId,
                BooleanHistoryAdapterVersion,sources[0].Fingerprint,result.Fingerprint,sources[0].Topology.Count,result.Topology.Count,
                entries.Distinct().ToImmutableArray(),2)
            {
                AdditionalSources=recipe.Inputs.Skip(1).Select((input,i)=>new TopologyHistorySource(input.Revision,input.AssetId,
                    sources[i+1].Fingerprint,sources[i+1].Topology.Count)).ToImmutableArray()
            };
            history.Validate();token.ThrowIfCancellationRequested();
            return new(stored.Geometry,assets.Acquire(stored.Geometry.AssetId)){TopologyHistory=history};
        }
        finally{foreach(var source in sources)source.Dispose();}
    }

    private static bool VerifyHistoryRoundtrip(RepairSnapshot live,RepairSnapshot stored,CancellationToken token)
    {
        if(!live.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)).SequenceEqual(stored.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex))))return false;
        foreach(var t in live.Topology)
        {
            token.ThrowIfCancellationRequested();
            using var a=live.CopySubshape(t.Selection);using var b=stored.CopySubshape(stored.Select(t.Selection.Index));
            if(!BrepDirectionRoundtrip.Matches(CanonicalBrep(a),CanonicalBrep(b)))return false;
        }
        return true;
    }
}
