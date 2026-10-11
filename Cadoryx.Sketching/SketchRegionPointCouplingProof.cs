using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed record SketchRegionPointCoupling(Guid ConsumerGroup,SketchEntityId SourceCurve,
    SketchEntityId Point,Guid ProviderGroup,SketchEntityId ProviderCurve);

/// <summary>A single solve's explicit point-provider proof. A crossing edge is
/// admitted only through a real generated variable and its direct region owner;
/// all original structural rows remain required. This does not admit consumers
/// of deferred expression geometry or intermediate live expression providers.</summary>
internal sealed class SketchRegionPointCouplingProof
{
    internal ImmutableArray<SketchRegionPointCoupling> Couplings {get;}
    private SketchRegionPointCouplingProof(ImmutableArray<SketchRegionPointCoupling> couplings)=>Couplings=couplings;

    internal static SketchRegionPointCouplingProof? TryCreate(CadSketch sketch,IReadOnlySet<Guid> deferred)
    {
        var active=sketch.RegionOffsets.Where(g=>!g.IsFrozen).ToDictionary(g=>g.Id);
        if(deferred.Count==0||deferred.Count==active.Count||deferred.Any(id=>!active.ContainsKey(id)))return null;
        try
        {
            // The established author graph includes every structural provider,
            // detects self/cross-group cycles and retains the depth64 contract.
            _=SketchRegionAuthorModel.OrderForRefresh(sketch);
            var owners=sketch.RegionOffsets.SelectMany(g=>g.Loops.SelectMany(l=>l.Outputs).Select(o=>(o.Curve,g.Id)))
                .ToDictionary(p=>p.Curve,p=>p.Id);
            var providers=SketchAssociativeEndpoints.Equations(sketch).GroupBy(e=>e.Point)
                .ToDictionary(g=>g.Key,g=>g.Select(e=>e.Curve).Distinct().ToImmutableArray());
            var couplings=ImmutableArray.CreateBuilder<SketchRegionPointCoupling>();
            foreach(var group in active.Values)
            foreach(var source in group.Sources.SelectMany(l=>l))
            {
                // Direct consumption of another group's geometry is not the
                // point-variable route. Follow live chains to detect crossings;
                // frozen authors deliberately end those dependencies.
                if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==source.Entity) is not null)
                {
                    if(Dependencies(source.Entity,[],0).Any(id=>Crosses(group.Id,id)))return null;
                    continue;
                }
                var pointIds=ExplicitPoints(source.Entity);
                foreach(var point in pointIds)
                {
                    if(!providers.TryGetValue(point,out var curves))continue;
                    foreach(var curve in curves)
                    {
                        var dependencies=Dependencies(curve,[],0);
                        if(!dependencies.Any(id=>Crosses(group.Id,id)))continue;
                        if(!owners.TryGetValue(curve,out var provider)||!active.ContainsKey(provider)||
                            !Crosses(group.Id,provider)||dependencies.Count!=1||!dependencies.Contains(provider))return null;
                        if(sketch.Points.Single(p=>p.Id==point).Origin!=SketchPointOrigin.CurveGenerated)return null;
                        var output=active[provider].Loops.SelectMany(l=>l.Outputs).Single(o=>o.Curve==curve);
                        if(output.Endpoints.Start!=point&&output.Endpoints.End!=point)return null;
                        // Every crossing provider is recorded; no arbitrary
                        // choice or coordinate-equality identity inference.
                        couplings.Add(new(group.Id,source.Entity,point,provider,curve));
                    }
                }
                // Unsupported explicit authors may carry endpoint metadata.
                // A crossing dependency there is outside this first proof.
                if(pointIds.IsEmpty&&Dependencies(source.Entity,[],0).Any(id=>Crosses(group.Id,id)))return null;
            }
            return couplings.Count==0?null:new(couplings.Distinct().ToImmutableArray());

            bool Crosses(Guid consumer,Guid provider)=>active.ContainsKey(provider)&&deferred.Contains(consumer)!=deferred.Contains(provider);
            HashSet<Guid> Dependencies(SketchEntityId entity,HashSet<SketchEntityId> path,int depth)
            {
                if(depth>=SketchCurveExpressionGeometry.MaximumDepth||!path.Add(entity))
                    throw new CadValidationException("Point coupling proof exceeded the original depth or contains a source cycle.");
                try
                {
                    if(owners.TryGetValue(entity,out var owner))return active.ContainsKey(owner)?[owner]:[];
                    if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==entity) is {} expression)
                        return expression.Source is {IsFrozen:false} source?Dependencies(source.Entity,path,depth+1):[];
                    var result=new HashSet<Guid>();
                    var points=ExplicitPoints(entity);
                    if(points.IsEmpty&&sketch.Circles.SingleOrDefault(e=>e.Id==entity) is {} circle)
                        points=[circle.Center];
                    if(points.IsEmpty&&SketchFixedCurveEndpoints.All(sketch).SingleOrDefault(e=>e.Curve==entity).Ends is {} ends)
                        points=[ends.Start,ends.End];
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
