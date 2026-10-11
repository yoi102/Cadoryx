using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Explicit degree-two chains and standalone exact curve profiles.
/// An isolated open expression follows the existing single-curve closing-chord contract.</summary>
public static class SketchMixedLoops
{
    private readonly record struct Edge(SketchEntityId Id,SketchEntityId Start,SketchEntityId End,bool IsCurved);
    public static ImmutableArray<ImmutableArray<SketchEntityId>> Find(CadSketch sketch)
    {
        sketch.Validate();
        var edges=sketch.Lines.Where(l=>!l.IsConstruction).Select(l=>new Edge(l.Id,l.Start,l.End,false))
            .Concat(sketch.Arcs.Where(a=>!a.IsConstruction).Select(a=>new Edge(a.Id,a.Start,a.End,true)))
            .Concat(sketch.Beziers.Where(b=>!b.IsConstruction).Select(b=>new Edge(b.Id,b.Start,b.End,true)))
            .Concat(sketch.Splines.Where(s=>!s.IsConstruction).Select(s=>new Edge(s.Id,s.Controls[0],s.Controls[^1],true)))
            .Concat(sketch.Ellipses.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End,true)))
            .Concat(sketch.Freeforms.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End,true)))
            .Concat(sketch.ExpressionCurves.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End,true)))
            .OrderBy(e=>e.Id.Value).ToArray();
        var adjacent=new Dictionary<SketchEntityId,List<Edge>>();
        foreach(var edge in edges)foreach(var endpoint in new[]{edge.Start,edge.End})
        {if(!adjacent.TryGetValue(endpoint,out var list))adjacent[endpoint]=list=[];list.Add(edge);}
        var remaining=edges.Select(e=>e.Id).ToHashSet();
        var loops=ImmutableArray.CreateBuilder<ImmutableArray<SketchEntityId>>();
        foreach(var e in sketch.Ellipses.Where(e=>!e.IsConstruction&&e.Endpoints is null))loops.Add([e.Id]);
        foreach(var e in sketch.Freeforms.Where(e=>!e.IsConstruction&&e.Endpoints is null))loops.Add([e.Id]);
        foreach(var e in sketch.ExpressionCurves.Where(e=>!e.IsConstruction))
        {
            var ends=e.Endpoints!;bool closed=double.Hypot(e.Geometry.At(0).X-e.Geometry.At(1).X,e.Geometry.At(0).Y-e.Geometry.At(1).Y)<=1e-7;
            bool isolated=adjacent[ends.Start].Count==1&&adjacent[ends.End].Count==1;
            var basis=e.Geometry;while(basis.Basis is {} next)basis=next;var p=basis.Primitive!;
            bool curved=p.Middle is not null||p.BezierControl is not null||!p.SplineControls.IsEmpty||p.Ellipse is not null||p.Freeform is not null;
            if(closed||isolated&&curved)
            {try{SketchProfileBuilder.Mixed(sketch,[e.Id]);loops.Add([e.Id]);}catch(CadValidationException){}}
        }
        foreach(var seed in edges)
        {
            if(!remaining.Contains(seed.Id))continue;
            var vertices=new HashSet<SketchEntityId>();var component=new List<Edge>();var pending=new Queue<Edge>();pending.Enqueue(seed);
            while(pending.TryDequeue(out var edge))
            {
                if(!remaining.Remove(edge.Id))continue;component.Add(edge);
                foreach(var endpoint in new[]{edge.Start,edge.End})
                    if(vertices.Add(endpoint))foreach(var next in adjacent[endpoint])pending.Enqueue(next);
            }
            if(component.Count is <2 or >256||!component.Any(e=>e.IsCurved)||vertices.Any(v=>adjacent[v].Count!=2))continue;
            var ordered=ImmutableArray.CreateBuilder<SketchEntityId>();var used=new HashSet<SketchEntityId>();var at=seed.Start;
            while(used.Count<component.Count)
            {
                var next=adjacent[at].First(e=>!used.Contains(e.Id));
                used.Add(next.Id);ordered.Add(next.Id);at=next.Start==at?next.End:next.Start;
            }
            var ids=ordered.ToImmutable();
            try{SketchProfileBuilder.Mixed(sketch,ids);loops.Add(ids);}
            catch(CadValidationException){/* Invalid curves stay editable but cannot create a solid. */}
        }
        return loops.ToImmutable();
    }
}
