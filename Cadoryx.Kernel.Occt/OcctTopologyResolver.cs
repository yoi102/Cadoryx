using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    public Task<TopologyResolution> ResolveAsync(DocumentSnapshot snapshot,TopologyReference reference,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();reference.Validate();
        TopologyResolution Failure(TopologyResolutionStatus status,string code)=>new(reference.Id,status,null,0,code);
        if(reference.DocumentId!=snapshot.Id)return Task.FromResult(Failure(TopologyResolutionStatus.WrongContext,"TOPOLOGY.WRONG_DOCUMENT"));
        if(!snapshot.Features.TryGetValue(reference.FeatureId,out var feature))return Task.FromResult(Failure(TopologyResolutionStatus.Missing,"TOPOLOGY.PRODUCER_MISSING"));
        if(feature.OutputBodyId!=reference.OutputBodyId)return Task.FromResult(Failure(TopologyResolutionStatus.WrongContext,"TOPOLOGY.WRONG_OUTPUT"));
        if(reference.Policy==TopologyRebindPolicy.ExactRevision&&reference.OriginRevision!=feature.Result.Revision)
            return Task.FromResult(Failure(TopologyResolutionStatus.Stale,"TOPOLOGY.REVISION_CHANGED"));
        if(feature.Recipe is not BoxRecipe box)return Task.FromResult(Failure(TopologyResolutionStatus.Unsupported,"TOPOLOGY.HISTORY_UNSUPPORTED"));
        box.Validate();
        if(feature.Result.Kind==BodyKind.Empty)return Task.FromResult(Failure(TopologyResolutionStatus.Missing,"TOPOLOGY.EMPTY_OUTPUT"));
        // Fixed numerical policy: user display tolerances must never merge distinct boundaries.
        const double tolerance=1e-6;
        if(Math.Min(box.X,Math.Min(box.Y,box.Z))<=tolerance*100)
            return Task.FromResult(Failure(TopologyResolutionStatus.Unsupported,"TOPOLOGY.BELOW_RESOLUTION"));
        return Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);
            using var topology=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
            var parts=reference.Kind==TopologyKind.Face?topology.Ancestors:topology.Items;
            var matches=new List<int>();
            for(int i=0;i<parts.Count;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(BoxTopology.Matches(parts[i],box,reference.Kind,reference.Boundary,reference.SecondBoundary))matches.Add(i);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return TopologyResolution.Candidates(reference,feature.Result,Version,matches);
        },cancellationToken);
    }
}
