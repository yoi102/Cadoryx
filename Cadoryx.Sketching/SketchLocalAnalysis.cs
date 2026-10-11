using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Sketching;

public enum SketchAnalysisStatus { Evaluated, InvalidInput, LimitExceeded, InvalidEvaluation }

/// <summary>Local linearization at the current authored coordinates and actual contact parameters.
/// Nullity at an unsatisfied point is not a solved degree of freedom or proof of global uniqueness.</summary>
public sealed record SketchLocalAnalysis(SketchAnalysisStatus Status,bool? IsSatisfied,int? DegreesOfFreedom,int? Rank,
    int VariableCount,int EquationCount,double? MaxNormalizedResidual,
    ImmutableArray<SketchConstraintResidual> Residuals,string Diagnostic);

public sealed partial class ManagedSketchConstraintSolver
{
    /// <summary>Read-only, zero-iteration evaluation. Never replaces actual parameters with their seeds.</summary>
    public SketchLocalAnalysis Analyze(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
    {
        var token=cancellationToken;token.ThrowIfCancellationRequested();options??=new();options.Validate();
        try{sketch.Validate();}
        catch(ArgumentException ex){return Failure(SketchAnalysisStatus.InvalidInput,ex.Message,0,0);}
        int n=CountVariables(sketch),m=CountEquations(sketch);
        // Match the solver's existing cheap path for unconstrained author curves.
        if(m==0)return new(SketchAnalysisStatus.Evaluated,true,n,0,n,0,0,[],"SKETCH.LOCAL_DOF");
        if(n>options.MaxVariables||m>options.MaxEquations)
            return Failure(SketchAnalysisStatus.LimitExceeded,"Dense solver budget exceeded.",n,m);
        try
        {
            SketchRegionEvaluationContext? regionContext=null;
            if(!sketch.RegionOffsets.IsEmpty)
            {
                if(regions is null)return Failure(SketchAnalysisStatus.InvalidInput,"SKETCH.REGION_BACKEND_REQUIRED",n,m);
                regionContext=new(regions,regionVerifier);regionContext.Refresh(sketch,token);
            }
            var system=new SketchEquationSystem(sketch,options,regionContext,token);
            var coordinates=system.Coordinates(sketch);
            token.ThrowIfCancellationRequested();
            var residual=system.Residual(coordinates);
            if(!residual.All(double.IsFinite))return Failure(SketchAnalysisStatus.InvalidEvaluation,"SKETCH.SINGULAR_EVALUATION",n,m);
            var jacobian=system.Jacobian(coordinates,token);
            if(!Finite(jacobian))return Failure(SketchAnalysisStatus.InvalidEvaluation,"SKETCH.SINGULAR_EVALUATION",n,m);
            token.ThrowIfCancellationRequested();int rank=Rank(jacobian);token.ThrowIfCancellationRequested();
            double max=Max(residual);bool satisfied=max<=1&&system.Admissible(coordinates);
            if(satisfied)
            {
                try
                {
                    SketchFiniteParametricContact.RequireSolvedRelations(sketch,options.LinearToleranceMm,options.AngularTolerance,options.CurvatureTolerancePerMm);
                    SketchAssociativeEndpoints.RequireSolved(sketch,options.LinearToleranceMm);
                }
                catch(CadValidationException){satisfied=false;}
                foreach(var relation in sketch.Constraints.OfType<CircularContactConstraint>().Where(c=>c.IsEnabled))
                {
                    token.ThrowIfCancellationRequested();
                    if(!SketchCircularContact.TryContactPoint(sketch,relation,options.LinearToleranceMm,out _,out _))satisfied=false;
                }
            }
            var groups=system.Groups.Select(g=>new SketchConstraintResidual(g.Constraint.Id,Max(residual.Skip(g.Start).Take(g.Count)))).ToImmutableArray();
            // Rank remains useful at an unsatisfied point, but cannot describe a solution's freedom.
            return new(SketchAnalysisStatus.Evaluated,satisfied,satisfied?n-rank:null,rank,n,m,max,groups,
                satisfied?"SKETCH.LOCAL_DOF":"SKETCH.CURRENT_RESIDUALS_UNSATISFIED");
        }
        catch(NotSupportedException ex){return Failure(SketchAnalysisStatus.LimitExceeded,ex.Message,n,m);}
        catch(CadValidationException ex){return Failure(SketchAnalysisStatus.InvalidEvaluation,ex.Message,n,m);}

        static SketchLocalAnalysis Failure(SketchAnalysisStatus status,string diagnostic,int variables,int equations)
            =>new(status,null,null,null,variables,equations,null,[],diagnostic);
    }

    private static int CountVariables(CadSketch sketch)=>sketch.Points.Length*2+sketch.Circles.Length+
        sketch.Constraints.OfType<FiniteParametricContactConstraint>().Where(c=>c.IsEnabled).Sum(c=>c.ParameterVariableCount);
    private static int CountEquations(CadSketch sketch)=>sketch.Constraints.Where(c=>c.IsEnabled).Sum(c=>c switch
    {
        FiniteParametricContactConstraint f=>f.Continuity==FiniteContactContinuity.Curvature?4:3,
        FixPointConstraint or CoincidentConstraint or MidpointConstraint or SymmetryConstraint or EndpointCurvatureConstraint=>2,
        _=>1
    })+2*SketchAssociativeEndpoints.Equations(sketch).Count();
}
