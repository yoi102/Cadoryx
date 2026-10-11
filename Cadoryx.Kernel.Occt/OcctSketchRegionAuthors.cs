using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:ISketchExpressionRegionVerifier
{
    public void ConfirmExpressionRegions(CadSketch sketch,CancellationToken cancellationToken=default)
    {
        sketch.Validate();
        foreach(var group in sketch.RegionOffsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources=group.Sources.Select(l=>l.Select(s=>new ExactExpressionRegionSource(s.Entity,s.Geometry,s.Reversed)).ToImmutableArray()).ToImmutableArray();
            var result=OffsetExpressionRegions(sources,group.Distance,group.Join switch
            {SketchRegionAuthorJoin.Miter=>ExactExpressionRegionJoin.Miter,SketchRegionAuthorJoin.Round=>ExactExpressionRegionJoin.Round,
                SketchRegionAuthorJoin.Bevel=>ExactExpressionRegionJoin.Bevel,_=>throw new CadValidationException("Unknown region join.")});
            if(result.OutwardDistance!=group.Distance||result.Loops.Length!=group.Loops.Length)
                throw new CadValidationException("Stored whole-region author differs from its exact replay.");
            for(int i=0;i<result.Loops.Length;i++)
            {
                var a=group.Loops[i];var b=result.Loops[i];
                if(b.SourceLoopIndex!=i||a.Depth!=b.Depth||a.Parent!=b.ParentLoopIndex||a.InputTraversalReversed!=b.InputTraversalReversed||
                    a.Outputs.Length!=b.Segments.Length)throw new CadValidationException("Stored region containment or output correspondence differs from exact replay.");
                if(group.Algorithm==SketchRegionJointAuthors.CurrentAlgorithm&&
                    !SketchRegionJointAuthors.SameValues(a.Joints,SketchRegionJointCorrespondence.Copy(b.Joints)))
                    throw new CadValidationException("Stored region creating-operation connection branch or source parameters differ from exact replay.");
                for(int j=0;j<a.Outputs.Length;j++)
                {
                    var x=a.Outputs[j];var y=b.Segments[j];
                    if((int)x.Role!=(int)y.Kind||x.BasisDepth!=y.BasisDepth||x.SourceTraversalReversed!=y.SourceTraversalReversed||
                        !x.SourceSlots.SequenceEqual(y.OriginalCurveIndices)||!y.SourceIds.SequenceEqual(x.SourceSlots.Select(slot=>group.Sources[i][slot].Entity))||
                        !x.Geometry.SameValues(y.Geometry))throw new CadValidationException("Stored region output value or source role differs from exact replay.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
