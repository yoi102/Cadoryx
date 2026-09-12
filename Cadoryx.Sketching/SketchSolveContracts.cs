using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

public enum SketchSolveStatus { UnderConstrained, FullyConstrained, Inconsistent, DidNotConverge, InvalidInput, LimitExceeded }
public sealed record SketchSolveOptions(double LinearToleranceMm=1e-7,double AngularTolerance=1e-9,int MaxIterations=80,
    int MaxVariables=256,int MaxEquations=512)
{
    public void Validate()
    {
        CadGuard.Positive(LinearToleranceMm,AngularTolerance);
        if(MaxIterations is <1 or >500||MaxVariables is <1 or >512||MaxEquations is <1 or >1024||AngularTolerance>1e-3)
            throw new CadValidationException("Invalid sketch solve limits.");
    }
}
public sealed record SketchConstraintResidual(SketchConstraintId ConstraintId,double NormalizedResidual);
public sealed record SketchSolveReport(SketchSolveStatus Status,CadSketch? Solution,int? DegreesOfFreedom,int? Rank,
    int VariableCount,int EquationCount,int Iterations,double MaxNormalizedResidual,
    ImmutableArray<SketchConstraintId> ConflictingConstraints,ImmutableArray<SketchConstraintId> RedundantConstraints,
    ImmutableArray<SketchConstraintResidual> Residuals,string Diagnostic)
{
    public bool Succeeded=>Solution is not null&&Status is SketchSolveStatus.UnderConstrained or SketchSolveStatus.FullyConstrained;
}
public interface ISketchConstraintSolver
{
    string Version {get;}
    Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default);
}
