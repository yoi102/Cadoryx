using System.Collections.Immutable;

namespace Cadoryx.Db;

public static class SketchLoops
{
    /// <summary>Only unambiguous degree-two straight-line components are eligible. Never guesses through branches.</summary>
    public static ImmutableArray<ImmutableArray<SketchEntityId>> Find(CadSketch sketch)
    {
        var lines=sketch.Lines.Where(l=>!l.IsConstruction).OrderBy(l=>l.Id.Value).ToArray();
        var adjacent=new Dictionary<SketchEntityId,List<SketchLine>>();
        foreach(var line in lines)foreach(var endpoint in new[]{line.Start,line.End})
        {if(!adjacent.TryGetValue(endpoint,out var list))adjacent[endpoint]=list=[];list.Add(line);}
        var remaining=lines.Select(l=>l.Id).ToHashSet();
        var loops=ImmutableArray.CreateBuilder<ImmutableArray<SketchEntityId>>();
        foreach(var seed in lines)
        {
            if(!remaining.Contains(seed.Id))continue;
            var vertices=new HashSet<SketchEntityId>();var component=new List<SketchLine>();var pending=new Queue<SketchLine>();pending.Enqueue(seed);
            while(pending.TryDequeue(out var line))
            {
                if(!remaining.Remove(line.Id))continue;component.Add(line);
                foreach(var endpoint in new[]{line.Start,line.End})if(vertices.Add(endpoint))foreach(var next in adjacent[endpoint])pending.Enqueue(next);
            }
            if(vertices.Any(p=>adjacent[p].Count!=2))continue;
            var ordered=ImmutableArray.CreateBuilder<SketchEntityId>();var used=new HashSet<SketchEntityId>();var at=seed.Start;
            while(used.Count<component.Count)
            {
                var next=adjacent[at].First(l=>!used.Contains(l.Id));
                used.Add(next.Id);ordered.Add(next.Id);at=next.Start==at?next.End:next.Start;
            }
            var ids=ordered.ToImmutable();
            try{SketchProfileBuilder.Polygon(sketch,ids);loops.Add(ids);}catch(CadValidationException){/* An invalid component stays editable, but is not a feature profile. */}
        }
        return loops.ToImmutable();
    }
}
