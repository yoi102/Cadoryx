using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum SketchRegionAuthorJoin { Miter,Round,Bevel }
public enum SketchRegionAuthorRole { Parallel,RoundBridge,BevelBridge }
public sealed record SketchRegionSourceAuthor(SketchEntityId Entity,SketchExpressionSourceKind Kind,
    SketchCurveExpressionGeometry Geometry,bool Reversed);
public sealed record SketchRegionOutputAuthor(SketchEntityId Curve,SketchExpressionEndpoints Endpoints,
    SketchRegionAuthorRole Role,ImmutableArray<int> SourceSlots,int BasisDepth,bool SourceTraversalReversed,
    SketchCurveExpressionGeometry Geometry);
public sealed record SketchRegionLoopAuthor(int Depth,int? Parent,bool InputTraversalReversed,
    ImmutableArray<SketchRegionOutputAuthor> Outputs)
{public ImmutableArray<SketchRegionJointAuthor> Joints {get;init;}=[];}
public sealed record SketchRegionOffsetAuthor(Guid Id,ImmutableArray<ImmutableArray<SketchRegionSourceAuthor>> Sources,
    double Distance,SketchRegionAuthorJoin Join,ImmutableArray<SketchRegionLoopAuthor> Loops,bool IsFrozen=false,string? FrozenReason=null)
{public string Algorithm {get;init;}=SketchRegionJointAuthors.LegacyAlgorithm;}
public readonly record struct SketchRegionDependencyNode(bool IsGroup,Guid Id);

