using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum ExactExpressionRegionJoin { Miter,Round,Bevel }
public enum ExactExpressionRegionSegmentKind { Parallel,RoundBridge,BevelBridge }
public enum ExactExpressionRegionJointKind { SourceEndpoints,NativeIntersection,RoundBridge,BevelBridge }

/// <summary>Creating-operation connection witness in original source slots and
/// author native parameter units. It does not prove a maintained solver chart
/// or native edge/face history. A single closed support has no corner witnesses.</summary>
public sealed record ExactExpressionRegionJoint(int PreviousSourceSlot,int NextSourceSlot,
    SketchEntityId PreviousSource,SketchEntityId NextSource,ExactExpressionRegionJointKind Kind,
    double PreviousParameter,double NextParameter,bool PreviousTraversalReversed,bool NextTraversalReversed);

/// <summary>Explicit original author value and traversal. Reversal never changes source identity.</summary>
public sealed record ExactExpressionRegionSource(SketchEntityId Id,SketchCurveExpressionGeometry Geometry,bool Reversed=false);
/// <summary>Original input indices are restored after native winding normalization.
/// BasisDepth identifies the full source prefix for a parallel segment only.
/// These are author correspondences, never native edge/face history.</summary>
public sealed record ExactExpressionRegionSegment(SketchCurveExpressionGeometry Geometry,ExactExpressionRegionSegmentKind Kind,
    ImmutableArray<int> OriginalCurveIndices,ImmutableArray<SketchEntityId> SourceIds,int BasisDepth,bool SourceTraversalReversed);
public sealed record ExactExpressionRegionLoop(int SourceLoopIndex,int Depth,int? ParentLoopIndex,bool InputTraversalReversed,
    double OutwardLoopDistance,ImmutableArray<ExactExpressionRegionSegment> Segments)
{public ImmutableArray<ExactExpressionRegionJoint> Joints {get;init;}=[];}
public sealed record ExactExpressionRegionOffset(ImmutableArray<ExactExpressionRegionLoop> Loops,double OutwardDistance,double FilledArea);

public interface IExactSketchExpressionRegions
{
    ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,
        double outwardDistance,ExactExpressionRegionJoin join);
}

public interface ISketchExpressionRegionVerifier
{
    void ConfirmExpressionRegions(CadSketch sketch,CancellationToken cancellationToken=default);
}
