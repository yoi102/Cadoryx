using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Explicit complete author-slot mapping. This is not native edge/face history.</summary>
public static class SketchRegionSourceReselection
{
    public sealed class Prepared
    {
        public CadSketch Original {get;}
        public CadSketch Candidate {get;}
        internal Prepared(CadSketch original,CadSketch candidate){Original=original;Candidate=candidate;}
    }
    public static Prepared Prepare(CadSketch sketch,Guid id,ImmutableArray<ImmutableArray<SketchEntityId>> slots,IExactSketchExpressionRegions kernel,CancellationToken token=default)
        =>new(sketch,Apply(sketch,id,slots,kernel,token));
    public static CadSketch Apply(CadSketch sketch,Guid id,ImmutableArray<ImmutableArray<SketchEntityId>> slots,
        IExactSketchExpressionRegions kernel,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        sketch.Validate();var original=sketch.RegionOffsets.SingleOrDefault(g=>g.Id==id)??
            throw new CadValidationException("Select a complete region author group.");
        if(slots.IsDefault||slots.Length!=original.Sources.Length||slots.Where((l,i)=>l.IsDefault||l.Length!=original.Sources[i].Length).Any())
            throw new CadValidationException("Explicitly identify every old source loop and slot; do not infer a partial correspondence.");
        var ids=slots.SelectMany(l=>l).ToArray();
        if(ids.Length>4096||ids.Any(v=>v.Value==Guid.Empty)||ids.Distinct().Count()!=ids.Length)
            throw new CadValidationException("Replacement source identities must be complete, unique and within the original source budget.");
        var owned=original.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve).ToHashSet();
        if(ids.Any(owned.Contains))throw new CadValidationException("A region cannot use its own result as replacement source.");
        // One declared seed per loop must reconstruct exactly the complete ordered
        // slot map. Endpoint branches and undeclared additional authors are refused.
        var inputs=SketchExpressionRegionSelection.Select(sketch,slots.Select(l=>l[0]));
        if(inputs.Length!=slots.Length||inputs.Where((l,i)=>!l.Select(v=>v.Id).SequenceEqual(slots[i])).Any())
            throw new CadValidationException("Replacement authors do not prove the declared complete directed loops.");
        var sources=inputs.Select((l,i)=>l.Select((v,j)=>
        {
            var author=SketchExpressionEntities.Author(sketch,v.Id);
            return new SketchRegionSourceAuthor(v.Id,author.Kind,v.Geometry,v.Reversed);
        }).ToImmutableArray()).ToImmutableArray();
        RequireAcyclicSources(sketch,id,ids);
        var context=new SketchRegionEvaluationContext(kernel);
        var intent=original with{Sources=sources,IsFrozen=false,FrozenReason=null};
        var computed=kernel.OffsetExpressionRegions(inputs,intent.Distance,(ExactExpressionRegionJoin)intent.Join);
        token.ThrowIfCancellationRequested();
        // Retain output IDs only after every loop, role, slot and containment
        // is proved against the explicitly supplied new authors.
        if(computed.Loops.IsDefault||computed.Loops.Length!=intent.Loops.Length)
            throw new CadValidationException("Explicit source mapping changed the complete output loop count.");
        // Direction changes are explicit source edits. Retain IDs only at the
        // same declared role/slot, never by coordinates or native topology.
        var loops=intent.Loops.Select((loop,i)=>
        {
            var actual=computed.Loops[i];
            if(actual.Segments.IsDefault||actual.Segments.Length!=loop.Outputs.Length)
                throw new CadValidationException("Source mapping changed output roles or count; create a new complete correspondence.");
            return loop with{InputTraversalReversed=actual.InputTraversalReversed,
                Joints=SketchRegionJointCorrespondence.Copy(actual.Joints),
                Outputs=loop.Outputs.Select((output,j)=>output with{SourceTraversalReversed=actual.Segments[j].SourceTraversalReversed}).ToImmutableArray()};
        }).ToImmutableArray();
        intent=intent with{Loops=loops,Algorithm=SketchRegionJointAuthors.CurrentAlgorithm};
        var updated=SketchRegionOutputCorrespondence.ReplaceDerivedValues(intent,sources,computed);
        var outputs=updated.Loops.SelectMany(l=>l.Outputs).ToDictionary(o=>o.Curve);
        var candidate=sketch with{RegionOffsets=sketch.RegionOffsets.Replace(original,updated),
            ExpressionCurves=sketch.ExpressionCurves.Select(c=>outputs.TryGetValue(c.Id,out var o)?c with{Geometry=o.Geometry}:c).ToImmutableArray()};
        return context.Refresh(candidate,token,synchronizeGeneratedPoints:true);
    }
    private static void RequireAcyclicSources(CadSketch sketch,Guid changed,IReadOnlyCollection<SketchEntityId> replacements)
    {
        var owners=sketch.RegionOffsets.SelectMany(g=>g.Loops.SelectMany(l=>l.Outputs).Select(o=>(o.Curve,g.Id))).ToDictionary(p=>p.Curve,p=>p.Id);
        var groups=sketch.RegionOffsets.ToDictionary(g=>g.Id);var expressions=sketch.ExpressionCurves.ToDictionary(c=>c.Id.Value);
        var active=new HashSet<(bool Group,Guid Id)>();var visited=new HashSet<(bool Group,Guid Id)>();
        void Source(SketchEntityId source,int depth)
        {
            if(owners.TryGetValue(source,out var owner))Visit((true,owner),depth);
            else if(expressions.ContainsKey(source.Value))Visit((false,source.Value),depth);
        }
        void Visit((bool Group,Guid Id) node,int depth)
        {
            if(depth>64)throw new CadValidationException("Replacement sources exceed the existing author depth budget.");
            if(!active.Add(node))throw new CadValidationException("Replacement sources introduce an author cycle.");
            try
            {
                if(visited.Contains(node))return;
                if(node.Group)
                {
                    var group=groups[node.Id];
                    if(node.Id==changed)foreach(var id in replacements)Source(id,depth+1);
                    else if(!group.IsFrozen)foreach(var id in group.Sources.SelectMany(l=>l).Select(v=>v.Entity))Source(id,depth+1);
                }
                else if(expressions[node.Id].Source is {IsFrozen:false} source)Source(source.Entity,depth+1);
                visited.Add(node);
            }
            finally{active.Remove(node);}
        }
        Visit((true,changed),0);
    }
}