public static class SketchRegionAuthorModel
{
    public static ImmutableArray<SketchRegionReference> Roots(CadSketch sketch,Guid groupId)
    {
        sketch.Validate();var group=sketch.RegionOffsets.SingleOrDefault(g=>g.Id==groupId)??throw new CadValidationException("Unknown complete region author.");
        SketchRegionReference Loop(int i)=>new(MixedCurveIds:group.Loops[i].Outputs.Select(o=>o.Curve).ToImmutableArray())
        {Children=Enumerable.Range(0,group.Loops.Length).Where(j=>group.Loops[j].Parent==i).Select(Loop).ToImmutableArray()};
        return Enumerable.Range(0,group.Loops.Length).Where(i=>group.Loops[i].Parent is null).Select(Loop).ToImmutableArray();
    }
    internal static void ValidateLocal(CadSketch sketch)
    {if(!sketch.RegionOffsets.IsDefault&&sketch.RegionOffsets.IsEmpty)return;_ = ValidateAndOrder(sketch,sketch.RegionOffsets,true);}
    public static ImmutableArray<SketchRegionDependencyNode> Order(CadSketch sketch)
    {sketch.Validate();return ValidateAndOrder(sketch,sketch.RegionOffsets,true);}
    /// <summary>Checks stored ownership and dependencies before changed source values
    /// have been derived. The returned candidate must still pass full validation.</summary>
    public static ImmutableArray<SketchRegionDependencyNode> OrderForRefresh(CadSketch sketch)
    {SketchAssociativeEndpoints.Validate(sketch);return ValidateAndOrder(sketch,sketch.RegionOffsets,false);}
    public static bool SameTable(CadSketch first,CadSketch second)=>!first.RegionOffsets.IsDefault&&!second.RegionOffsets.IsDefault&&
        first.RegionOffsets.Length==second.RegionOffsets.Length&&first.RegionOffsets.Zip(second.RegionOffsets).All(pair=>SameValues(pair.First,pair.Second));
    public static bool SameValues(SketchRegionOffsetAuthor a,SketchRegionOffsetAuthor b)
    {
        if(a.Id!=b.Id||a.Distance!=b.Distance||a.Join!=b.Join||a.IsFrozen!=b.IsFrozen||a.FrozenReason!=b.FrozenReason||a.Algorithm!=b.Algorithm||
            a.Sources.IsDefault||b.Sources.IsDefault||a.Loops.IsDefault||b.Loops.IsDefault||a.Sources.Length!=b.Sources.Length||a.Loops.Length!=b.Loops.Length)return false;
        for(int i=0;i<a.Sources.Length;i++)
        {
            var x=a.Sources[i];var y=b.Sources[i];if(x.IsDefault||y.IsDefault||x.Length!=y.Length)return false;
            for(int j=0;j<x.Length;j++)if(x[j].Entity!=y[j].Entity||x[j].Kind!=y[j].Kind||x[j].Reversed!=y[j].Reversed||!x[j].Geometry.SameValues(y[j].Geometry))return false;
            var p=a.Loops[i];var q=b.Loops[i];if(p.Depth!=q.Depth||p.Parent!=q.Parent||p.InputTraversalReversed!=q.InputTraversalReversed||
                p.Outputs.IsDefault||q.Outputs.IsDefault||p.Outputs.Length!=q.Outputs.Length||!SketchRegionJointAuthors.SameValues(p.Joints,q.Joints))return false;
            for(int j=0;j<p.Outputs.Length;j++)
            {
                var u=p.Outputs[j];var v=q.Outputs[j];if(u.Curve!=v.Curve||u.Endpoints!=v.Endpoints||u.Role!=v.Role||u.BasisDepth!=v.BasisDepth||
                    u.SourceTraversalReversed!=v.SourceTraversalReversed||!u.SourceSlots.SequenceEqual(v.SourceSlots)||!u.Geometry.SameValues(v.Geometry))return false;
            }
        }
        return true;
    }
    private static ImmutableArray<SketchRegionDependencyNode> ValidateAndOrder(CadSketch sketch,ImmutableArray<SketchRegionOffsetAuthor> groups,bool requireCurrentSources)
    {
        if(groups.IsDefault||groups.Length>10000)throw new CadValidationException("Uninitialized or oversized region author table.");
        var owners=new Dictionary<SketchEntityId,Guid>();var byId=new Dictionary<Guid,SketchRegionOffsetAuthor>();
        foreach(var group in groups)
        {
            if(group is null)throw new CadValidationException("Missing owned region author.");
            if(group.Algorithm is not (SketchRegionJointAuthors.LegacyAlgorithm or SketchRegionJointAuthors.CurrentAlgorithm))throw new CadValidationException("Unsupported region author algorithm.");
            if(group.Id==Guid.Empty||!byId.TryAdd(group.Id,group)||!Enum.IsDefined(group.Join))throw new CadValidationException("Invalid or duplicate region author identity or join.");
            CadGuard.Finite(group.Distance);
            if(Math.Abs(group.Distance)<=1e-7||group.Sources.IsDefault||group.Sources.Length is <1 or >64||group.Loops.IsDefault||group.Sources.Length!=group.Loops.Length||
                group.Sources.Any(l=>l.IsDefault||l.Length is <1 or >256)||group.Sources.Sum(l=>l.Length)>4096)
                throw new CadValidationException("Region author exceeds the existing source loop/curve budget.");
            if(group.IsFrozen!=(group.FrozenReason is not null))throw new CadValidationException("Frozen region authors need an explicit reason and retain their exact last valid value.");
            if(group.FrozenReason is {} reason)CadGuard.Name(reason);
            if(group.Loops.Any(l=>l is null||l.Outputs.IsDefault||l.Outputs.Any(o=>o is null||o.Geometry is null||o.Endpoints is null)))
                throw new CadValidationException("Incomplete owned region loop or output.");
            var sourceIds=new HashSet<SketchEntityId>();
            for(int i=0;i<group.Sources.Length;i++)
            {
                foreach(var source in group.Sources[i])
                {
                    if(source is null||source.Geometry is null)throw new CadValidationException("Missing owned region source.");
                    CadGuard.Id(source.Entity);source.Geometry.Validate();
                    if(!Enum.IsDefined(source.Kind)||!sourceIds.Add(source.Entity))throw new CadValidationException("Invalid or duplicate region source slot.");
                    if(requireCurrentSources&&!group.IsFrozen)
                    {
                        var current=SketchExpressionEntities.Author(sketch,source.Entity);
                        if(current.Kind!=source.Kind||!current.Geometry.SameValues(source.Geometry))throw new CadValidationException("Refresh the complete group before publishing changed sources.");
                    }
                }
                var loop=group.Loops[i];
                if(loop.Depth is <0 or >12||loop.Parent is {} parent&&(parent<0||parent>=group.Loops.Length||parent==i)||
                    (loop.Depth==0)!=(loop.Parent is null)||loop.Parent is {} p&&group.Loops[p].Depth!=loop.Depth-1||
                    loop.Outputs.IsDefault||loop.Outputs.Length is <1 or >256)
                    throw new CadValidationException("Invalid explicit region containment tree or output boundary budget.");
                for(int j=0;j<loop.Outputs.Length;j++)
                {
                    var output=loop.Outputs[j];
                    if(output is null||output.Geometry is null||output.Endpoints is null)throw new CadValidationException("Missing owned region output.");
                    CadGuard.Id(output.Curve);output.Geometry.Validate();
                    if(!owners.TryAdd(output.Curve,group.Id)||!Enum.IsDefined(output.Role)||output.SourceSlots.IsDefault||
                        output.SourceSlots.Any(slot=>slot<0||slot>=group.Sources[i].Length))throw new CadValidationException("Invalid or multiply owned output correspondence.");
                    var curve=sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==output.Curve)??throw new CadValidationException("Region output author is missing.");
                    if(curve.Source is not null||curve.Endpoints!=output.Endpoints||!curve.Geometry.SameValues(output.Geometry))
                        throw new CadValidationException("Region owns complete output author values and structural endpoint identities.");
                    if(sketch.Points.Where(p=>p.Id==output.Endpoints.Start||p.Id==output.Endpoints.End).Any(p=>p.Origin!=SketchPointOrigin.CurveGenerated))
                        throw new CadValidationException("Region outputs require explicitly generated endpoint identities.");
                    if(output.Role==SketchRegionAuthorRole.Parallel)
                    {
                        if(output.SourceSlots.Length!=1||output.BasisDepth is <1 or >2)throw new CadValidationException("Parallel output needs one explicit complete source prefix.");
                        var basis=output.Geometry;for(int k=0;k<output.BasisDepth;k++)basis=basis.Basis??throw new CadValidationException("Incomplete region source prefix.");
                        if(!basis.SameValues(group.Sources[i][output.SourceSlots[0]].Geometry))throw new CadValidationException("Region output lost its original full source value.");
                        var operation=output.BasisDepth==2?output.Geometry.Basis!:output.Geometry;
                        if(operation.Kind!=SketchCurveExpressionKind.Offset||operation.SignedOffset!=group.Distance*(output.SourceTraversalReversed?-1:1)||
                            output.Geometry.Reversed!=(basis.Reversed^output.SourceTraversalReversed))
                            throw new CadValidationException("Parallel region output has a different distance or direction from its declared operation.");
                    }
                    else
                    {
                        int count=group.Sources[i].Length,step=loop.InputTraversalReversed?-1:1;
                        if(output.SourceSlots.Length!=2||output.SourceSlots[1]!=(output.SourceSlots[0]+step+count)%count||output.BasisDepth!=0||output.Geometry.Basis is not null)
                            throw new CadValidationException("A bridge needs two declared adjacent source slots and its exact primitive definition.");
                    }
                    if(loop.Outputs.Length>1&&output.Endpoints.End!=loop.Outputs[(j+1)%loop.Outputs.Length].Endpoints.Start)
                        throw new CadValidationException("Region neighbours require one explicitly shared generated endpoint identity.");
                }
                SketchRegionJointAuthors.Validate(group,i);
            }
            if(group.Loops.Sum(l=>l.Outputs.Length)>4096)throw new CadValidationException("Region output exceeds the existing total curve budget.");
        }
        var nodes=groups.Select(g=>new SketchRegionDependencyNode(true,g.Id))
            .Concat(sketch.ExpressionCurves.Where(e=>!owners.ContainsKey(e.Id)).Select(e=>new SketchRegionDependencyNode(false,e.Id.Value))).ToArray();
        var dependencies=nodes.ToDictionary(n=>n,_=>new HashSet<SketchRegionDependencyNode>());
        var pointProviders=new Dictionary<SketchEntityId,HashSet<SketchRegionDependencyNode>>();
        foreach(var equation in SketchAssociativeEndpoints.Equations(sketch))
        {
            var owner=Owner(equation.Curve);
            if(!pointProviders.TryGetValue(equation.Point,out var values))pointProviders.Add(equation.Point,values=[]);
            values.Add(owner);
        }
        foreach(var expression in sketch.ExpressionCurves.Where(e=>!owners.ContainsKey(e.Id)&&e.Source is {IsFrozen:false}))
            AddSource(new(false,expression.Id.Value),expression.Source!.Entity);
        foreach(var group in groups.Where(g=>!g.IsFrozen))
            foreach(var source in group.Sources.SelectMany(l=>l))AddSource(new(true,group.Id),source.Entity);
        var degree=dependencies.ToDictionary(p=>p.Key,p=>p.Value.Count);var children=nodes.ToDictionary(n=>n,_=>new List<SketchRegionDependencyNode>());
        foreach(var (node,parents) in dependencies)foreach(var parent in parents)children[parent].Add(node);
        var ready=new Queue<SketchRegionDependencyNode>(nodes.Where(n=>degree[n]==0));var ordered=ImmutableArray.CreateBuilder<SketchRegionDependencyNode>();
        var depths=nodes.ToDictionary(n=>n,_=>1);
        while(ready.TryDequeue(out var node))
        {
            if(depths[node]>64)throw new CadValidationException("Region/expression source author depth exceeds the unchanged author budget.");
            ordered.Add(node);
            foreach(var child in children[node]){depths[child]=Math.Max(depths[child],depths[node]+1);if(--degree[child]==0)ready.Enqueue(child);}
        }
        if(ordered.Count!=nodes.Length)throw new CadValidationException("Region/expression source dependencies contain a self or cross-group cycle, including structural point providers.");
        return ordered.ToImmutable();

