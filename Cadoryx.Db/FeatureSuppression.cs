using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Computes the feature closure intentionally blocked by suppression.</summary>
public static class FeatureSuppression
{
    public static ImmutableHashSet<FeatureId> Blocked(DocumentSnapshot document)
    {
        var blocked=document.Features.Values.Where(f=>f.IsSuppressed).Select(f=>f.Id).ToHashSet();
        if(blocked.Count==0)return ImmutableHashSet<FeatureId>.Empty;
        var consumers=new Dictionary<FeatureId,List<FeatureId>>();
        foreach(var feature in document.Features.Values)
            foreach(var input in feature.Inputs)
        {
            if(!consumers.TryGetValue(input,out var next))consumers[input]=next=[];
            next.Add(feature.Id);
        }
        var queue=new Queue<FeatureId>(blocked);
        while(queue.TryDequeue(out var featureId))
        {
            if(!consumers.TryGetValue(featureId,out var next))continue;
            foreach(var id in next)if(blocked.Add(id))queue.Enqueue(id);
        }
        return blocked.ToImmutableHashSet();
    }
}
