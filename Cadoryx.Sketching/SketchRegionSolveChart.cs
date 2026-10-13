using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

/// <summary>One solve owns these immutable author proofs. All structural and
/// enabled relation rows remain in the original bounded equation system.</summary>
internal sealed partial class SketchRegionSolveChart
{
    private readonly ImmutableDictionary<Guid,SketchRegionOffsetAuthor> groups;
    internal ImmutableHashSet<Guid> GroupIds {get;}
    internal bool IsComplete {get;}
    internal SketchRegionPointCouplingProof? PointCouplingProof {get;}
    internal bool IsPointCoupled=>PointCouplingProof is not null;
    internal SketchRegionLivePointCouplingProof? LivePointCouplingProof {get;}
    internal bool IsLivePointCoupled=>LivePointCouplingProof is not null;
    private SketchRegionSolveChart(ImmutableDictionary<Guid,SketchRegionOffsetAuthor> groups,int activeCount,SketchRegionPointCouplingProof? pointCouplingProof=null,SketchRegionLivePointCouplingProof? livePointCouplingProof=null)
    {this.groups=groups;GroupIds=groups.Keys.ToImmutableHashSet();IsComplete=groups.Count==activeCount;PointCouplingProof=pointCouplingProof;LivePointCouplingProof=livePointCouplingProof;}
    internal bool Contains(SketchRegionOffsetAuthor group)=>groups.TryGetValue(group.Id,out var original)&&ReferenceEquals(original,group);

    // A mixed solve may defer complete natural groups while retaining the native
    // intersection/bridge route for other independent groups. A loop mixing
    // natural and native joints is still outside this chart's proof scope.
    internal static SketchRegionSolveChart? TryCreate(CadSketch sketch)
    {
        var active=sketch.RegionOffsets.Where(g=>!g.IsFrozen).ToArray();
        if(active.Length==0)return null;
        var selected=ImmutableDictionary.CreateBuilder<Guid,SketchRegionOffsetAuthor>();bool hasNatural=false;
        foreach(var group in active)
            if(ProveGroup(sketch,group,out bool natural)){selected.Add(group.Id,group);hasNatural|=natural;}
        if(!hasNatural)return null;
        if(selected.Count!=active.Length)
        {
            // Native groups cannot consume a deferred group's stale value, or
            // vice versa. Include indirect expression and generated-point
            // providers in the dependency check; coordinates are not identities.
            var owners=active.SelectMany(g=>g.Loops.SelectMany(l=>l.Outputs).Select(o=>(o.Curve,g.Id)))
                .ToDictionary(p=>p.Curve,p=>p.Id);
            var providers=SketchAssociativeEndpoints.Equations(sketch).GroupBy(e=>e.Point)
                .ToDictionary(g=>g.Key,g=>g.Select(e=>e.Curve).Distinct().ToArray());
            try
            {
                foreach(var group in active)
                foreach(var source in group.Sources.SelectMany(l=>l))
                    if(Dependencies(source.Entity,[],0).Any(id=>selected.ContainsKey(group.Id)!=selected.ContainsKey(id)))return null;
            }
            catch(CadValidationException){return null;}

            HashSet<Guid> Dependencies(SketchEntityId entity,HashSet<SketchEntityId> path,int depth)
            {
                if(depth>=SketchCurveExpressionGeometry.MaximumDepth||!path.Add(entity))
                    throw new CadValidationException("Mixed equation dependency proof exceeded the original author depth or contains a cycle.");
                try
                {
                    if(owners.TryGetValue(entity,out var owner))return [owner];
                    var result=new HashSet<Guid>();
                    if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==entity) is {} expression)
                    {
                        if(expression.Source is {IsFrozen:false} source)result.UnionWith(Dependencies(source.Entity,path,depth+1));
                        return result;
                    }
                    foreach(var point in SourcePoints(entity))
                        if(providers.TryGetValue(point,out var curves))
                            foreach(var curve in curves)result.UnionWith(Dependencies(curve,path,depth+1));
                    return result;
                }
                finally{path.Remove(entity);}
            }
            IEnumerable<SketchEntityId> SourcePoints(SketchEntityId entity)
            {
                if(sketch.Lines.SingleOrDefault(e=>e.Id==entity) is {} line)return [line.Start,line.End];
                if(sketch.Circles.SingleOrDefault(e=>e.Id==entity) is {} circle)return [circle.Center];
                if(sketch.Arcs.SingleOrDefault(e=>e.Id==entity) is {} arc)return [arc.Start,arc.Middle,arc.End];
                if(sketch.Beziers.SingleOrDefault(e=>e.Id==entity) is {} bezier)return [bezier.Start,bezier.Control,bezier.End];
                if(sketch.Splines.SingleOrDefault(e=>e.Id==entity) is {} spline)return spline.Controls;
                if(SketchFixedCurveEndpoints.All(sketch).SingleOrDefault(e=>e.Curve==entity).Ends is {} ends)return [ends.Start,ends.End];
                return [];
            }
        }
        return new(selected.ToImmutable(),active.Length);


    }
    private static bool ProveGroup(CadSketch sketch,SketchRegionOffsetAuthor group,out bool natural)
    {
        natural=false;
        if(group.Algorithm!=SketchRegionJointAuthors.CurrentAlgorithm)return false;
        for(int i=0;i<group.Loops.Length;i++)
        {
            var loop=group.Loops[i];var sources=group.Sources[i];
            if(loop.Outputs.Any(o=>o.Role!=SketchRegionAuthorRole.Parallel))return false;
            if(sources.Length==1){if(!loop.Joints.IsEmpty)return false;continue;}
            if(loop.Joints.Length!=sources.Length)return false;
            foreach(var joint in loop.Joints)
            {
                if(joint.Kind!=SketchRegionJointKind.SourceEndpoints)return false;
                (SketchEntityId Start,SketchEntityId End) before,after;
                try{before=SketchEndpointTangency.Endpoints(sketch,joint.PreviousSource);after=SketchEndpointTangency.Endpoints(sketch,joint.NextSource);}
                catch(CadValidationException){return false;}
                var end=joint.PreviousTraversalReversed?before.Start:before.End;
                var start=joint.NextTraversalReversed?after.End:after.Start;
                if(end!=start||new[]{before.Start,before.End}.Intersect(new[]{after.Start,after.End}).Count()!=1)return false;
                bool proof=sketch.Constraints.Any(c=>c.IsEnabled&&(c switch
                {
                    EndpointTangentConstraint t=>Matches(t.A,t.B,t.Joint,t.Branch),
                    EndpointCurvatureConstraint t=>Matches(t.A,t.B,t.Joint,t.Branch),
                    _=>false
                }));
                if(!proof)return false;
                natural=true;
                bool Matches(SketchEntityId a,SketchEntityId b,SketchEntityId point,EndpointTangentBranch branch)
                    =>branch==EndpointTangentBranch.Opposed&&point==end&&
                      (a==joint.PreviousSource&&b==joint.NextSource||b==joint.PreviousSource&&a==joint.NextSource);
            }
        }
        return true;
    }
}
