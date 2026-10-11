using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchLocalAnalysisTests
{
    [Theory][InlineData(FiniteContactParameterMode.SolveA)][InlineData(FiniteContactParameterMode.SolveB)][InlineData(FiniteContactParameterMode.SolveBoth)]
    public async Task CurrentActualParametersMatchSolvedLocalRankWithoutMovingSeedsOrGeometry(FiniteContactParameterMode mode)
    {
        var solver=new ManagedSketchConstraintSolver();var authored=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,mode);
        var result=await solver.SolveAsync(authored);Assert.True(result.Succeeded,result.Diagnostic);var solved=result.Solution!;
        var constraints=solved.Constraints;var points=solved.Points;
        var analysis=solver.Analyze(solved);
        Assert.Equal(SketchAnalysisStatus.Evaluated,analysis.Status);Assert.True(analysis.IsSatisfied);
        Assert.Equal(result.VariableCount,analysis.VariableCount);Assert.Equal(result.EquationCount,analysis.EquationCount);
        Assert.Equal(result.Rank,analysis.Rank);Assert.Equal(result.DegreesOfFreedom,analysis.DegreesOfFreedom);
        Assert.InRange(analysis.MaxNormalizedResidual!.Value,0,1);
        Assert.Equal(result.Residuals.Select(r=>r.ConstraintId),analysis.Residuals.Select(r=>r.ConstraintId));
        Assert.All(analysis.Residuals,r=>Assert.InRange(r.NormalizedResidual,0,1));
        var repeated=solver.Analyze(solved);Assert.Equal(analysis.Rank,repeated.Rank);
        Assert.Equal(analysis.MaxNormalizedResidual,repeated.MaxNormalizedResidual);Assert.Equal(analysis.Residuals.ToArray(),repeated.Residuals.ToArray());
        Assert.Equal(constraints,solved.Constraints);Assert.Equal(points,solved.Points);
        var atSeeds=solved with{Constraints=solved.Constraints.Select(c=>c is FiniteParametricContactConstraint f?
            f with{ParameterA=f.SlidingA?.Seed??f.ParameterA,ParameterB=f.SlidingB?.Seed??f.ParameterB}:c).ToImmutableArray()};
        var unsatisfied=solver.Analyze(atSeeds);Assert.Equal(SketchAnalysisStatus.Evaluated,unsatisfied.Status);
        Assert.False(unsatisfied.IsSatisfied);Assert.Null(unsatisfied.DegreesOfFreedom);Assert.NotNull(unsatisfied.Rank);
        Assert.True(unsatisfied.MaxNormalizedResidual>1);
    }

    [Fact]
    public async Task UnsolvedAuthorIsReportedWithoutAttemptingToSatisfyConstraints()
    {
        var sketch=SketchTestData.Rectangle();var solver=new ManagedSketchConstraintSolver();
        var initial=solver.Analyze(sketch);Assert.Equal(SketchAnalysisStatus.Evaluated,initial.Status);
        Assert.False(initial.IsSatisfied);Assert.Null(initial.DegreesOfFreedom);Assert.True(initial.MaxNormalizedResidual>1);
        var solved=await solver.SolveAsync(sketch);Assert.True(solved.Succeeded);
        var satisfied=solver.Analyze(solved.Solution!);Assert.True(satisfied.IsSatisfied);Assert.Equal(0,satisfied.DegreesOfFreedom);
        Assert.Equal(8,satisfied.Rank);Assert.Equal(1,sketch.Points[0].Position.X);
    }

    [Fact]
    public void AutomaticVariablesAndEquationsUseOriginalBudgetsAndFiniteJsonDiagnostics()
    {
        var solver=new ManagedSketchConstraintSolver();var s=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth);
        int n=s.Points.Length*2+s.Circles.Length;
        var variables=solver.Analyze(s,new(MaxVariables:n+1));Assert.Equal(SketchAnalysisStatus.LimitExceeded,variables.Status);
        Assert.Equal(n+2,variables.VariableCount);Assert.Null(variables.Rank);Assert.Null(variables.MaxNormalizedResidual);
        var equations=solver.Analyze(s,new(MaxEquations:1));Assert.Equal(SketchAnalysisStatus.LimitExceeded,equations.Status);
        Assert.Null(equations.DegreesOfFreedom);Assert.Null(equations.IsSatisfied);
        Assert.Contains("LimitExceeded",System.Text.Json.JsonSerializer.Serialize(equations,new System.Text.Json.JsonSerializerOptions{Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>solver.Analyze(s,cancellationToken:canceled.Token));
    }

    [Fact]
    public void DisabledAutomaticRelationsAddNoVariablesAndLargeUnconstrainedAuthorsNeedNoDenseSystem()
    {
        var solver=new ManagedSketchConstraintSolver();var s=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth);
        s=s with{Constraints=s.Constraints.Select(c=>c with{IsEnabled=false}).ToImmutableArray()};
        var result=solver.Analyze(s);Assert.Equal(s.Points.Length*2+s.Circles.Length,result.VariableCount);
        Assert.Equal(result.VariableCount,result.DegreesOfFreedom);Assert.Equal(0,result.Rank);
        var large=SketchTestData.Empty() with{Points=Enumerable.Range(0,300).Select(i=>new SketchPoint(SketchEntityId.New(),new(i,i%7))).ToImmutableArray()};
        var analysis=solver.Analyze(large);Assert.True(analysis.IsSatisfied);Assert.Equal(600,analysis.DegreesOfFreedom);
    }

    [Theory][InlineData(FiniteContactParameterMode.Fixed)][InlineData(FiniteContactParameterMode.SolveBoth)]
    public async Task ZeroPointCopiedCurvesReportLocalNullityIncludingRedundantG2Rows(FiniteContactParameterMode mode)
    {
        var(sketch,curve,_)=SketchMixedFiniteContactTests.Curve("Ellipse");var copy=sketch.Ellipses.Single() with{Id=SketchEntityId.New()};
        var contact=new FiniteParametricContactConstraint(SketchConstraintId.New(),curve,copy.Id,.37,.37,FiniteContactTangentBranch.SameDirection)
            {Continuity=FiniteContactContinuity.Curvature,ParameterMode=mode};
        if(mode==FiniteContactParameterMode.SolveBoth)
            contact=contact with{ParameterA=.34,ParameterB=.46,SlidingA=new(.34,.2,.6),SlidingB=new(.46,.3,.7)};
        sketch=sketch with{Ellipses=sketch.Ellipses.Add(copy),Constraints=[contact]};
        var solver=new ManagedSketchConstraintSolver();var solved=await solver.SolveAsync(sketch);Assert.True(solved.Succeeded,solved.Diagnostic);
        var analysis=solver.Analyze(solved.Solution!);Assert.True(analysis.IsSatisfied);Assert.Equal(4,analysis.EquationCount);
        Assert.Equal(mode==FiniteContactParameterMode.Fixed?0:2,analysis.VariableCount);
        Assert.Equal(mode==FiniteContactParameterMode.Fixed?0:1,analysis.Rank);
        Assert.Equal(mode==FiniteContactParameterMode.Fixed?0:1,analysis.DegreesOfFreedom);
        Assert.Equal(solved.Rank,analysis.Rank);Assert.Equal(solved.DegreesOfFreedom,analysis.DegreesOfFreedom);
    }
}
