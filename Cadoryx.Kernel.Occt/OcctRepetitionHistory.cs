using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    public const string RepetitionHistoryAdapterVersion="OcctSharp 8.0.1-preview.31.cadoryx.curve-inspection.10 / multicopy-native-history-v1";
    private const string LegacyRepetitionHistoryAdapterVersion="OcctSharp 8.0.1-preview.31.cadoryx.curve-inspection.9 / multicopy-native-history-v1";
    private static bool IsRepetitionHistoryAdapter(string value)=>value is RepetitionHistoryAdapterVersion or LegacyRepetitionHistoryAdapterVersion;

    private sealed class RepetitionWitness(RepairSnapshot map,ImmutableArray<TopologyHistoryEntry> entries):IDisposable
    {
        internal RepairSnapshot Map {get;}=map;
        internal ImmutableArray<TopologyHistoryEntry> Entries {get;}=entries;
        public void Dispose()=>Map.Dispose();
    }

    private GeometryResult EvaluateRepetition(RepetitionRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        using var source=RepairSnapshot.Create(shape);
        var copies=new List<RepetitionWitness>();RepetitionWitness? joined=null;
        try
        {
            var originals=source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex).ToArray();
            bool supported=originals.Length>0&&originals.Length*(long)recipe.Count<=100000;
            long evidence=0;
            for(int i=0;i<recipe.Count;i++)
            {
                token.ThrowIfCancellationRequested();using var transform=RepetitionTransform(recipe,i);
                using var operation=ShapeHistoryModeling.Transform(source,transform);
                var map=RepairSnapshot.Create(operation.RequireShape());
                try
                {
                    var rows=RepetitionNativeRows(operation,[source],map,token,ref evidence);
                    supported&=rows is not null;
                    var entries=supported?rows!.Value.Select(e=>e with{CopyId=recipe.CopyIds[i]}).ToImmutableArray():[];
                    copies.Add(new(map,entries));map=null!;
                }
                finally{map?.Dispose();}
            }
            RepetitionWitness current;
            if(copies.Count==1)current=copies[0];
            else if(recipe.Output==RepetitionOutput.Compound)
            {
                using var operation=ShapeHistoryModeling.Compound(copies.Select(c=>c.Map).ToArray());
                joined=JoinRepetitionWitness(operation,copies,token,ref evidence,ref supported);current=joined;
            }
            else
            {
                current=copies[0];
                for(int i=1;i<copies.Count;i++)
                {
                    token.ThrowIfCancellationRequested();
                    using var operation=BooleanHistoryModeling.Build(TopologyBooleanOperation.Fuse,[current.Map,copies[i].Map]);
                    var next=JoinRepetitionWitness(operation,[current,copies[i]],token,ref evidence,ref supported);
                    joined?.Dispose();joined=next;current=next;
                }
            }
            using var finalShape=current.Map.CopyShape();
            using var stored=OcctGeometryBridge.StoreShape(finalShape,assets);
            using var reread=OcctGeometryBridge.ReadShape(stored.Geometry,assets);
            using var resultMap=RepairSnapshot.Create(reread);
            token.ThrowIfCancellationRequested();
            GeometryResult result;
            if(!supported||source.Topology.Count>100000||resultMap.Topology.Count>100000||
                !VerifyHistoryRoundtrip(current.Map,resultMap,token)||current.Entries.Length>100000)
                result=new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),[new("HISTORY.UNSUPPORTED",
                    "A repetition stage or BRep roundtrip has no complete native correspondence within the evidence budget. Reselect final topology explicitly.")]);
            else
            {
                var history=new TopologyHistory(recipe.Source.Revision,recipe.Source.AssetId,stored.Geometry.Revision,stored.Geometry.AssetId,
                    RepetitionHistoryAdapterVersion,source.Fingerprint,resultMap.Fingerprint,source.Topology.Count,resultMap.Topology.Count,
                    current.Entries.Distinct().ToImmutableArray(),4);
                history.Validate();
                result=new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets)){TopologyHistory=history};
            }
            try{return AttachRepetitionStyles(result,recipe,source,resultMap,assets,token);}
            catch{result.Dispose();throw;}
        }
        finally{joined?.Dispose();foreach(var copy in copies)copy.Dispose();}
    }

    private static GpTrsf RepetitionTransform(RepetitionRecipe recipe,int index)
    {
        if(recipe.IncludeSource&&index==0)return GpTrsf.Identity;
        var frame=recipe.Frame;var origin=frame.Translation;
        if(recipe.Kind==RepetitionKind.Mirror)
        {
            var normal=frame.Rotation.Rotate(Vector3d.UnitZ);
            return GpTrsf.CreatePlaneMirror(origin.X,origin.Y,origin.Z,normal.X,normal.Y,normal.Z);
        }
        if(recipe.Kind==RepetitionKind.Linear)
            return OcctGeometryBridge.ToNative(new(frame.Rotation.Rotate(new(1,0,0))*(index*recipe.Step),Quaterniond.Identity));
        var axis=frame.Rotation.Rotate(Vector3d.UnitZ);
        var rotation=Quaterniond.FromAxisAngle(axis,index*(recipe.FullCircle?Math.Tau/recipe.Count:recipe.Step));
        return OcctGeometryBridge.ToNative(new(origin-rotation.Rotate(origin),rotation));
    }

    private static ImmutableArray<TopologyHistoryEntry>? RepetitionNativeRows(LocalFeatureResult operation,IReadOnlyList<RepairSnapshot> sources,
        RepairSnapshot result,CancellationToken token,ref long evidence)
    {
        if(operation.Diagnostics.HasComposedHistory||!operation.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution))return null;
        evidence+=operation.History.Count;
        if(evidence>TopologyHistoryPaths.MaxHistoryEntries)return null;
        var entries=ImmutableArray.CreateBuilder<TopologyHistoryEntry>();
        foreach(var row in operation.History)
        {
            token.ThrowIfCancellationRequested();
            if(row.Source is not {} s||s.PlanId!=operation.PlanId||s.ArgumentIndex<0||s.ArgumentIndex>=sources.Count||
                s.TopologyIndex<0||s.TopologyIndex>=sources[s.ArgumentIndex].Topology.Count||sources[s.ArgumentIndex].Topology[s.TopologyIndex].Kind!=s.Kind)
                throw new CadValidationException("Invalid native repetition source locator.");
            var evolution=row.Kind switch
            {
                LocalFeatureHistoryKind.Unchanged=>TopologyEvolution.Unchanged,LocalFeatureHistoryKind.Modified=>TopologyEvolution.Modified,
                LocalFeatureHistoryKind.Generated=>TopologyEvolution.Generated,LocalFeatureHistoryKind.Deleted=>TopologyEvolution.Deleted,
                LocalFeatureHistoryKind.Unmapped=>TopologyEvolution.Unmapped,_=>throw new CadValidationException("Unknown repetition history relation.")
            };
            if(row.ResultTopologyIndex is {} index&&(index<0||index>=result.Topology.Count||row.Shape is null||result.Topology[index].Kind!=row.Shape.Kind))
                throw new CadValidationException("Invalid native repetition target locator.");
            if(row.ResultTopologyIndex is null&&evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified or TopologyEvolution.Generated)
                evolution=TopologyEvolution.Unmapped;
            entries.Add(new(s.TopologyIndex,HistoryKind(s.Kind),evolution,row.ResultTopologyIndex,
                row.ResultTopologyIndex is {} target?HistoryKind(result.Topology[target].Kind):null,s.ArgumentIndex));
        }
        var covered=entries.Select(e=>(e.SourceArgument,e.SourceIndex)).ToHashSet();
        for(int a=0;a<sources.Count;a++)
            if(sources[a].Topology.Any(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex&&!covered.Contains((a,t.Selection.Index))))
                throw new CadValidationException("Native repetition history omitted a source slot.");
        return entries.Distinct().ToImmutableArray();
    }

    private static RepetitionWitness JoinRepetitionWitness(LocalFeatureResult operation,IReadOnlyList<RepetitionWitness> inputs,
        CancellationToken token,ref long evidence,ref bool supported)
    {
        var map=RepairSnapshot.Create(operation.RequireShape());
        try
        {
            var rows=RepetitionNativeRows(operation,inputs.Select(i=>i.Map).ToArray(),map,token,ref evidence);
            supported&=rows is not null;
            if(!supported){var empty=new RepetitionWitness(map,[]);map=null!;return empty;}
            var bySource=rows!.Value.ToLookup(e=>(e.SourceArgument,e.SourceIndex));
            var shared=rows.Value.Where(e=>e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex!.Value)
                .Where(g=>g.Select(e=>(e.SourceArgument,e.SourceIndex)).Distinct().Take(2).Count()>1).Select(g=>g.Key).ToHashSet();
            var output=new HashSet<TopologyHistoryEntry>();
            for(int a=0;a<inputs.Count;a++)foreach(var origin in inputs[a].Entries)
            {
                token.ThrowIfCancellationRequested();
                if(origin.ResultIndex is not {} old){output.Add(origin);continue;}
                var next=bySource[(a,old)].ToArray();
                if(next.Length==0)throw new CadValidationException("Repetition composition has an unaccounted intermediate source.");
                bool split=next.Where(e=>e.Evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified&&e.ResultKind==origin.ResultKind)
                    .Select(e=>e.ResultIndex).Distinct().Take(2).Count()>1;
                foreach(var successor in next)
                {
                    var evolution=successor.ResultIndex is null?successor.Evolution:
                        origin.Evolution==TopologyEvolution.Generated||successor.Evolution==TopologyEvolution.Generated?TopologyEvolution.Generated:
                        origin.Evolution==TopologyEvolution.Unchanged&&successor.Evolution==TopologyEvolution.Unchanged?TopologyEvolution.Unchanged:TopologyEvolution.Modified;
                    output.Add(origin with{Evolution=evolution,ResultIndex=successor.ResultIndex,ResultKind=successor.ResultKind,
                        PathAmbiguous=origin.PathAmbiguous||split||successor.ResultIndex is {} target&&shared.Contains(target)});
                    if(output.Count>100000){supported=false;break;}
                }
                if(!supported)break;
            }
            var witness=new RepetitionWitness(map,supported?output.ToImmutableArray():[]);map=null!;return witness;
        }
        finally{map?.Dispose();}
    }
}
