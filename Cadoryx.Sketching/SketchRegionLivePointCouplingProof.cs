using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed record SketchRegionLivePointCoupling(Guid ConsumerGroup,SketchEntityId SourceCurve,
    SketchEntityId Point,SketchEntityId PointProvider,Guid ProviderGroup,ImmutableArray<SketchEntityId> LivePath);

/// <summary>One equation system's complete explicit live-expression point paths.
/// The old independent/direct-point proofs keep their existing refusal scope.
/// This proof admits only point variables, not direct geometry consumption.</summary>
internal sealed class SketchRegionLivePointCouplingProof
{
    internal ImmutableArray<SketchRegionLivePointCoupling> Couplings {get;}
    private SketchRegionLivePointCouplingProof(ImmutableArray<SketchRegionLivePointCoupling> couplings)=>Couplings=couplings;

    internal static SketchRegionLivePointCouplingProof? TryCreate(CadSketch sketch,IReadOnlySet<Guid> deferred)
    {
        var active=sketch.RegionOffsets.Where(g=>!g.IsFrozen).ToDictionary(g=>g.Id);
        if(deferred.Count==0||deferred.Count==active.Count||deferred.Any(id=>!active.ContainsKey(id)))return null;
        try
        {
            _=SketchRegionAuthorModel.OrderForRefresh(sketch);
            var owners=sketch.RegionOffsets.SelectMany(g=>g.Loops.SelectMany(l=>l.Outputs).Select(o=>(o.Curve,g.Id)))
                .ToDictionary(p=>p.Curve,p=>p.Id);
            var providers=SketchAssociativeEndpoints.Equations(sketch).GroupBy(e=>e.Point)
                .ToDictionary(g=>g.Key,g=>g.Select(e=>e.Curve).Distinct().ToImmutableArray());
            var couplings=ImmutableArray.CreateBuilder<SketchRegionLivePointCoupling>();bool hasIntermediate=false;
            foreach(var group in active.Values)
            foreach(var source in group.Sources.SelectMany(l=>l))
            {
                if(sketch.ExpressionCurves.Any(e=>e.Id==source.Entity))
                {
                    if(Dependencies(source.Entity,[],0).Any(id=>Crosses(group.Id,id)))return null;
                    continue;
                }
                var points=ExplicitPoints(source.Entity);
                if(points.IsEmpty&&Dependencies(source.Entity,[],0).Any(id=>Crosses(group.Id,id)))return null;
                foreach(var point in points)
                {
                    if(!providers.TryGetValue(point,out var curves))continue;
                    foreach(var curve in curves)
                    {
                        var dependencies=Dependencies(curve,[],0);
                        if(!dependencies.Any(id=>Crosses(group.Id,id)))continue;
                        if(dependencies.Count!=1||sketch.Points.Single(p=>p.Id==point).Origin!=SketchPointOrigin.CurveGenerated)return null;
                        var path=LivePath(curve);if(path.IsDefaultOrEmpty)return null;
                        var provider=owners[path[^1]];
                        if(!Crosses(group.Id,provider)||!dependencies.Contains(provider))return null;
                        var pointProvider=sketch.ExpressionCurves.Single(e=>e.Id==curve);
                        if(pointProvider.Endpoints is not {} ends||ends.Start!=point&&ends.End!=point)return null;
                        // Every provider of a shared variable remains a structural
                        // equation. Preserve all paths instead of choosing one.
                        couplings.Add(new(group.Id,source.Entity,point,curve,provider,path));
                        hasIntermediate|=path.Length>1;
                    }
                }
            }
            return !hasIntermediate?null:new(couplings.ToImmutable());

            bool Crosses(Guid consumer,Guid provider)=>active.ContainsKey(provider)&&deferred.Contains(consumer)!=deferred.Contains(provider);
            ImmutableArray<SketchEntityId> LivePath(SketchEntityId entity)
            {
                var path=ImmutableArray.CreateBuilder<SketchEntityId>();var seen=new HashSet<SketchEntityId>();
                while(true)
                {
                    if(path.Count>=SketchCurveExpressionGeometry.MaximumDepth||!seen.Add(entity))return [];
                    path.Add(entity);
                    if(owners.TryGetValue(entity,out var owner))return active.ContainsKey(owner)?path.ToImmutable():[];
                    var expression=sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==entity);
                    if(expression?.Source is not {IsFrozen:false,Kind:SketchExpressionSourceKind.Expression} source)return [];
                    entity=source.Entity;
                }
            }
            HashSet<Guid> Dependencies(SketchEntityId entity,HashSet<SketchEntityId> path,int depth)
            {
                if(depth>=SketchCurveExpressionGeometry.MaximumDepth||!path.Add(entity))
                    throw new CadValidationException("Live point coupling exceeds the existing author depth or contains a cycle.");
                try
                {
                    if(owners.TryGetValue(entity,out var owner))return active.ContainsKey(owner)?[owner]:[];
                    if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==entity) is {} expression)
                        return expression.Source is {IsFrozen:false} source?Dependencies(source.Entity,path,depth+1):[];
                    var result=new HashSet<Guid>();var points=ExplicitPoints(entity);
                    if(points.IsEmpty&&sketch.Circles.SingleOrDefault(e=>e.Id==entity) is {} circle)points=[circle.Center];
                    if(points.IsEmpty&&SketchFixedCurveEndpoints.All(sketch).SingleOrDefault(e=>e.Curve==entity).Ends is {} ends)points=[ends.Start,ends.End];
                    foreach(var point in points)
                        if(providers.TryGetValue(point,out var curves))
                            foreach(var curve in curves)result.UnionWith(Dependencies(curve,path,depth+1));
                    return result;
                }
                finally{path.Remove(entity);}
            }
            ImmutableArray<SketchEntityId> ExplicitPoints(SketchEntityId entity)
            {
                if(sketch.Lines.SingleOrDefault(e=>e.Id==entity) is {} line)return[line.Start,line.End];
                if(sketch.Arcs.SingleOrDefault(e=>e.Id==entity) is {} arc)return[arc.Start,arc.Middle,arc.End];
                if(sketch.Beziers.SingleOrDefault(e=>e.Id==entity) is {} bezier)return[bezier.Start,bezier.Control,bezier.End];
                if(sketch.Splines.SingleOrDefault(e=>e.Id==entity) is {} spline)return spline.Controls;
                return [];
            }
        }
        catch(CadValidationException){return null;}
    }
}
