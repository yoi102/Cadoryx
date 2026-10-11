using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchAssociativeEndpointTests
{
    internal static (SketchDraft Draft,SketchEntityId Curve) Source(int kind,bool reverse=false)
    {
        var(draft,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),kind);var id=draft.AddExactOffset(source,.03);
        if(reverse)draft.EditExpression(id,draft.Value.ExpressionCurves.Single(c=>c.Id==id).Geometry.Reverse());
        draft.AttachExpressionEndpoints(id);return(draft,id);
    }
    public static IEnumerable<object[]> Sources()=>from kind in Enumerable.Range(0,8) from reverse in new[]{false,true} select new object[]{kind,reverse};
    [Theory][MemberData(nameof(Sources))]
    public async Task StableStructuralEquationsMoveEndpointVariablesWithoutCapturedFixedAnchors(int kind,bool reverse)
    {
        var(d,id)=Source(kind,reverse);var initial=d.Value;var c=initial.ExpressionCurves.Single(c=>c.Id==id);var e=c.Endpoints!;
        Assert.DoesNotContain(initial.Constraints,v=>v.Id==e.StartEquation||v.Id==e.EndEquation);
        var point=initial.Points.Single(p=>p.Id==e.Start);d.MovePoint(e.Start,new(point.Position.X+.08,point.Position.Y-.04));
        var source=d.Value;var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);
        var solved=result.Solution!;Assert.Equal(e,solved.ExpressionCurves.Single(c=>c.Id==id).Endpoints);
        Assert.Equal(4+source.Constraints.Sum(c=>c is FixPointConstraint?2:1),result.EquationCount);
        SketchAssociativeEndpoints.RequireSolved(solved);SketchExpressionEntities.ValidateSolved(source,solved);
        SketchContactSolutionVerification.Validate(source,solved);
        var kernel=new OcctGeometryKernel();foreach(var pair in new[]{(e.Start,0d),(e.End,1d)})
        {var actual=solved.Points.Single(p=>p.Id==pair.Item1).Position;Near(actual,kernel.EvaluateExpression(solved.ExpressionCurves.Single(c=>c.Id==id).Geometry,pair.Item2).Point);}
    }
    internal static void Near(Point2d a,Point2d b,double tolerance=1e-7)=>Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,tolerance);
    internal static CadSketch Pair(int kind,bool reverse,bool start,bool curvature,bool pinB=false)
    {
        var(d,id)=Source(kind,reverse);var c=d.Value.ExpressionCurves.Single(c=>c.Id==id);var e=c.Endpoints!;double parameter=start?0:1;
        var g=c.Geometry.Evaluate(parameter);double speed=double.Hypot(g.First.X,g.First.Y),sign=start?-1:1;
        var t=new Point2d(sign*g.First.X/speed,sign*g.First.Y/speed);double k=sign*(g.First.X*g.Second.Y-g.First.Y*g.Second.X)/(speed*speed*speed);
        var p=g.Point;d.AddBezier(p,new(p.X+4*t.X,p.Y+4*t.Y),new(p.X+8*t.X-32*k*t.Y,p.Y+8*t.Y+32*k*t.X),existingStart:start?e.Start:e.End);
        var s=d.Value;var b=s.Beziers[^1];var endpointIds=new[]{e.Start,e.End};
        var points=pinB?s.Points.Where(p=>!endpointIds.Contains(p.Id)):s.Points.Where(p=>!endpointIds.Contains(p.Id)&&p.Id!=b.Control&&p.Id!=b.End);
        var constraints=points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray();
        constraints=constraints.AddRange(s.Circles.Select(c=>(SketchConstraint)new RadiusConstraint(SketchConstraintId.New(),c.Id,c.Radius)));
        constraints=constraints.Add(curvature?new EndpointCurvatureConstraint(SketchConstraintId.New(),id,b.Id,start?e.Start:e.End):
            new EndpointTangentConstraint(SketchConstraintId.New(),id,b.Id,start?e.Start:e.End));
        return s with{Constraints=constraints};
    }
    public static IEnumerable<object[]> Joins()=>from kind in Enumerable.Range(1,6) from reverse in new[]{false,true}
        from start in new[]{false,true} from curvature in new[]{false,true} select new object[]{kind,reverse,start,curvature};
    [Theory][MemberData(nameof(Joins))]
    public async Task DirectedOneSidedStartAndEndG1G2UseRealSourceAndStableJoint(int kind,bool reverse,bool start,bool curvature)
    {
        var source=Pair(kind,reverse,start,curvature,true);source.Validate();
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,$"{kind}/{reverse}/{start}/{curvature}: {result.Diagnostic}; {result.MaxNormalizedResidual:R}");
        var solved=result.Solution!;SketchContactSolutionVerification.Validate(source,solved);SketchAssociativeEndpoints.RequireSolved(solved);
        var a=solved.ExpressionCurves.Single();var b=solved.Beziers[^1];var native=new OcctGeometryKernel().EvaluateExpression(a.Geometry,start?0:1);
        double speed=double.Hypot(native.First.X,native.First.Y),sign=start?1:-1;var joint=start?a.Endpoints!.Start:a.Endpoints!.End;
        var second=SketchEndpointCurvature.Evaluate(solved,b.Id,joint);
        Assert.InRange(Math.Abs(sign*(native.First.X*second.Tangent.Y-native.First.Y*second.Tangent.X)/speed),0,1e-9);
        Assert.True(sign*(native.First.X*second.Tangent.X+native.First.Y*second.Tangent.Y)/speed<0);
        if(curvature)Assert.InRange(Math.Abs(sign*(native.First.X*native.Second.Y-native.First.Y*native.Second.X)/(speed*speed*speed)+second.CurvaturePerMm),0,1e-7);
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void AllStructuralAndEndpointContactJacobianColumnsMatchIndependentCoordinatePerturbations(int kind)
    {
        var sketch=Pair(kind,true,true,true);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3,CurvatureTolerancePerMm:1e-3)],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            const double h=1e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
            var p=(double[])residual.Invoke(system,[plus])!;var m=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<jac.RowCount;row++)Assert.InRange(Math.Abs((p[row]-m[row])/(2*h)-jac[row,col]),0,Math.Max(3e-5,Math.Abs(jac[row,col])*3e-6));
        }
    }
    [Fact] public async Task StructuralEquationsCountTowardUnchangedBudgetsAndAnalysisCannotSkipThem()
    {
        var(d,id)=Source(5);var source=d.Value;var e=source.ExpressionCurves.Single().Endpoints!;
        source=source with{Points=source.Points.Select(p=>p.Id==e.Start?p with{Position=new(p.Position.X+.1,p.Position.Y)}:p).ToImmutableArray()};
        var solver=new ManagedSketchConstraintSolver();var analysis=solver.Analyze(source);Assert.Equal(4,analysis.EquationCount);Assert.False(analysis.IsSatisfied);
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(source,new(MaxEquations:3))).Status);
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(source,new(MaxVariables:3))).Status);
        var result=await solver.SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);Assert.Equal(4,result.EquationCount);Assert.Equal(0,result.DegreesOfFreedom);
    }
    [Fact] public void SourceThroughOwnDerivedEndpointIsAnExplicitCycleAndLeavesDraftUntouched()
    {
        var(d,id)=Source(0);var old=d.Value;var e=old.ExpressionCurves.Single().Endpoints!;var line=old.Lines[0];
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Lines=s.Lines.Replace(line,line with{Start=e.Start})}));Assert.Same(old,d.Value);
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{ExpressionCurves=[s.ExpressionCurves.Single() with{Endpoints=e with{StartEquation=s.Constraints.FirstOrDefault()?.Id??e.EndEquation}}]}));
        Assert.Same(old,d.Value);
    }
}
