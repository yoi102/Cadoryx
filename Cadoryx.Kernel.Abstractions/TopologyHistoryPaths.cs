using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record HistoryPathStep(FeatureId Feature,int SourceArgument);
public sealed record HistoryPathPlan(ImmutableArray<HistoryPathStep> Steps,HistoryResolution? Failure);

/// <summary>Counts dependency routes without enumerating them. Two routes are already ambiguous,
/// even if later native reductions might coincide. No geometry or history is used to choose a route.</summary>
public static class TopologyHistoryPaths
{
    public const int MaxSteps=32,MaxNodes=1024,MaxEdges=4096,MaxDepth=128;
    public const int MaxNativeMaps=256,MaxTopologySlots=1_000_000,MaxHistoryEntries=1_000_000;

    public static HistoryPathPlan Plan(DocumentSnapshot snapshot,FeatureId source,FeatureId target,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        HistoryResolution? failure=null;
        var counts=new Dictionary<FeatureId,int>();var active=new HashSet<FeatureId>();
        var predecessors=new Dictionary<FeatureId,HistoryPathStep>();int nodes=0,edges=0;
        int Fail(HistoryResolutionStatus status,string code,FeatureId id)
        {failure??=new(status,null,0,"HISTORY."+code){StoppedAt=id};return 0;}
        int Visit(FeatureId id,int depth)
        {
            token.ThrowIfCancellationRequested();
            if(failure is not null)return 0;
            if(depth>MaxDepth)return Fail(HistoryResolutionStatus.LimitExceeded,"GRAPH_DEPTH_LIMIT",id);
            if(active.Contains(id))return Fail(HistoryResolutionStatus.WrongContext,"CYCLIC_PATH",id);
            if(counts.TryGetValue(id,out int known))return known;
            if(++nodes>MaxNodes)return Fail(HistoryResolutionStatus.LimitExceeded,"GRAPH_NODE_LIMIT",id);
            if(!snapshot.Features.TryGetValue(id,out var feature))return Fail(HistoryResolutionStatus.Missing,"MISSING_FEATURE",id);
            if(feature.Id!=id||feature.Inputs.IsDefault)return Fail(HistoryResolutionStatus.WrongContext,"INVALID_PATH",id);
            if(id==source){counts[id]=1;return 1;}
            active.Add(id);int count=0;
            var distinct=new HashSet<FeatureId>();
            for(int a=0;a<feature.Inputs.Length;a++)
            {
                if(++edges>MaxEdges)return Fail(HistoryResolutionStatus.LimitExceeded,"GRAPH_EDGE_LIMIT",id);
                var input=feature.Inputs[a];
                if(!distinct.Add(input))return Fail(HistoryResolutionStatus.WrongContext,"DUPLICATE_INPUT",id);
                if(snapshot.Features.TryGetValue(input,out var upstream)&&upstream.PartId!=feature.PartId)
                    return Fail(HistoryResolutionStatus.WrongContext,"FOREIGN_PART",id);
                int routes=Visit(input,depth+1);
                if(failure is not null)return 0;
                if(routes>0)predecessors[id]=new(id,a);
                count=Math.Min(2,count+routes);
                // Still inspect other inputs: do not hide missing nodes, cycles or exceeded budgets.
            }
            active.Remove(id);counts[id]=count;return count;
        }
        if(!snapshot.Features.ContainsKey(source))Fail(HistoryResolutionStatus.Missing,"MISSING_FEATURE",source);
        else if(source==target)Fail(HistoryResolutionStatus.Unsupported,"NO_PATH",target);
        else
        {
            int count=Visit(target,0);
            if(failure is null&&count!=1)Fail(count==0?HistoryResolutionStatus.Unsupported:HistoryResolutionStatus.Ambiguous,
                count==0?"NO_PATH":"MULTIPLE_PATHS",target);
        }
        if(failure is not null)return new([],failure);
        var reverse=new List<HistoryPathStep>();var current=target;
        while(current!=source)
        {
            token.ThrowIfCancellationRequested();
            if(reverse.Count==MaxSteps)
            {Fail(HistoryResolutionStatus.LimitExceeded,"PATH_LENGTH_LIMIT",target);return new([],failure);}
            var step=predecessors[current];reverse.Add(step);current=snapshot.Features[current].Inputs[step.SourceArgument];
        }
        reverse.Reverse();return new([..reverse],null);
    }
}
