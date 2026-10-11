using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private GeometryResult EvaluateHolePattern(HolePatternRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        if(recipe.Template.Thread?.Representation==ThreadHoleRepresentation.Modeled&&threadExecutor is {} executor)
            return executor.EvaluateAsync(recipe,assets,token).GetAwaiter().GetResult();
        GeometryResult? current=null;
        var histories=new List<TopologyHistory?>();
        try
        {
            var source=recipe.Layout.IncludeSource?recipe.Source:recipe.Template.Source;
            for(int i=recipe.Layout.IncludeSource?1:0;i<recipe.Layout.Count;i++)
            {
                token.ThrowIfCancellationRequested();
                var next=EvaluateHole(recipe.At(i,source),assets,token);
                if(histories.Count<TopologyHistoryComposition.MaximumStages)histories.Add(next.TopologyHistory);
                current?.Dispose();current=next;source=next.Geometry;
            }
            token.ThrowIfCancellationRequested();
            if(recipe.Layout.IncludeSource&&current is not null)
                return FinishComposedDrilling(current,histories,assets,token,
                    (recipe.Layout.Count-1)*(recipe.Template.Thread?.Representation==ThreadHoleRepresentation.Modeled||recipe.Template.Head!=HoleHead.Plain?2:1));
            return new(source,GeometryAssetLease.Acquire(source,assets),[new("HISTORY.UNSUPPORTED",
                "Drilling operations are replayed sequentially. No original-to-final topology map is composed; select final topology explicitly.")]);
        }
        finally{current?.Dispose();}
    }
}
