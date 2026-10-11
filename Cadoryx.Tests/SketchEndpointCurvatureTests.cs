using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchEndpointCurvatureTests
{
    internal static CadSketch Pair(string kind="Spline",bool reverse=false,double scale=1,double angle=0,double translation=0,bool solved=false)
    {
        Point2d T(Point2d p)=>new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Curvature continuity",RigidTransform3d.Identity));d.AddPoint(T(new(0,0)));var joint=d.Value.Points[0].Id;
        d.AddArc(T(new(0,0)),T(new(2,4-Math.Sqrt(12))),T(new(4,4)),existingStart:joint);var a=d.Value.Arcs[0].Id;
        double tangentY=solved?0:-.2;SketchEntityId b;SketchEntityId[] movable;
        if(kind=="Bezier")
        {
            var points=new[]{T(new(0,0)),T(new(-3,tangentY)),T(new(-6,solved?4.5:4.9))};
            if(reverse)Array.Reverse(points);
            d.AddBezier(points[0],points[1],points[2],existingStart:reverse?null:joint,existingEnd:reverse?joint:null);
            var curve=d.Value.Beziers[0];b=curve.Id;movable=[curve.Control,reverse?curve.Start:curve.End];
        }
        else
        {
            bool nonuniform=kind=="Nonuniform";
            Point2d[] points=nonuniform?[T(new(0,0)),T(new(-2,tangentY)),T(new(-4,solved?18:18.4)),T(new(-5,3)),T(new(-6,4))]:
                [T(new(0,0)),T(new(-2,tangentY)),T(new(-4,solved?1.5:1.9)),T(new(-6,4))];
            var geometry=nonuniform?new CubicSplineDefinition(points.ToImmutableArray(),[2,2.25,5],[4,1,4]):CubicSplineDefinition.Uniform(points.ToImmutableArray());
            if(reverse)geometry=geometry.Reverse();
            d.AddSpline(geometry,existingStart:reverse?null:joint,existingEnd:reverse?joint:null);
            var curve=d.Value.Splines[0];b=curve.Id;movable=reverse?[curve.Controls[^2],curve.Controls[^3]]:[curve.Controls[1],curve.Controls[2]];
        }
        // Two scalar coordinates are free in the rotated local normal direction.
        // Rotated tests leave all four coordinates free, preserving two local DOF.
        var relation=new EndpointCurvatureConstraint(SketchConstraintId.New(),a,b,joint);
        d.Change(s=>s with{Constraints=[..s.Points.Where(p=>!movable.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            ..angle==0?movable.Select(id=>(SketchConstraint)new OffsetXConstraint(SketchConstraintId.New(),joint,id,s.Points.Single(p=>p.Id==id).Position.X-translation)):[],relation]});
        return d.Value;
    }

    internal static void RequireG2(CadSketch s)
    {
        foreach(var c in s.Constraints.OfType<EndpointCurvatureConstraint>().Where(c=>c.IsEnabled))
        {
            var a=SketchEndpointCurvature.Evaluate(s,c.A,c.Joint);var b=SketchEndpointCurvature.Evaluate(s,c.B,c.Joint);
            Assert.InRange(Math.Abs(a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X),0,1e-9);
            Assert.True(a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y<-.99999999);
            Assert.InRange(Math.Abs(a.CurvaturePerMm+b.CurvaturePerMm),0,1e-7);
        }
    }

    [Theory]
    [InlineData("Bezier",false,1,0,0)][InlineData("Bezier",true,1,0,1000000)]
    [InlineData("Spline",false,1,0,0)][InlineData("Spline",true,1,0,1000000)]
    [InlineData("Nonuniform",false,1,0,0)][InlineData("Nonuniform",true,1,0,0)]
    [InlineData("Spline",false,.01,0,0)][InlineData("Spline",true,1000,0,0)]
    [InlineData("Nonuniform",true,2,.7,1000000)]
    public async Task ArcAndAuthorCurveSolveBothEndpointsAndNonuniformSpans(string kind,bool reverse,double scale,double angle,double translation)
    {
        var sketch=Pair(kind,reverse,scale,angle,translation);var report=await new ManagedSketchConstraintSolver().SolveAsync(sketch);
        Assert.True(report.Succeeded,$"{report.Diagnostic}; iterations={report.Iterations}; residual={report.MaxNormalizedResidual:R}; {string.Join(';',report.Residuals)}");RequireG2(report.Solution!);
        Assert.Equal(angle==0?0:2,report.DegreesOfFreedom);var c=sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single();
        Assert.Equal(c,report.Solution!.Constraints.OfType<EndpointCurvatureConstraint>().Single());
        Assert.Equal(sketch.Points.Select(p=>p.Id),report.Solution.Points.Select(p=>p.Id));
        foreach(var curve in new[]{c.A,c.B})RequireNativeEndpoint(report.Solution,curve,c.Joint);
        var swapped=sketch with{Constraints=sketch.Constraints.Select(v=>v.Id==c.Id?c with{A=c.B,B=c.A}:v).ToImmutableArray()};
        report=await new ManagedSketchConstraintSolver().SolveAsync(swapped);Assert.True(report.Succeeded,report.Diagnostic);RequireG2(report.Solution!);
    }

    internal static void RequireNativeEndpoint(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);SketchPoint2d P(SketchEntityId id)=>new(points[id].X,points[id].Y);
        SketchCurve2d native;
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==curve) is {} arc)
        {
            var g=SketchArcGeometry.Through(points[arc.Start],points[arc.Middle],points[arc.End]);native=SketchCurve2d.CircularArc(new(g.Center.X,g.Center.Y),g.Radius,g.StartAngle,g.SweepAngle);
        }
        else if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)native=SketchCurve2d.Bezier([P(bezier.Start),P(bezier.Control),P(bezier.End)]);
        else if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is {} spline)
        {
            var g=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
            native=SketchCurve2d.BSpline(g.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,3);
        }
        else {var line=sketch.Lines.Single(l=>l.Id==curve);native=SketchCurve2d.Segment(P(line.Start),P(line.End));}
        bool start=SketchEndpointTangency.Endpoints(sketch,curve).Start==joint;var sampled=native.Evaluate(start?0:1);
        using var edge=SketchModeling.CreateEdge(native,SketchPlane.XY);Assert.True(edge.IsValid);
        var range=edge.GetEdgeCurveSnapshot();
        var exact=edge.EvaluateEdgeDerivatives(start!=native.Reversed?range.FirstParameter:range.LastParameter);var first=exact.FirstDerivative;var second=exact.SecondDerivative;
        Assert.InRange(double.Hypot(exact.Point.X-points[joint].X,exact.Point.Y-points[joint].Y),0,1e-7);
        double direction=(first.X*sampled.FirstDerivative.X+first.Y*sampled.FirstDerivative.Y<0?-1:1)*(start?1:-1);
        double speed=double.Hypot(first.X,first.Y),curvature=direction*(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);
        var managed=SketchEndpointCurvature.Evaluate(sketch,curve,joint);
        Assert.InRange(Math.Abs(curvature-managed.CurvaturePerMm),0,Math.Max(1e-8,Math.Abs(curvature)*1e-7));
        Assert.InRange(double.Hypot(direction*first.X/speed-managed.Tangent.X,direction*first.Y/speed-managed.Tangent.Y),0,1e-7);
    }

    [Theory][InlineData("Bezier",false)][InlineData("Spline",false)][InlineData("Nonuniform",false)][InlineData("Nonuniform",true)]
    public void BothAnalyticRowsMatchEveryIndependentCentralDifferenceColumn(string kind,bool reverse)
    {
        var sketch=Pair(kind,reverse);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        // Derivative verification uses explicit row units to avoid cancellation of
        // near-zero columns after production tolerance weights of up to 1e9.
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3,CurvatureTolerancePerMm:1e-3)],null)!;var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;var residual=type.GetMethod("Residual",flags)!;
        for(int column=0;column<x.Length;column++)
        {
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();const double step=1e-6;plus[column]+=step;minus[column]-=step;
            var a=(double[])residual.Invoke(system,[plus])!;var b=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<a.Length;row++)
            {
                double numeric=(a[row]-b[row])/(2*step);
                Assert.True(Math.Abs(numeric-jac[row,column])<=Math.Max(1,Math.Abs(numeric))*3e-6,$"row={row}; column={column}; numeric={numeric:R}; analytic={jac[row,column]:R}");
            }
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LineAndSplineHaveExactZeroCurvatureWithoutCollapsingTheirRegion(bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Zero curvature",RigidTransform3d.Identity));d.AddLine(new(-6,0),new(0,0));var line=d.Value.Lines[0];
        var g=CubicSplineDefinition.Uniform([new(0,0),new(2,.1),new(4,.6),new(6,4)]);if(reverse)g=g.Reverse();
        d.AddSpline(g,existingStart:reverse?null:line.End,existingEnd:reverse?line.End:null);var s=d.Value.Splines[0];var movable=reverse?new[]{s.Controls[^2],s.Controls[^3]}:new[]{s.Controls[1],s.Controls[2]};
        d.Change(value=>value with{Constraints=[..value.Points.Where(p=>!movable.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            ..movable.Select(id=>(SketchConstraint)new OffsetXConstraint(SketchConstraintId.New(),line.End,id,value.Points.Single(p=>p.Id==id).Position.X)),
            new EndpointCurvatureConstraint(SketchConstraintId.New(),line.Id,s.Id,line.End)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);RequireG2(report.Solution!);
        Assert.InRange(Math.Abs(SketchEndpointCurvature.Evaluate(report.Solution!,s.Id,line.End).CurvaturePerMm),0,1e-7);RequireNativeEndpoint(report.Solution!,s.Id,line.End);
    }

    [Fact] public async Task DisableBudgetRedundancyConflictAndCuspKeepTheOriginalAuthor()
    {
        var s=Pair();var c=s.Constraints.OfType<EndpointCurvatureConstraint>().Single();var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(s);
        Assert.True(report.Succeeded,report.Diagnostic);var solved=report.Solution!;
        Assert.Equal(2,(await solver.SolveAsync(s with{Constraints=s.Constraints.Select(v=>v.Id==c.Id?v with{IsEnabled=false}:v).ToImmutableArray()})).DegreesOfFreedom);
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(s,new(MaxEquations:s.Points.Length*2-1))).Status);
        var redundant=await solver.SolveAsync(solved with{Constraints=solved.Constraints.Add(c with{Id=SketchConstraintId.New()})});Assert.True(redundant.Succeeded);Assert.NotEmpty(redundant.RedundantConstraints);
        var fixedAll=s with{Constraints=[..s.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),c]};Assert.False((await solver.SolveAsync(fixedAll)).Succeeded);
        var b=s.Splines[0];var neighbor=b.Controls[1];var cusp=s with{Points=s.Points.Select(p=>p.Id==neighbor?p with{Position=new(2,0)}:p).ToImmutableArray()};
        Assert.Equal("SKETCH.TANGENCY_BRANCH",(await solver.SolveAsync(cusp)).Diagnostic);
        foreach(bool enabled in new[]{false,true})Assert.Throws<CadValidationException>(()=>(s with{Constraints=[c with{Joint=neighbor,IsEnabled=enabled}]}).Validate());
        Assert.Equal(new Point2d(-2,-.2),s.Points.Single(p=>p.Id==neighbor).Position);
    }

    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void ExactSecondDerivativeUsesActualNonuniformAndRepeatedKnots(int multiplicity)
    {
        var controls=Enumerable.Range(0,multiplicity+4).Select(i=>new Point2d(i,Math.Sin(i))).ToImmutableArray();var g=new CubicSplineDefinition(controls,[2,2.7,5],[4,multiplicity,4]);
        var native=SketchCurve2d.BSpline(controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,3);
        using var edge=SketchModeling.CreateEdge(native,SketchPlane.XY);
        foreach(double t in new[]{0d,.1,(2.7-2)/3,.5,.9,1})
        {
            var a=g.EvaluateDerivatives(t);var b=edge.EvaluateEdgeDerivatives(2+3*t);
            Assert.InRange(double.Hypot(a.Derivative.X-3*b.FirstDerivative.X,a.Derivative.Y-3*b.FirstDerivative.Y),0,1e-8);
            Assert.InRange(double.Hypot(a.SecondDerivative.X-9*b.SecondDerivative.X,a.SecondDerivative.Y-9*b.SecondDerivative.Y),0,1e-7);
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactLineLineAndOpposedArcArcAreSupportedWithoutGuessingJointIdentity(bool arcs)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Exact G2",RigidTransform3d.Identity));d.AddPoint(new(0,0));var joint=d.Value.Points[0].Id;
        SketchEntityId a,b;
        if(arcs)
        {
            d.AddArc(new(0,0),new(2,4-Math.Sqrt(12)),new(4,4),existingStart:joint);
            d.AddArc(new(0,0),new(-2,4-Math.Sqrt(12)),new(-4,4),existingStart:joint);a=d.Value.Arcs[0].Id;b=d.Value.Arcs[1].Id;
        }
        else{d.AddLine(new(0,0),new(3,0),start:joint);d.AddLine(new(0,0),new(-3,0),start:joint);a=d.Value.Lines[0].Id;b=d.Value.Lines[1].Id;}
        d.Change(s=>s with{Constraints=[..s.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),new EndpointCurvatureConstraint(SketchConstraintId.New(),a,b,joint)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);RequireG2(report.Solution!);
        foreach(var curve in new[]{a,b})RequireNativeEndpoint(report.Solution!,curve,joint);
        var detached=d.Value with{Points=d.Value.Points.Add(new(SketchEntityId.New(),new(0,0)))};var foreign=detached.Points[^1].Id;
        foreach(bool enabled in new[]{false,true})Assert.Throws<CadValidationException>(()=>(detached with{Constraints=[new EndpointCurvatureConstraint(SketchConstraintId.New(),a,b,foreign,IsEnabled:enabled)]}).Validate());
    }

    [Fact] public void NonfiniteTrialResidualsDoNotThrowBeforeAdmissibilityRejectsThem()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[Pair(),new SketchSolveOptions()],null)!;var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        x[0]=double.NaN;var residual=(double[])type.GetMethod("Residual",flags)!.Invoke(system,[x])!;
        Assert.Contains(residual,r=>!double.IsFinite(r));Assert.False((bool)type.GetMethod("Admissible",flags)!.Invoke(system,[x])!);
    }
}
