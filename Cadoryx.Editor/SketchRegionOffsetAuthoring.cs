using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Creates one complete region author and all generated endpoints in one edit.
/// Subsequent source changes require the group evaluator, never independent segment refresh.</summary>
public static class SketchRegionOffsetAuthoring
{
    public sealed record Result(CadSketch Sketch,Guid Group);
    public static Result Create(CadSketch sketch,IEnumerable<SketchEntityId> seeds,double distance,SketchRegionAuthorJoin join,IExactSketchExpressionRegions kernel)
    {
        var inputs=SketchExpressionRegionSelection.Select(sketch,seeds);
        var result=kernel.OffsetExpressionRegions(inputs,distance,join switch
        {SketchRegionAuthorJoin.Miter=>ExactExpressionRegionJoin.Miter,SketchRegionAuthorJoin.Round=>ExactExpressionRegionJoin.Round,
            SketchRegionAuthorJoin.Bevel=>ExactExpressionRegionJoin.Bevel,_=>throw new CadValidationException("Unknown region author join.")});
        if(result.OutwardDistance!=distance||result.Loops.IsDefault||result.Loops.Length!=inputs.Length)
            throw new CadValidationException("Region provider changed the authored distance or loop count.");
        var points=sketch.Points.ToBuilder();var curves=sketch.ExpressionCurves.ToBuilder();var loops=ImmutableArray.CreateBuilder<SketchRegionLoopAuthor>();
        for(int i=0;i<result.Loops.Length;i++)
        {
            var loop=result.Loops[i];if(loop.SourceLoopIndex!=i||loop.Segments.IsDefault||loop.Segments.Length is <1 or >256)
                throw new CadValidationException("Region provider changed source correspondence or boundary budget.");
            var vertices=Enumerable.Range(0,loop.Segments.Length==1?2:loop.Segments.Length).Select(_=>SketchEntityId.New()).ToArray();
            for(int j=0;j<vertices.Length;j++)points.Add(new(vertices[j],loop.Segments.Length==1?loop.Segments[0].Geometry.At(j):loop.Segments[j].Geometry.At(0))
                {Origin=SketchPointOrigin.CurveGenerated});
            var outputs=ImmutableArray.CreateBuilder<SketchRegionOutputAuthor>();
            for(int j=0;j<loop.Segments.Length;j++)
            {
                var segment=loop.Segments[j];var id=SketchEntityId.New();var ends=new SketchExpressionEndpoints(vertices[j],vertices[(j+1)%vertices.Length],SketchConstraintId.New(),SketchConstraintId.New());
                curves.Add(new(id,segment.Geometry,null,false){Endpoints=ends});
                outputs.Add(new(id,ends,(SketchRegionAuthorRole)segment.Kind,segment.OriginalCurveIndices,segment.BasisDepth,segment.SourceTraversalReversed,segment.Geometry));
            }
            loops.Add(new(loop.Depth,loop.ParentLoopIndex,loop.InputTraversalReversed,outputs.ToImmutable()){Joints=SketchRegionJointCorrespondence.Copy(loop.Joints)});
        }
        var group=new SketchRegionOffsetAuthor(Guid.NewGuid(),inputs.Select(l=>l.Select(s=>new SketchRegionSourceAuthor(s.Id,SketchExpressionEntities.Author(sketch,s.Id).Kind,
            s.Geometry,s.Reversed)).ToImmutableArray()).ToImmutableArray(),distance,join,loops.ToImmutable()){Algorithm=SketchRegionJointAuthors.CurrentAlgorithm};
        var candidate=sketch with{Points=points.ToImmutable(),ExpressionCurves=curves.ToImmutable(),RegionOffsets=sketch.RegionOffsets.Add(group)};
        candidate.Validate();SketchAssociativeEndpoints.RequireSolved(candidate);
        // The independent verifier replays the operation and checks every role,
        // value and source slot, including providers supplied by another caller.
        if(kernel is not ISketchExpressionRegionVerifier verifier)throw new CadValidationException("Region authoring requires exact complete replay verification.");
        verifier.ConfirmExpressionRegions(candidate);return new(candidate,group.Id);
    }
}
