using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public void ChangeRegionDistance(Guid id,double distance)=>Change(s=>
        (regionEvaluation??throw new NotSupportedException("Whole-region distance changes require exact evaluation.")).ChangeDistance(s,id,distance),true);
    public Guid OffsetExpressionRegions(IEnumerable<SketchEntityId> seeds,double distance,SketchRegionAuthorJoin join,IExactSketchExpressionRegions kernel)
    {
        regionEvaluation??=new(kernel);Guid id=Guid.Empty;
        Change(s=>{var result=SketchRegionOffsetAuthoring.Create(s,seeds,distance,join,kernel);id=result.Group;return result.Sketch;});return id;
    }
    public void FreezeRegion(Guid id,string reason)=>Change(s=>
    {
        CadGuard.Name(reason);var g=Group(s,id);return s with{RegionOffsets=s.RegionOffsets.Replace(g,g with{IsFrozen=true,FrozenReason=reason})};
    },true);
    public void ResumeRegion(Guid id)=>Change(s=>
    {
        var g=Group(s,id);return s with{RegionOffsets=s.RegionOffsets.Replace(g,g with{IsFrozen=false,FrozenReason=null})};
    },true);
    public void DetachRegion(Guid id)=>Change(s=>s with{RegionOffsets=s.RegionOffsets.Remove(Group(s,id))},true);
    public void RemoveRegion(Guid id)=>Change(s=>
    {
        var g=Group(s,id);var curves=g.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve).ToHashSet();
        var points=g.Loops.SelectMany(l=>l.Outputs).SelectMany(o=>new[]{o.Endpoints.Start,o.Endpoints.End}).ToHashSet();
        var next=s with{RegionOffsets=s.RegionOffsets.Remove(g),ExpressionCurves=s.ExpressionCurves.Where(c=>!curves.Contains(c.Id)).ToImmutableArray()};
        var orphaned=SketchPointUsage.GeneratedOrphans(next,points);
        var removed=curves.Union(orphaned).ToHashSet();
        return next with{Points=next.Points.Where(p=>!orphaned.Contains(p.Id)).ToImmutableArray(),
            Constraints=next.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)).ToImmutableArray()};
    },true);
    private static SketchRegionOffsetAuthor Group(CadSketch s,Guid id)=>s.RegionOffsets.SingleOrDefault(g=>g.Id==id)??
        throw new CadValidationException("Select a whole region author group.");
    private static void RequireUnchangedRegionOutputs(CadSketch before,CadSketch after)
    {
        foreach(var g in before.RegionOffsets)
        {
            var current=after.RegionOffsets.SingleOrDefault(v=>v.Id==g.Id);
            if(current is null||!SketchRegionAuthorModel.SameValues(g,current))
                throw new CadValidationException("Change, freeze, detach or remove the complete region group explicitly.");
            foreach(var o in g.Loops.SelectMany(l=>l.Outputs))
            {
                var old=before.ExpressionCurves.Single(c=>c.Id==o.Curve);var next=after.ExpressionCurves.SingleOrDefault(c=>c.Id==o.Curve);
                if(next is null||!SketchExpressionEntities.SameValues(old,next))
                    throw new CadValidationException("Detach the complete region before editing one of its output segments.");
                foreach(var id in new[]{o.Endpoints.Start,o.Endpoints.End})
                    if(before.Points.Single(p=>p.Id==id)!=after.Points.SingleOrDefault(p=>p.Id==id))
                        throw new CadValidationException("Region-generated endpoints are driven by the complete group.");
            }
        }
    }
}
