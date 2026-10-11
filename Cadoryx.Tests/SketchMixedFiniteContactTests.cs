using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchMixedFiniteContactTests
{
    internal static (CadSketch Sketch,SketchEntityId Curve,SketchCurve2d Native) Curve(string kind,bool reverse=false)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),kind,RigidTransform3d.Identity));
        if(kind=="Circle")
        {draft.AddCircle(new(3,-2),4);return(draft.Value,draft.Value.Circles[0].Id,SketchCurve2d.Circle(new(3,-2),4));}
        if(kind=="Arc")
        {
            Point2d At(double t)=>new(3+4*Math.Cos(5.7+t*2.1),-2+4*Math.Sin(5.7+t*2.1));
            draft.AddArc(At(reverse?1:0),At(.5),At(reverse?0:1));
            return(draft.Value,draft.Value.Arcs[0].Id,SketchCurve2d.CircularArc(new(3,-2),4,reverse?7.8:5.7,reverse?-2.1:2.1));
        }
        if(kind=="Ellipse")
        {
            var g=new SketchEllipseGeometry(new(3,-2),new(5,2),new(-1,3),.7,3.7);if(reverse)g=g.Reversed();
            var e=new SketchEllipse(SketchEntityId.New(),g);var principal=g.Principal();
            return(draft.Value with{Ellipses=[e]},e.Id,SketchCurve2d.EllipticArc(new(3,-2),principal.MajorRadius,principal.MinorRadius,principal.StartAngle,principal.SweepAngle,principal.AxisAngle));
        }
        if(kind is "RationalBezier" or "RationalSpline" or "Periodic")
        {
            var g=SketchFreeformGeometryTests.Geometry(kind=="RationalBezier"?1:kind=="RationalSpline"?2:3) with{Reversed=reverse};
            var e=new SketchFreeform(SketchEntityId.New(),g);
            var native=g.Kind==SketchFreeformKind.Bezier?SketchCurve2d.Bezier(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Weights):
                SketchCurve2d.BSpline(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,g.Degree,g.Periodic,g.Weights);
            double range=native.LastParameter-native.FirstParameter;
            native=native.Trim((g.FirstParameter-native.FirstParameter)/range,(g.LastParameter-native.FirstParameter)/range);
            if(reverse)native=native.Reverse();return(draft.Value with{Freeforms=[e]},e.Id,native);
        }
        if(kind=="Line")
        {draft.AddLine(new(-4,0),new(4,0));return(draft.Value,draft.Value.Lines[0].Id,SketchCurve2d.Segment(new(-4,0),new(4,0)));}
        if(kind=="Bezier")
        {
            draft.AddBezier(new(reverse?4:-4,0),new(0,4),new(reverse?-4:4,0));
            return(draft.Value,draft.Value.Beziers[0].Id,SketchCurve2d.Bezier([new(reverse?4:-4,0),new(0,4),new(reverse?-4:4,0)]));
        }
        var definition=new CubicSplineDefinition([new(-4,0),new(-2,4),new(0,5),new(2,3),new(4,0)],[2,2.75,5],[4,1,4]);
        if(reverse)definition=definition.Reverse();draft.AddSpline(definition);
        return(draft.Value,draft.Value.Splines[0].Id,SketchCurve2d.BSpline(definition.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),definition.Knots,definition.Multiplicities,3));
    }

    internal static CadSketch Pair(string kind,bool opposite=false,bool perturb=false,FiniteContactContinuity order=FiniteContactContinuity.Curvature)
    {
        var(source,a,_)=Curve(kind);const double t=.37;
        var g=SketchFiniteParametricContact.EvaluateGeometry(source,a,t,2);
        double sign=opposite?-1:1,k=g.CurvaturePerMm*sign;
        if(Math.Abs(k)<1e-8)k=.15; // An unsolved G2 line case still needs a valid Bezier author.
        var tangent=new Point2d(g.Tangent.X*sign,g.Tangent.Y*sign);
        var second=new Point2d(-tangent.Y*k*64,tangent.X*k*64);
        var mid=new Point2d(g.Position.X+second.X/8,g.Position.Y+second.Y/8);
        var draft=new SketchDraft(source);
        draft.AddBezier(new(mid.X-4*tangent.X,mid.Y-4*tangent.Y),
            new(g.Position.X-second.X/8+(perturb?.15:0),g.Position.Y-second.Y/8+(perturb?.12:0)),
            new(mid.X+4*tangent.X,mid.Y+4*tangent.Y));
        var s=draft.Value;var b=s.Beziers[^1].Id;
        var constraints=source.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray();
        if(!source.Circles.IsEmpty)constraints=constraints.Add(new RadiusConstraint(SketchConstraintId.New(),source.Circles[0].Id,source.Circles[0].Radius));
        return s with{Constraints=constraints.Add(new FiniteParametricContactConstraint(SketchConstraintId.New(),a,b,t,.5,
            opposite?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection){Continuity=order})};
    }

    [Theory]
    [InlineData("Circle",false)][InlineData("Arc",false)][InlineData("Arc",true)][InlineData("Ellipse",false)][InlineData("Ellipse",true)]
    [InlineData("RationalBezier",false)][InlineData("RationalBezier",true)][InlineData("RationalSpline",false)][InlineData("RationalSpline",true)]
    [InlineData("Periodic",false)][InlineData("Periodic",true)][InlineData("Line",false)][InlineData("Bezier",false)][InlineData("Bezier",true)]
    [InlineData("Spline",false)][InlineData("Spline",true)]
    public void ExactMixedPositionsFirstSecondAndSignedCurvatureMatchNative(string kind,bool reverse)
    {
        var(s,id,curve)=Curve(kind,reverse);s.Validate();
        using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);Assert.True(edge.IsValid);
        var interval=edge.GetEdgeCurveSnapshot();double range=interval.LastParameter-interval.FirstParameter,direction=curve.Reversed?-1:1;
        foreach(double t in new[]{.07,.25,.37,.5,.75,.91})
        {
            var native=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(curve.Reversed?1-t:t)*range);
            var actual=SketchFiniteParametricContact.EvaluateDerivatives(s,id,t,2);
            Near(actual.Position,new(native.Point.X,native.Point.Y));
            Near(actual.First,new(native.FirstDerivative.X*range*direction,native.FirstDerivative.Y*range*direction));
            Near(actual.Second,new(native.SecondDerivative.X*range*range,native.SecondDerivative.Y*range*range),5e-7);
            double speed=double.Hypot(native.FirstDerivative.X,native.FirstDerivative.Y);
            double curvature=direction*(native.FirstDerivative.X*native.SecondDerivative.Y-native.FirstDerivative.Y*native.SecondDerivative.X)/(speed*speed*speed);
            Assert.InRange(Math.Abs(curvature-SketchFiniteParametricContact.EvaluateGeometry(s,id,t,2).CurvaturePerMm),0,1e-7);
        }
    }

    [Theory]
    [InlineData("Circle",false)][InlineData("Circle",true)][InlineData("Arc",false)][InlineData("Arc",true)]
    [InlineData("Ellipse",false)][InlineData("Ellipse",true)][InlineData("RationalBezier",false)][InlineData("RationalBezier",true)]
    [InlineData("RationalSpline",false)][InlineData("RationalSpline",true)][InlineData("Periodic",false)][InlineData("Periodic",true)]
    [InlineData("Bezier",false)][InlineData("Bezier",true)][InlineData("Spline",false)][InlineData("Spline",true)]
    public async Task MixedInteriorG2SolveKeepsExplicitParametersAndDirectedSignedCurvature(string kind,bool opposite)
    {
        var s=Pair(kind,opposite,true);var report=await new ManagedSketchConstraintSolver().SolveAsync(s);
        Assert.True(report.Succeeded,$"{kind} {opposite}: {report.Diagnostic}, {report.MaxNormalizedResidual:R}");
        var actual=report.Solution!;SketchContactSolutionVerification.Validate(s,actual);SketchFiniteParametricContact.RequireSolvedRelations(actual);
        var c=actual.Constraints.OfType<FiniteParametricContactConstraint>().Single();Assert.Equal(.37,c.ParameterA);Assert.Equal(.5,c.ParameterB);
        Assert.Equal(FiniteContactContinuity.Curvature,c.Continuity);Assert.Equal(s.Ellipses,actual.Ellipses);Assert.Equal(s.Freeforms,actual.Freeforms);
        Assert.Equal(s.Points.Select(p=>p.Id),actual.Points.Select(p=>p.Id));
    }

    [Theory]
    [InlineData("Circle",false)][InlineData("Circle",true)][InlineData("Arc",false)][InlineData("Arc",true)]
    [InlineData("Ellipse",false)][InlineData("Ellipse",true)][InlineData("RationalSpline",false)][InlineData("RationalSpline",true)]
    [InlineData("Bezier",false)][InlineData("Spline",true)][InlineData("Line",false)]
    public void FourAnalyticJacobianRowsMatchEveryIndependentDifferenceColumn(string kind,bool opposite)
    {
        var s=Pair(kind,opposite,true);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[s,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3,CurvatureTolerancePerMm:1e-3)],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            const double step=1e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=step;minus[col]-=step;
            var p=(double[])residual.Invoke(system,[plus])!;var m=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<jac.RowCount;row++)
                Assert.InRange(Math.Abs((p[row]-m[row])/(2*step)-jac[row,col]),0,Math.Max(2e-5,Math.Abs(jac[row,col])*2e-6));
        }
    }

    [Fact]
    public async Task G2CountsFourRowsRejectsWrongOrderAndCannotPassWithOnlyG1()
    {
        var s=Pair("Circle");var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var limited=await new ManagedSketchConstraintSolver().SolveAsync(s with{Constraints=[c]},new(MaxEquations:3));
        Assert.Equal(SketchSolveStatus.LimitExceeded,limited.Status);
        Assert.Throws<CadValidationException>(()=>(s with{Constraints=[c with{Continuity=(FiniteContactContinuity)99,IsEnabled=false}]}).Validate());
        var wrong=s with{Constraints=s.Constraints.Select(v=>v.Id==c.Id?c with{Branch=FiniteContactTangentBranch.OppositeDirection}:v).ToImmutableArray()};
        Assert.False((await new ManagedSketchConstraintSolver().SolveAsync(wrong)).Succeeded);
        var b=s.Beziers.Single();var control=s.Points.Single(p=>p.Id==b.Control);var g=SketchFiniteParametricContact.EvaluateGeometry(s,c.A,c.ParameterA);
        // Change acceleration while keeping the contact position and tangent exactly.
        var shift=new Point2d(control.Position.X-g.Position.X,control.Position.Y-g.Position.Y);
        s=s with{Points=s.Points.Select(p=>p.Id==b.Control?p with{Position=new(p.Position.X+shift.X,p.Position.Y+shift.Y)}:
            p.Id==b.Start||p.Id==b.End?p with{Position=new(p.Position.X-shift.X,p.Position.Y-shift.Y)}:p).ToImmutableArray()};
        var g1=s with{Constraints=s.Constraints.Select(v=>v.Id==c.Id?c with{Continuity=FiniteContactContinuity.Tangent}:v).ToImmutableArray()};
        SketchFiniteParametricContact.RequireSolvedRelations(g1);
        Assert.Throws<CadValidationException>(()=>SketchFiniteParametricContact.RequireSolvedRelations(s));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void CopiedSplineKnotAndPeriodicSeamMustProveRequestedDerivativeOrder(bool periodic)
    {
        var g=periodic?SketchFreeformGeometryTests.Geometry(4) with{FirstParameter=0,LastParameter=3}:
            new SketchFreeformGeometry(SketchFreeformKind.BSpline,[new(0,0),new(1,2),new(2,3),new(3,3),new(4,2),new(5,0)],[],[0,.5,1],[4,2,4],3,false,0,1);
        double t=periodic?1d/3:.5;g.RequireDerivativeContinuity(t,1);
        Assert.Throws<CadValidationException>(()=>g.RequireDerivativeContinuity(t,2));
        g.RequireDerivativeContinuity(t-1e-5,2);g.RequireDerivativeContinuity(t+1e-5,2);
        if(periodic)
        {
            var shifted=g with{FirstParameter=.2,LastParameter=3.2};
            double seam=(3-.2)/3;shifted.RequireDerivativeContinuity(seam,1);
            Assert.Throws<CadValidationException>(()=>shifted.RequireDerivativeContinuity(seam,2));
        }
    }

    [Fact]
    public async Task FixedExactConicsCanVerifyWithoutInventingSolverVariables()
    {
        var(s,a,_)=Curve("Ellipse");var original=s.Ellipses[0];var b=original with{Id=SketchEntityId.New()};
        s=s with{Ellipses=s.Ellipses.Add(b),Constraints=[new FiniteParametricContactConstraint(SketchConstraintId.New(),a,b.Id,.37,.37,FiniteContactTangentBranch.SameDirection){Continuity=FiniteContactContinuity.Curvature}]};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(s);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(0,report.VariableCount);Assert.Equal(4,report.EquationCount);Assert.Equal(0,report.Rank);
        Assert.Equal(s.Ellipses,report.Solution!.Ellipses);
    }

    internal static void Near(Point2d actual,Point2d expected,double tolerance=1e-7)=>
        Assert.InRange(double.Hypot(actual.X-expected.X,actual.Y-expected.Y),0,tolerance);
}