        SketchRegionDependencyNode Owner(SketchEntityId curve)=>owners.TryGetValue(curve,out var group)?new(true,group):new(false,curve.Value);
        void AddSource(SketchRegionDependencyNode node,SketchEntityId entity)
        {
            // Also establishes that an unfrozen primitive source still exists.
            _=SketchExpressionEntities.Author(sketch,entity);
            if(sketch.ExpressionCurves.Any(e=>e.Id==entity))dependencies[node].Add(Owner(entity));
            foreach(var point in AuthorPoints(entity))if(pointProviders.TryGetValue(point,out var providers))dependencies[node].UnionWith(providers);
        }
        IEnumerable<SketchEntityId> AuthorPoints(SketchEntityId entity)
        {
            if(sketch.Lines.SingleOrDefault(e=>e.Id==entity) is {} line)return[line.Start,line.End];
            if(sketch.Circles.SingleOrDefault(e=>e.Id==entity) is {} circle)return[circle.Center];
            if(sketch.Arcs.SingleOrDefault(e=>e.Id==entity) is {} arc)return[arc.Start,arc.Middle,arc.End];
            if(sketch.Beziers.SingleOrDefault(e=>e.Id==entity) is {} bezier)return[bezier.Start,bezier.Control,bezier.End];
            if(sketch.Splines.SingleOrDefault(e=>e.Id==entity) is {} spline)return spline.Controls;
            return[];
        }
    }
}
