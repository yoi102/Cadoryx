using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchFiniteParametricContactSolveTests
{
    internal static CadSketch Pair(string kind="Bezier",bool reverse=false,double scale=1,double angle=0,double translation=0)
    {
        Point2d T(Point2d p)=>new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Interior contact",RigidTransform3d.Identity));
        d.AddBezier(T(new(-4,0)),T(new(0,4)),T(new(4,0)));var a=d.Value.Beziers[0].Id;
        double ta=.37,tb=.61;var source=SketchFiniteParametricContact.Evaluate(d.Value,a,ta);
        if(kind=="Line")d.AddLine(new(-3,0),new(3,0));
        else if(kind=="Bezier")d.AddBezier(new(-3,0),new(0,2),new(3,0));
        else
        {
            var geometry=kind=="Nonuniform"?new CubicSplineDefinition([new(-3,0),new(-2,2),new(0,2.5),new(2,2),new(3,0)],[2,2.5,5],[4,1,4]):
                CubicSplineDefinition.Uniform([new(-3,0),new(-1,2),new(1,2),new(3,0)]);
            d.AddSpline(reverse?geometry.Reverse():geometry);
        }
        SketchEntityId b=kind=="Line"?d.Value.Lines[0].Id:kind=="Bezier"?d.Value.Beziers[^1].Id:d.Value.Splines[0].Id;
        if(reverse&&kind is "Line" or "Bezier")
            d.Change(s=>kind=="Line"?s with{Lines=s.Lines.Select(l=>l.Id==b?l with{Start=l.End,End=l.Start}:l).ToImmutableArray()}:
                s with{Beziers=s.Beziers.Select(c=>c.Id==b?c with{Start=c.End,End=c.Start}:c).ToImmutableArray()});
        if(reverse)tb=1-tb;
        var target=SketchFiniteParametricContact.Evaluate(d.Value,b,tb);
        double rotation=Math.Atan2(source.Tangent.Y,source.Tangent.X)-Math.Atan2(target.Tangent.Y,target.Tangent.X)+(reverse?Math.PI:0)+.035;
        var bPoints=SketchFiniteParametricContact.Weights(d.Value,b,tb).Select(w=>w.Point).ToHashSet();
        d.Change(s=>s with{Points=s.Points.Select(p=>
        {
            if(!bPoints.Contains(p.Id))return p;double x=p.Position.X-target.Position.X,y=p.Position.Y-target.Position.Y;
            return p with{Position=new(source.Position.X+scale*(Math.Cos(rotation)*x-Math.Sin(rotation)*y+.08),
                source.Position.Y+scale*(Math.Sin(rotation)*x+Math.Cos(rotation)*y+.13))};
        }).ToImmutableArray()});
        d.Change(s=>s with{Constraints=[..s.Points.Where(p=>!bPoints.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            new FiniteParametricContactConstraint(SketchConstraintId.New(),a,b,ta,tb,reverse?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection)]});
        return d.Value;
    }

    [Theory]
    [InlineData("Line",false,1,0,0)][InlineData("Line",true,1,.7,1000000)]
    [InlineData("Bezier",false,.01,.7,0)][InlineData("Bezier",true,1000,.7,0)]
    [InlineData("Spline",false,1,.7,1000000)][InlineData("Spline",true,.01,0,0)]
    [InlineData("Nonuniform",false,1,.7,0)][InlineData("Nonuniform",true,1000,0,0)]
    public async Task InteriorContactSolvesExplicitParametersAndKeepsEveryIdentity(string kind,bool reverse,double scale,double angle,double translation)
    {
        var authored=Pair(kind,reverse,scale,angle,translation);var report=await new ManagedSketchConstraintSolver().SolveAsync(authored);
        Assert.True(report.Succeeded,$"{report.Diagnostic}; iteration={report.Iterations}; residual={report.MaxNormalizedResidual:R}");
        var solved=report.Solution!;SketchFiniteParametricContact.RequireSolvedRelations(solved);SketchContactSolutionVerification.Validate(authored,solved);
        Assert.Equal(authored.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));Assert.Equal(authored.Constraints,solved.Constraints);
        var c=solved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Assert.Equal(.37,c.ParameterA);Assert.Equal(reverse?.39:.61,c.ParameterB,10);
        foreach(var target in new[]{(c.A,c.ParameterA),(c.B,c.ParameterB)})RequireNativeGeometry(solved,target.Item1,target.Item2);
        Assert.Equal(9,report.EquationCount);Assert.Equal(solved.Points.Length*2-9,report.DegreesOfFreedom);
        var swap=authored with{Constraints=authored.Constraints.Select(v=>v.Id==c.Id?c with{A=c.B,B=c.A,ParameterA=c.ParameterB,ParameterB=c.ParameterA}:v).ToImmutableArray()};
        report=await new ManagedSketchConstraintSolver().SolveAsync(swap);Assert.True(report.Succeeded,report.Diagnostic);SketchFiniteParametricContact.RequireSolvedRelations(report.Solution!);
    }

    internal static void RequireNativeGeometry(CadSketch sketch,SketchEntityId curve,double parameter)
    {
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);SketchPoint2d P(SketchEntityId id)=>new(points[id].X,points[id].Y);
        SketchCurve2d definition;
        if(sketch.Lines.SingleOrDefault(l=>l.Id==curve) is {} line)definition=SketchCurve2d.Segment(P(line.Start),P(line.End));
        else if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)definition=SketchCurve2d.Bezier([P(bezier.Start),P(bezier.Control),P(bezier.End)]);
        else
        {
            var spline=sketch.Splines.Single(s=>s.Id==curve);var g=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
            definition=SketchCurve2d.BSpline(g.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,3);
        }
        using var edge=SketchModeling.CreateEdge(definition,SketchPlane.XY);Assert.True(edge.IsValid);var interval=edge.GetEdgeCurveSnapshot();
        var native=edge.EvaluateEdgeDerivatives(interval.FirstParameter+parameter*(interval.LastParameter-interval.FirstParameter));
        var actual=SketchFiniteParametricContact.Evaluate(sketch,curve,parameter);double speed=double.Hypot(native.FirstDerivative.X,native.FirstDerivative.Y);
        Assert.InRange(double.Hypot(actual.Position.X-native.Point.X,actual.Position.Y-native.Point.Y),0,1e-7);
        Assert.InRange(double.Hypot(actual.Tangent.X-native.FirstDerivative.X/speed,actual.Tangent.Y-native.FirstDerivative.Y/speed),0,1e-7);
    }

    [Theory][InlineData("Line",false)][InlineData("Bezier",true)][InlineData("Spline",false)][InlineData("Nonuniform",true)]
    public void ThreeAnalyticRowsMatchEveryIndependentDifferenceColumn(string kind,bool reverse)
    {
        var sketch=Pair(kind,reverse);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3)],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            const double step=1e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=step;minus[col]-=step;
            var p=(double[])residual.Invoke(system,[plus])!;var m=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<jac.RowCount;row++)Assert.InRange(Math.Abs((p[row]-m[row])/(2*step)-jac[row,col]),0,Math.Max(1e-5,Math.Abs(jac[row,col])*1e-6));
        }
    }

    [Fact]
    public async Task WrongBranchDoesNotFlipTheCurveAndEquationBudgetCountsAllThreeRows()
    {
        var s=Pair();var contact=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();var before=s.Points;
        var wrong=s with{Constraints=s.Constraints.Select(c=>c.Id==contact.Id?contact with{Branch=FiniteContactTangentBranch.OppositeDirection}:c).ToImmutableArray()};
        var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(wrong);Assert.False(report.Succeeded);Assert.Equal(before,wrong.Points);
        report=await solver.SolveAsync(s,new(MaxEquations:8));Assert.Equal(SketchSolveStatus.LimitExceeded,report.Status);
        report=await solver.SolveAsync(s);Assert.True(report.Succeeded);Assert.Throws<CadValidationException>(()=>SketchContactSolutionVerification.Validate(s,s));
    }

    [Theory][InlineData(double.NaN)][InlineData(-.1)][InlineData(0)][InlineData(1)][InlineData(1.1)]
    public void InvalidInteriorParametersAreRejectedEvenWhenDisabled(double value)
    {
        var s=Pair();var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        foreach(bool enabled in new[]{false,true})Assert.Throws<CadValidationException>(()=>(s with{Constraints=[c with{ParameterA=value,IsEnabled=enabled}]}).Validate());
    }

    [Theory][InlineData(2)][InlineData(3)]
    public void RepeatedInternalKnotRequiresExplicitSideButNearbyParametersRemainDistinct(int multiplicity)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Multiple knot",RigidTransform3d.Identity));
        d.AddBezier(new(-4,0),new(0,4),new(4,0));
        var controls=Enumerable.Range(0,4+multiplicity).Select(i=>new Point2d(i,1+Math.Sin(i*.4))).ToImmutableArray();
        d.AddSpline(new CubicSplineDefinition(controls,[2,2.75,5],[4,multiplicity,4]));var s=d.Value;
        var c=new FiniteParametricContactConstraint(SketchConstraintId.New(),s.Beziers[0].Id,s.Splines[0].Id,.5,.25,FiniteContactTangentBranch.SameDirection);
        foreach(double t in new[]{.25,.25-5e-13,.25+5e-13})foreach(bool enabled in new[]{true,false})
            Assert.Throws<CadValidationException>(()=>(s with{Constraints=[c with{ParameterB=t,IsEnabled=enabled}]}).Validate());
        foreach(double t in new[]{.25-2e-12,.25+2e-12})
        {var contact=c with{ParameterB=t};SketchFiniteParametricContact.ValidateTargets(s,contact);RequireNativeGeometry(s,contact.B,t);}
    }

    [Fact]
    public void InteriorDerivativeDegeneracyIsRejectedWithoutInventingDirection()
    {
        var s=Pair("Line");var line=s.Lines.Single();var start=s.Points.Single(p=>p.Id==line.Start).Position;
        // The entity remains a structurally valid segment, but its derivative is below
        // the contact contract's threshold. Normalizing it would invent a tangent.
        var tiny=s with{Points=s.Points.Select(p=>p.Id==line.End?p with{Position=new(start.X+5e-8,start.Y)}:p).ToImmutableArray()};
        Assert.Throws<CadValidationException>(()=>SketchFiniteParametricContact.Evaluate(tiny,line.Id,.61));
        var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        foreach(bool enabled in new[]{true,false})Assert.Throws<CadValidationException>(()=>SketchFiniteParametricContact.ValidateTargets(tiny,c with{IsEnabled=enabled}));
        var valid=tiny with{Points=tiny.Points.Select(p=>p.Id==line.End?p with{Position=new(start.X+2e-7,start.Y)}:p).ToImmutableArray()};
        Assert.Equal(new Point2d(1,0),SketchFiniteParametricContact.Evaluate(valid,line.Id,.61).Tangent);
    }
}
