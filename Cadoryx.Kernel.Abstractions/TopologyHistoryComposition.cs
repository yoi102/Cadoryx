using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Composes declared native witnesses. A stopped source can never become
/// a usable final locator, even when later operations happen to recombine its branches.</summary>
public static class TopologyHistoryComposition
{
    public const int MaximumStages=32;
    public const int MaximumEntries=1_000_000;
    public static TopologyHistory Compose(IReadOnlyList<TopologyHistory> stages,string adapter,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();CadGuard.Name(adapter);
        if(stages.Count is <2 or >MaximumStages)throw new CadValidationException("Composed native history exceeds its stage budget.");
        long total=0;var indices=new List<Index>(stages.Count);
        for(int i=0;i<stages.Count;i++)
        {
            token.ThrowIfCancellationRequested();var stage=stages[i]??throw new CadValidationException("A native history stage is missing.");stage.Validate();
            total+=stage.Entries.Length;if(total>MaximumEntries)throw new CadValidationException("Composed native history exceeds its entry budget.");
            if(i>0)
            {
                var previous=stages[i-1];
                if(previous.ResultRevision!=stage.SourceRevision||previous.ResultAsset!=stage.SourceAsset||
                    previous.ResultFingerprint!=stage.SourceFingerprint||previous.ResultCount!=stage.SourceCount)
                    throw new CadValidationException("Native history stages do not share the same immutable intermediate result.");
            }
            indices.Add(new(stage));
        }
        var entries=ImmutableArray.CreateBuilder<TopologyHistoryEntry>();
        foreach(var source in stages[0].Entries.Where(e=>e.SourceArgument==0).Select(e=>(e.SourceIndex,e.SourceKind)).Distinct())
        {
            token.ThrowIfCancellationRequested();int index=source.SourceIndex;bool unchanged=true;TopologyEvolution? stop=null;
            foreach(var stage in indices)
            {
                token.ThrowIfCancellationRequested();var next=stage.Resolve(index,source.SourceKind);
                if(next.Target is not {} target){stop=next.Evolution;break;}
                unchanged&=next.Evolution==TopologyEvolution.Unchanged;index=target;
            }
            entries.Add(new(source.SourceIndex,source.SourceKind,stop??(unchanged?TopologyEvolution.Unchanged:TopologyEvolution.Modified),
                stop is null?index:null,stop is null?source.SourceKind:null));
        }
        var first=stages[0];var last=stages[^1];
        var result=new TopologyHistory(first.SourceRevision,first.SourceAsset,last.ResultRevision,last.ResultAsset,adapter,
            first.SourceFingerprint,last.ResultFingerprint,first.SourceCount,last.ResultCount,entries.ToImmutable(),3);
        result.Validate();token.ThrowIfCancellationRequested();return result;
    }
    private sealed class Index
    {
        private readonly Dictionary<(int Index,HistoryShapeKind Kind),TopologyHistoryEntry[]> sources;
        private readonly HashSet<int> shared;
        internal Index(TopologyHistory stage)
        {
            sources=stage.Entries.Where(e=>e.SourceArgument==0).GroupBy(e=>(e.SourceIndex,e.SourceKind)).ToDictionary(g=>g.Key,g=>g.ToArray());
            // Include every operand, including a temporary Boolean cutter. Dropping
            // its ownership before this check would hide a real shared target.
            shared=stage.Entries.Where(e=>e.ResultIndex.HasValue).GroupBy(e=>e.ResultIndex!.Value)
                .Where(g=>g.Select(e=>(e.SourceArgument,e.SourceIndex)).Distinct().Take(2).Count()>1).Select(g=>g.Key).ToHashSet();
        }
        internal (TopologyEvolution Evolution,int? Target) Resolve(int source,HistoryShapeKind kind)
        {
            if(!sources.TryGetValue((source,kind),out var entries)||entries.Any(e=>e.Evolution==TopologyEvolution.Unmapped))return(TopologyEvolution.Unmapped,null);
            if(entries.Any(e=>e.Evolution==TopologyEvolution.Ambiguous))return(TopologyEvolution.Ambiguous,null);
            if(entries.Any(e=>e.Evolution==TopologyEvolution.GeneratedOnly))return(TopologyEvolution.GeneratedOnly,null);
            if(entries.Any(e=>e.Evolution==TopologyEvolution.Deleted))return(entries.Length==1?TopologyEvolution.Deleted:TopologyEvolution.Ambiguous,null);
            var targets=entries.Where(e=>e.Evolution is TopologyEvolution.Unchanged or TopologyEvolution.Modified&&e.ResultKind==kind)
                .Select(e=>e.ResultIndex!.Value).Distinct().Take(2).ToArray();
            if(targets.Length==0)return(entries.Any(e=>e.Evolution==TopologyEvolution.Generated)?TopologyEvolution.GeneratedOnly:TopologyEvolution.Unmapped,null);
            if(targets.Length!=1||shared.Contains(targets[0]))return(TopologyEvolution.Ambiguous,null);
            return(entries.Where(e=>e.ResultIndex==targets[0]).All(e=>e.Evolution==TopologyEvolution.Unchanged)?TopologyEvolution.Unchanged:TopologyEvolution.Modified,targets[0]);
        }
    }
}
