using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSlidingContactSolveTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach(string kind in new[]{"Circle","Arc","Ellipse","RationalBezier","RationalSpline","Periodic","Bezier","Spline"})
        foreach(bool opposite in new[]{false,true})
        foreach(var continuity in new[]{FiniteContactContinuity.Tangent,FiniteContactContinuity.Curvature})
        foreach(var mode in Enum.GetValues<FiniteContactParameterMode>())yield return[kind,opposite,continuity,mode];
    }
    internal static CadSketch Pair(string kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode,bool pinAll=true)
    {
        var sketch=SketchMixedFiniteContactTests.Pair(kind,opposite,false,continuity);
        var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        c=c with{ParameterMode=mode};
        if(c.SolvesA)c=c with{ParameterA=.34,SlidingA=new(.34,.2,.6)};
        if(c.SolvesB)c=c with{ParameterB=.46,SlidingB=new(.46,.3,.7)};
        var constraints=sketch.Constraints.Select(v=>v.Id==c.Id?c:v).ToImmutableArray();
        if(pinAll)
        {
            var fixedPoints=constraints.OfType<FixPointConstraint>().Select(p=>p.Point).ToHashSet();
            constraints=constraints.AddRange(sketch.Points.Where(p=>!fixedPoints.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)));
        }
        return sketch with{Constraints=constraints};
    }
    [Theory][MemberData(nameof(Cases))]
    public async Task ExplicitModesSolveDirectedG1G2WithImmutableCopiedGeometryAndAllAuthorsPreserved(string kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        var authored=Pair(kind,opposite,continuity,mode);authored.Validate();
        var report=await new ManagedSketchConstraintSolver().SolveAsync(authored);
        Assert.True(report.Succeeded,$"{kind}/{opposite}/{continuity}/{mode}: {report.Diagnostic}, iteration={report.Iterations}, residual={report.MaxNormalizedResidual:R}");
        var solved=report.Solution!;SketchContactSolutionVerification.Validate(authored,solved);
        var original=authored.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var actual=solved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Assert.Equal(authored.Points.Length*2+authored.Circles.Length+actual.ParameterVariableCount,report.VariableCount);
        Assert.Equal(original.SlidingA,actual.SlidingA);Assert.Equal(original.SlidingB,actual.SlidingB);
        if(!actual.SolvesA)Assert.Equal(original.ParameterA,actual.ParameterA);
        if(!actual.SolvesB)Assert.Equal(original.ParameterB,actual.ParameterB);
        Assert.Equal(authored.Ellipses,solved.Ellipses);Assert.Equal(authored.Freeforms,solved.Freeforms);
        Assert.Equal(authored.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        foreach(var p in authored.Points)SketchMixedFiniteContactTests.Near(p.Position,solved.Points.Single(q=>q.Id==p.Id).Position);
        var(_,_,native)=SketchMixedFiniteContactTests.Curve(kind);
        VerifyNative(solved,actual.A,actual.ParameterA,native);
        var b=solved.Beziers.Single(v=>v.Id==actual.B);
        SketchPoint2d P(SketchEntityId id){var p=solved.Points.Single(p=>p.Id==id).Position;return new(p.X,p.Y);}
        VerifyNative(solved,actual.B,actual.ParameterB,SketchCurve2d.Bezier([P(b.Start),P(b.Control),P(b.End)]));
    }
    private static void VerifyNative(CadSketch sketch,SketchEntityId id,double t,SketchCurve2d definition)
    {
        using var edge=SketchModeling.CreateEdge(definition,SketchPlane.XY);
        var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter,sign=definition.Reversed?-1:1;
        var j=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(definition.Reversed?1-t:t)*range);
        var g=SketchFiniteParametricContact.EvaluateGeometry(sketch,id,t,2);double speed=double.Hypot(j.FirstDerivative.X,j.FirstDerivative.Y);
        SketchMixedFiniteContactTests.Near(g.Position,new(j.Point.X,j.Point.Y));
        SketchMixedFiniteContactTests.Near(g.Tangent,new(sign*j.FirstDerivative.X/speed,sign*j.FirstDerivative.Y/speed));
        double curvature=sign*(j.FirstDerivative.X*j.SecondDerivative.Y-j.FirstDerivative.Y*j.SecondDerivative.X)/Math.Pow(speed,3);
        Assert.InRange(Math.Abs(g.CurvaturePerMm-curvature),0,1e-7);
    }
    [Theory][InlineData("Circle",false)][InlineData("Arc",true)][InlineData("Ellipse",false)][InlineData("RationalSpline",true)]
    [InlineData("Bezier",false)][InlineData("Spline",true)]
    public void EntireContactSystemJacobianIncludesBothBoundedParametersAndEveryControlColumn(string kind,bool opposite)
    {
        var sketch=Pair(kind,opposite,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth,false);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3,CurvatureTolerancePerMm:1e-3)],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            const double h=1e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
            var p=(double[])residual.Invoke(system,[plus])!;var m=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<jac.RowCount;row++)Assert.InRange(Math.Abs((p[row]-m[row])/(2*h)-jac[row,col]),0,Math.Max(2e-5,Math.Abs(jac[row,col])*2e-6));
        }
    }
    [Fact]
    public async Task ZeroPointExactCurvesCanSolveBothParametersAndKeepCopiesFixed()
    {
        var(s,id,_)=SketchMixedFiniteContactTests.Curve("Ellipse");var copy=s.Ellipses.Single() with{Id=SketchEntityId.New()};
        s=s with{Ellipses=s.Ellipses.Add(copy),Constraints=[new FiniteParametricContactConstraint(SketchConstraintId.New(),id,copy.Id,.34,.46,FiniteContactTangentBranch.SameDirection)
            {Continuity=FiniteContactContinuity.Curvature,ParameterMode=FiniteContactParameterMode.SolveBoth,SlidingA=new(.34,.2,.6),SlidingB=new(.46,.3,.7)}]};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(s);Assert.True(result.Succeeded,result.Diagnostic);
        Assert.Equal(2,result.VariableCount);Assert.Equal(4,result.EquationCount);Assert.Equal(s.Ellipses,result.Solution!.Ellipses);
        SketchContactSolutionVerification.Validate(s,result.Solution);
    }
    [Fact]
    public async Task SlidingVariablesCountTowardTheOriginalBudgetAndCancellationPublishesNothing()
    {
        var s=Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth);
        int baseCount=s.Points.Length*2+s.Circles.Length;
        var result=await new ManagedSketchConstraintSolver().SolveAsync(s,new(MaxVariables:baseCount+1));
        Assert.Equal(SketchSolveStatus.LimitExceeded,result.Status);Assert.Null(result.Solution);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ManagedSketchConstraintSolver().SolveAsync(s,cancellationToken:cancel.Token));
        Assert.Equal(.34,s.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);
    }
    [Fact]
    public async Task AContactOutsideTheAllowedIntervalCannotSucceedOrClipToTheBoundary()
    {
        var s=Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveA);var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        s=s with{Constraints=s.Constraints.Replace(c,c with{ParameterA=.22,SlidingA=new(.22,.2,.24)})};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(s);Assert.False(result.Succeeded);Assert.Null(result.Solution);
        Assert.Equal(.22,s.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);
    }
    [Theory][InlineData("mode")][InlineData("seed")][InlineData("bounds")][InlineData("branch")][InlineData("identity")][InlineData("fixed-parameter")]
    public async Task VerificationRejectsChangedAuthorsEvenWhenTheReturnedGeometryLooksSolved(string change)
    {
        var source=Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveA);
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);
        var c=result.Solution!.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var changed=change switch
        {
            "mode"=>c with{ParameterMode=FiniteContactParameterMode.Fixed,SlidingA=null},
            "seed"=>c with{SlidingA=c.SlidingA! with{Seed=.35}},"bounds"=>c with{SlidingA=c.SlidingA! with{Upper=.61}},
            "branch"=>c with{Branch=FiniteContactTangentBranch.OppositeDirection},"identity"=>c with{A=c.B,B=c.A},_=>c with{ParameterB=.51}
        };
        Assert.Throws<CadValidationException>(()=>SketchContactSolutionVerification.Validate(source,result.Solution with{Constraints=result.Solution.Constraints.Replace(c,changed)}));
    }
}
