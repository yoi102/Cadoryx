using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public static class SketchRegionJointCorrespondence
{
    public static ImmutableArray<SketchRegionJointAuthor> Copy(ImmutableArray<ExactExpressionRegionJoint> joints)
    {
        if(joints.IsDefault)throw new CadValidationException("Missing complete creating-operation region connection witness table.");
        return joints.Select(j=>j is null?throw new CadValidationException("Missing owned region connection witness."):
            new SketchRegionJointAuthor(j.PreviousSourceSlot,j.NextSourceSlot,j.PreviousSource,j.NextSource,j.Kind switch
            {
                ExactExpressionRegionJointKind.SourceEndpoints=>SketchRegionJointKind.SourceEndpoints,
                ExactExpressionRegionJointKind.NativeIntersection=>SketchRegionJointKind.NativeIntersection,
                ExactExpressionRegionJointKind.RoundBridge=>SketchRegionJointKind.RoundBridge,
                ExactExpressionRegionJointKind.BevelBridge=>SketchRegionJointKind.BevelBridge,
                _=>throw new CadValidationException("Unknown creating-operation region connection branch.")
            },j.PreviousParameter,j.NextParameter,j.PreviousTraversalReversed,j.NextTraversalReversed)).ToImmutableArray();
    }
}
