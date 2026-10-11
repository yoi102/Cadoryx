using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Explicit author identities and directed degree-two endpoint chains.
/// Coordinate coincidence never joins authors or supplies an implicit closing chord.
/// Native geometry and containment are certified separately by the region kernel.</summary>
public static class SketchExpressionRegionSelection
{
    private sealed record Edge(SketchEntityId Id,SketchEntityId Start,SketchEntityId End);
    public static ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> Select(CadSketch sketch,IEnumerable<SketchEntityId> seeds)
    {
        sketch.Validate();ArgumentNullException.ThrowIfNull(seeds);
        var supplied=seeds.Take(257).ToArray();
        if(supplied.Length is <1 or >256)throw new CadValidationException("Select one through 256 explicit region seed authors.");
        var ids=supplied.Distinct().ToArray();
        var edges=sketch.Lines.Where(e=>!e.IsConstruction).Select(e=>new Edge(e.Id,e.Start,e.End))
            .Concat(sketch.Arcs.Where(e=>!e.IsConstruction).Select(e=>new Edge(e.Id,e.Start,e.End)))
            .Concat(sketch.Beziers.Where(e=>!e.IsConstruction).Select(e=>new Edge(e.Id,e.Start,e.End)))
            .Concat(sketch.Splines.Where(e=>!e.IsConstruction).Select(e=>new Edge(e.Id,e.Controls[0],e.Controls[^1])))
            .Concat(sketch.Ellipses.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End)))
            .Concat(sketch.Freeforms.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End)))
            .Concat(sketch.ExpressionCurves.Where(e=>!e.IsConstruction&&e.Endpoints is not null).Select(e=>new Edge(e.Id,e.Endpoints!.Start,e.Endpoints.End)))
            .ToDictionary(e=>e.Id);
        var adjacent=new Dictionary<SketchEntityId,List<Edge>>();
        foreach(var edge in edges.Values)foreach(var point in new[]{edge.Start,edge.End})
        {if(!adjacent.TryGetValue(point,out var list))adjacent.Add(point,list=[]);list.Add(edge);}
        var loops=ImmutableArray.CreateBuilder<ImmutableArray<ExactExpressionRegionSource>>();var used=new HashSet<SketchEntityId>();
        foreach(var seed in ids)
        {
            if(used.Contains(seed))continue;
            bool circle=sketch.Circles.Any(c=>c.Id==seed&&!c.IsConstruction);
            bool fixedClosed=sketch.Ellipses.Any(e=>e.Id==seed&&!e.IsConstruction&&e.Endpoints is null)||
                sketch.Freeforms.Any(e=>e.Id==seed&&!e.IsConstruction&&e.Endpoints is null);
            if(circle||fixedClosed)
            {
                var source=Value(seed,false);RequireClosed(source.Geometry);used.Add(seed);loops.Add([source]);
            }
            else
            {
                if(!edges.TryGetValue(seed,out var first))throw new CadValidationException("Region seed is missing, construction-only or lacks explicit endpoint identities.");
                var geometry=SketchExpressionEntities.Author(sketch,seed).Geometry;
                if(adjacent[first.Start].Count==1&&adjacent[first.End].Count==1&&Near(geometry.At(0),geometry.At(1)))
                {RequireEndpoints(first,geometry);used.Add(seed);loops.Add([Value(seed,false)]);}
                else
                {
                    var component=new Dictionary<SketchEntityId,Edge>();var pending=new Queue<Edge>();pending.Enqueue(first);
                    while(pending.TryDequeue(out var edge))
                    {
                        if(!component.TryAdd(edge.Id,edge))continue;
                        if(component.Count>256)throw new CadValidationException("An explicit region loop exceeds the existing 256-curve budget.");
                        foreach(var point in new[]{edge.Start,edge.End})foreach(var next in adjacent[point])pending.Enqueue(next);
                    }
                    if(component.Count<2||component.Values.SelectMany(e=>new[]{e.Start,e.End}).Any(p=>adjacent[p].Count!=2))
                        throw new CadValidationException("Region seeds require one closed degree-two chain of explicit endpoint identities; add an authored closing edge or resolve the branch.");
                    var ordered=ImmutableArray.CreateBuilder<ExactExpressionRegionSource>();var traversed=new HashSet<SketchEntityId>();
                    var at=first.Start;var nextEdge=first;
                    while(traversed.Count<component.Count)
                    {
                        if(!traversed.Add(nextEdge.Id))throw new CadValidationException("Explicit region traversal repeats a source author.");
                        var source=Value(nextEdge.Id,nextEdge.Start!=at);RequireEndpoints(nextEdge,source.Geometry);ordered.Add(source);
                        at=nextEdge.Start==at?nextEdge.End:nextEdge.Start;
                        if(traversed.Count<component.Count)
                        {
                            var next=adjacent[at].Where(e=>!traversed.Contains(e.Id)).ToArray();
                            if(next.Length!=1)throw new CadValidationException("Explicit region traversal has no unique next source author.");
                            nextEdge=next[0];
                        }
                    }
                    if(at!=first.Start)throw new CadValidationException("Explicit region traversal does not close on its original point identity.");
                    used.UnionWith(traversed);loops.Add(ordered.ToImmutable());
                }
            }
            if(loops.Count>64||loops.Sum(l=>l.Length)>4096)throw new CadValidationException("Selected regions exceed the existing loop or total-curve budget.");
        }
        return loops.ToImmutable();

        ExactExpressionRegionSource Value(SketchEntityId id,bool reversed)
        {var author=SketchExpressionEntities.Author(sketch,id);author.Geometry.Validate();return new(id,author.Geometry,reversed);}
        void RequireEndpoints(Edge edge,SketchCurveExpressionGeometry geometry)
        {
            var start=sketch.Points.Single(p=>p.Id==edge.Start).Position;var end=sketch.Points.Single(p=>p.Id==edge.End).Position;
            if(!Near(start,geometry.At(0))||!Near(end,geometry.At(1)))
                throw new CadValidationException("Solve the explicit author endpoints before selecting their region boundary.");
        }
    }
    private static void RequireClosed(SketchCurveExpressionGeometry geometry)
    {if(!Near(geometry.At(0),geometry.At(1)))throw new CadValidationException("A standalone region source must be closed; its profile closing chord is not an authored source.");}
    private static bool Near(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-7;
}
