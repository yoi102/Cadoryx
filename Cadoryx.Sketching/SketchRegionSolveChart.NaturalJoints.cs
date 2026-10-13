using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed partial class SketchRegionSolveChart
{
    // This proves one owned natural boundary in a mixed loop only. It must never select or defer
    // an entire mixed loop, authorize native intersections/bridges, or derive
    // identity from coincident coordinates. Full native refresh remains required.
    internal static bool ProvesNaturalBoundary(CadSketch sketch,SketchRegionOffsetAuthor group,
        int loopIndex,int sourceSlot,bool start)
    {
        if(group.IsFrozen||group.Algorithm!=SketchRegionJointAuthors.CurrentAlgorithm||
            sketch.RegionOffsets.Count(g=>g.Id==group.Id)!=1||
            !ReferenceEquals(sketch.RegionOffsets.First(g=>g.Id==group.Id),group)||
            loopIndex<0||loopIndex>=group.Loops.Length||loopIndex>=group.Sources.Length)return false;
        var sources=group.Sources[loopIndex];var loop=group.Loops[loopIndex];
        if(sources.IsDefault||sources.Length<2||sourceSlot<0||sourceSlot>=sources.Length||
            loop.Outputs.IsDefault||loop.Joints.IsDefault||loop.Joints.Length!=sources.Length||
            !loop.Joints.Any(j=>j is not null&&j.Kind!=SketchRegionJointKind.SourceEndpoints))return false;
        var matches=loop.Joints.Where(j=>j is not null&&(start?j.NextSourceSlot:j.PreviousSourceSlot)==sourceSlot).ToArray();
        if(matches.Length!=1||matches[0].Kind!=SketchRegionJointKind.SourceEndpoints)return false;
        var joint=matches[0];int previous=joint.PreviousSourceSlot,next=joint.NextSourceSlot;
        int step=loop.InputTraversalReversed?-1:1;
        if(previous<0||previous>=sources.Length||next!=(previous+step+sources.Length)%sources.Length||
            joint.PreviousSource!=sources[previous].Entity||joint.NextSource!=sources[next].Entity||
            joint.PreviousTraversalReversed!=(sources[previous].Reversed^loop.InputTraversalReversed)||
            joint.NextTraversalReversed!=(sources[next].Reversed^loop.InputTraversalReversed))return false;
        var before=loop.Outputs.Select((o,i)=>(o,i)).Where(p=>p.o.Role==SketchRegionAuthorRole.Parallel&&p.o.SourceSlots.SequenceEqual(new[]{previous})).ToArray();
        var after=loop.Outputs.Select((o,i)=>(o,i)).Where(p=>p.o.Role==SketchRegionAuthorRole.Parallel&&p.o.SourceSlots.SequenceEqual(new[]{next})).ToArray();
        if(before.Length!=1||after.Length!=1||after[0].i!=(before[0].i+1)%loop.Outputs.Length||
            before[0].o.Endpoints.End!=after[0].o.Endpoints.Start||
            before[0].o.SourceTraversalReversed!=joint.PreviousTraversalReversed||
            after[0].o.SourceTraversalReversed!=joint.NextTraversalReversed)return false;
        var p=sources[previous].Geometry;var q=sources[next].Geometry;
        if(joint.PreviousParameter!=(p.Reversed^joint.PreviousTraversalReversed?p.FirstParameter:p.LastParameter)||
            joint.NextParameter!=(q.Reversed^joint.NextTraversalReversed?q.LastParameter:q.FirstParameter))return false;
        foreach(var output in new[]{before[0].o,after[0].o})
        {
            var expression=sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==output.Curve);
            if(expression is null||expression.Source is not null||expression.Endpoints!=output.Endpoints||
                !expression.Geometry.SameValues(output.Geometry))return false;
        }
        (SketchEntityId Start,SketchEntityId End) a,b;
        try{a=SketchEndpointTangency.Endpoints(sketch,joint.PreviousSource);b=SketchEndpointTangency.Endpoints(sketch,joint.NextSource);}
        catch(CadValidationException){return false;}
        var end=joint.PreviousTraversalReversed?a.Start:a.End;
        var beginning=joint.NextTraversalReversed?b.End:b.Start;
        if(end!=beginning||new[]{a.Start,a.End}.Intersect(new[]{b.Start,b.End}).Count()!=1)return false;
        return sketch.Constraints.Any(c=>c.IsEnabled&&(c switch
        {
            EndpointTangentConstraint t=>Matches(t.A,t.B,t.Joint,t.Branch),
            EndpointCurvatureConstraint t=>Matches(t.A,t.B,t.Joint,t.Branch),
            _=>false
        }));
        bool Matches(SketchEntityId x,SketchEntityId y,SketchEntityId point,EndpointTangentBranch branch)
            =>branch==EndpointTangentBranch.Opposed&&point==end&&
              (x==joint.PreviousSource&&y==joint.NextSource||y==joint.PreviousSource&&x==joint.NextSource);
    }
}
