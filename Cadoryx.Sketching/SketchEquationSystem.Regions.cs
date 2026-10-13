using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed partial class SketchEquationSystem
{
    // Ordinary intersection/bridge trials share the complete native candidate.
    // A proven natural chart uses immutable author metadata plus the independent
    // solver coordinates. Independent native groups may share an equation-only
    // candidate. Explicitly coupled groups consume current generated variables
    // through their declared point providers; every structural row remains.
    // Deferred chart geometry is never derived off the join manifold.
    // Trial generated endpoint variables remain solver coordinates; overwriting
    // them here would erase structural residuals and their independent columns.
    private CadSketch EvaluationSketch(double[] x)
    {
        if(regionContext is null||regionChart is {IsComplete:true})return sketch;
        regionToken.ThrowIfCancellationRequested();
        if(evaluatedCoordinates is not null&&x.AsSpan().SequenceEqual(evaluatedCoordinates))return evaluatedSketch!;
        var result=regionChart is null?regionContext.Refresh(Apply(x),regionToken,verify:false):
            regionChart.IsLivePointCoupled?regionContext.RefreshForDerivedLocalEquations(Apply(x),regionChart.GroupIds,regionChart.DeriveLocalAuthors,regionToken):
            regionContext.RefreshForLocalEquations(Apply(x),regionChart.GroupIds,regionToken);
        evaluatedCoordinates=(double[])x.Clone();evaluatedSketch=result;return result;
    }
}
