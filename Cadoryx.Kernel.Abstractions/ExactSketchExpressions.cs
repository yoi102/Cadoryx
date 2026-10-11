using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum ExactExpressionRegularityState { Certified,Folded,NotCertified }
public sealed record ExactExpressionRegularity(ExactExpressionRegularityState State,double FirstParameter,double LastParameter,
    int ExpressionNodeIndex,int IntervalsChecked,double MinimumTangentialScale,string Diagnostic)
{ public bool IsCertified=>State==ExactExpressionRegularityState.Certified; }

/// <summary>Copied expression values cross this boundary; kernel owners stay inside the adapter.
/// Certification proves finite regularity only, not injectivity or native history identity.</summary>
public interface IExactSketchExpressionGeometry
{
    ExactExpressionRegularity InspectExpressionRegularity(SketchCurveExpressionGeometry expression);
    SketchCurveExpressionGeometry PrepareExpression(SketchCurveExpressionGeometry expression);
    SketchExpressionEvaluation EvaluateExpression(SketchCurveExpressionGeometry expression,double parameter);
    ImmutableArray<ExactSketchProjection> ProjectExpression(SketchCurveExpressionGeometry expression,Point2d point);
    ImmutableArray<ExactSketchIntersection> IntersectExpressions(SketchCurveExpressionGeometry first,SketchCurveExpressionGeometry second);
    ExactRegionInspection InspectExpressionRegion(ImmutableArray<SketchCurveExpressionGeometry> directedBoundary);
    GeometryResult CreateExpressionEdge(SketchCurveExpressionGeometry expression,RigidTransform3d plane,IAssetStore assets);
}
