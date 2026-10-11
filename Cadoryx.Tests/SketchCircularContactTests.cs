using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchCircularContactTests
{
    internal static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    internal static FixPointConstraint Fix(CadSketch s,SketchEntityId id)=>new(SketchConstraintId.New(),id,P(s,id));
    private static Point2d Transform(Point2d p,double scale,double angle,double translation)=>
        new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));

    internal static CadSketch Pair(CircularContactBranch branch,bool arcA=false,bool arcB=false,double gap=0,
        double scale=1,double angle=0,double translation=0)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Circular contact",RigidTransform3d.Identity));
        double ra=branch==CircularContactBranch.BContainsA?2:5,rb=branch==CircularContactBranch.BContainsA?5:2;
        double distance=(branch==CircularContactBranch.External?ra+rb:Math.Abs(ra-rb))+gap;
        Point2d T(Point2d p)=>Transform(p,scale,angle,translation);
        SketchEntityId Add(double cx,double radius,bool arc,bool left)
        {
            if(!arc){d.AddCircle(T(new(cx,0)),radius*scale);return d.Value.Circles[^1].Id;}
            double mid=left?Math.PI:0;
            d.AddArc(T(new(cx+radius*Math.Cos(mid-Math.PI/2),radius*Math.Sin(mid-Math.PI/2))),
                T(new(cx+radius*Math.Cos(mid),radius*Math.Sin(mid))),T(new(cx+radius*Math.Cos(mid+Math.PI/2),radius*Math.Sin(mid+Math.PI/2))));
            return d.Value.Arcs[^1].Id;
        }
        var a=Add(0,ra,arcA,branch==CircularContactBranch.BContainsA);
        var b=Add(distance,rb,arcB,branch!=CircularContactBranch.AContainsB);
        d.Change(s=>s with{Constraints=s.Constraints.Add(new CircularContactConstraint(SketchConstraintId.New(),a,b,branch))});
        return d.Value;
    }

    internal static CadSketch LineArc(double gap=0,bool reverse=false,bool outsideArc=false,bool outsideLine=false,bool major=false)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Finite line contact",RigidTransform3d.Identity));
        d.AddLine(reverse?new(10,0):new(0,0),reverse?new(0,0):new(10,0));
        double cx=outsideLine?12:5,cy=2+gap;
        if(outsideArc)d.AddArc(new(cx-2,cy),new(cx,cy+2),new(cx+2,cy));
        else if(major)d.AddArc(new(cx,cy+2),new(cx,cy-2),new(cx+2,cy));
        else d.AddArc(new(cx-2,cy),new(cx,cy-2),new(cx+2,cy));
        var line=d.Value.Lines[0];var arc=d.Value.Arcs[0];
        d.Change(s=>s with{Constraints=[new CircularContactConstraint(SketchConstraintId.New(),arc.Id,line.Id,
            reverse?CircularContactBranch.LineRight:CircularContactBranch.LineLeft)]});return d.Value;
    }

    internal static CadSketch Fixed(CadSketch s)=>s with{Constraints=s.Constraints.AddRange(s.Points.Select(p=>Fix(s,p.Id)))};
    private static SketchConstraint[] RigidArc(CadSketch s,SketchArc arc)
    {
        var a=P(s,arc.Start);var b=P(s,arc.Middle);var c=P(s,arc.End);
        return [new OffsetXConstraint(SketchConstraintId.New(),arc.Start,arc.Middle,b.X-a.X),
            new OffsetYConstraint(SketchConstraintId.New(),arc.Start,arc.Middle,b.Y-a.Y),
            new OffsetXConstraint(SketchConstraintId.New(),arc.Start,arc.End,c.X-a.X),
            new OffsetYConstraint(SketchConstraintId.New(),arc.Start,arc.End,c.Y-a.Y)];
    }

    [Theory]
    [InlineData(CircularContactBranch.External,false,false)] [InlineData(CircularContactBranch.External,true,false)]
    [InlineData(CircularContactBranch.External,false,true)] [InlineData(CircularContactBranch.External,true,true)]
    [InlineData(CircularContactBranch.AContainsB,false,false)] [InlineData(CircularContactBranch.AContainsB,true,true)]
    [InlineData(CircularContactBranch.BContainsA,false,false)] [InlineData(CircularContactBranch.BContainsA,true,true)]
    public async Task ExactIndependentCircularCurvesMeetOnTheSelectedBranch(CircularContactBranch branch,bool arcA,bool arcB)
    {
        var seed=Fixed(Pair(branch,arcA,arcB));var relation=seed.Constraints.OfType<CircularContactConstraint>().Single();
        var report=await new ManagedSketchConstraintSolver().SolveAsync(seed);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.True(SketchCircularContact.TryContactPoint(report.Solution!,relation,1e-7,out var contact,out var diagnostic),diagnostic);
        Assert.Equal(branch==CircularContactBranch.BContainsA?-2:5,contact.X,6);Assert.Equal(0,contact.Y,6);
        Assert.Equal(seed.Points.Select(p=>p.Id).ToArray(),report.Solution!.Points.Select(p=>p.Id).ToArray());
        Assert.Equal(relation,report.Solution.Constraints.OfType<CircularContactConstraint>().Single());
    }

    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task LineDirectionAndMajorArcMembershipAreExplicit(bool reverse,bool major)
    {
        var s=Fixed(LineArc(reverse:reverse,major:major));var report=await new ManagedSketchConstraintSolver().SolveAsync(s);
        Assert.True(report.Succeeded,report.Diagnostic);Assert.True(SketchCircularContact.TryContactPoint(report.Solution!,s.Constraints.OfType<CircularContactConstraint>().Single(),1e-7,out var contact,out _));
        Assert.Equal(5,contact.X,6);Assert.Equal(0,contact.Y,6);
        if(major){var arc=s.Arcs.Single();Assert.True(Math.Abs(SketchArcGeometry.Through(P(s,arc.Start),P(s,arc.Middle),P(s,arc.End)).SweepAngle)>Math.PI);}
    }

    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task SupportingTangencyOutsideTheFiniteCurvesCannotReportSuccess(bool outsideArc,bool outsideLine)
    {
        var s=Fixed(LineArc(outsideArc:outsideArc,outsideLine:outsideLine));var report=await new ManagedSketchConstraintSolver().SolveAsync(s);
        Assert.Null(report.Solution);Assert.Equal(SketchSolveStatus.DidNotConverge,report.Status);Assert.Equal("SKETCH.CONTACT_OUTSIDE_DOMAIN",report.Diagnostic);
        Assert.InRange(report.MaxNormalizedResidual,0,1);
        // The pre-existing constraint retains its supporting-circle/line semantics.
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Legacy",RigidTransform3d.Identity));
        d.AddLine(new(0,0),new(1,0));d.AddCircle(new(12,2),2);
        var old=Fixed(d.Value with{Constraints=d.Value.Constraints.Add(new TangentConstraint(SketchConstraintId.New(),d.Value.Lines[0].Id,d.Value.Circles[0].Id))});
        Assert.True((await new ManagedSketchConstraintSolver().SolveAsync(old)).Succeeded);
    }

    [Theory][InlineData(CircularContactBranch.External)][InlineData(CircularContactBranch.AContainsB)][InlineData(CircularContactBranch.BContainsA)]
    public async Task ArcIntervalOnEitherCircularOperandIsRechecked(CircularContactBranch branch)
    {
        var seed=Pair(branch,true,true);var first=seed.Arcs[0];var center=SketchCircularContact.Support(seed,first.Id).Center;
        var outside=seed with{Points=seed.Points.Select(p=>p.Id==first.Middle?p with{Position=new(2*center.X-p.Position.X,2*center.Y-p.Position.Y)}:p).ToImmutableArray()};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(Fixed(outside));
        Assert.Null(report.Solution);Assert.Equal("SKETCH.CONTACT_OUTSIDE_DOMAIN",report.Diagnostic);
    }

    [Theory][InlineData(.001,0,0)][InlineData(1,.71,1000)][InlineData(1000,-1.1,-1e6)]
    public async Task CircleContactSolvesAfterRotationTranslationAndScale(double scale,double angle,double translation)
    {
        var seed=Pair(CircularContactBranch.External,gap:.4,scale:scale,angle:angle,translation:translation);
        var a=seed.Circles[0];var b=seed.Circles[1];var pa=P(seed,a.Center);var pb=P(seed,b.Center);
        // One remaining translational freedom is intentionally left to the local solver.
        seed=seed with{Constraints=seed.Constraints.Add(Fix(seed,a.Center))};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(seed);Assert.True(report.Succeeded,report.Diagnostic);
        var solved=report.Solution!;var actual=P(solved,b.Center);var solvedA=P(solved,a.Center);
        Assert.InRange(Math.Abs(double.Hypot(actual.X-solvedA.X,actual.Y-solvedA.Y)-solved.Circles.Sum(c=>c.Radius)),0,1e-7);
        foreach(var circle in seed.Circles)Assert.InRange(Math.Abs(solved.Circles.Single(c=>c.Id==circle.Id).Radius-circle.Radius),0,1e-7);
        Assert.True(SketchCircularContact.TryContactPoint(report.Solution!,seed.Constraints.OfType<CircularContactConstraint>().Single(),1e-7,out _,out _));
        Assert.True((actual.X-pa.X)*(pb.X-pa.X)+(actual.Y-pa.Y)*(pb.Y-pa.Y)>0);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SolverMovesFiniteArcWithoutChangingAuthoredDirection(bool twoArcs)
    {
        var seed=twoArcs?Pair(CircularContactBranch.External,true,true,.4):LineArc(.4);
        var moving=seed.Arcs[^1];var constraints=seed.Constraints.AddRange(RigidArc(seed,moving));
        if(twoArcs)
        {
            constraints=constraints.AddRange(seed.Points.Where(p=>p.Id!=moving.Start&&p.Id!=moving.Middle&&p.Id!=moving.End).Select(p=>Fix(seed,p.Id)));
            constraints=constraints.Add(new OffsetYConstraint(SketchConstraintId.New(),seed.Arcs[0].Start,moving.Start,P(seed,moving.Start).Y-P(seed,seed.Arcs[0].Start).Y));
        }
        else
        {
            constraints=constraints.Add(Fix(seed,seed.Lines[0].Start)).Add(Fix(seed,seed.Lines[0].End));
            constraints=constraints.Add(new OffsetXConstraint(SketchConstraintId.New(),seed.Lines[0].Start,moving.Start,P(seed,moving.Start).X));
        }
        seed=seed with{Constraints=constraints};var report=await new ManagedSketchConstraintSolver().SolveAsync(seed);Assert.True(report.Succeeded,report.Diagnostic);
        var old=SketchArcGeometry.Through(P(seed,moving.Start),P(seed,moving.Middle),P(seed,moving.End));
        var now=SketchArcGeometry.Through(P(report.Solution!,moving.Start),P(report.Solution!,moving.Middle),P(report.Solution!,moving.End));
        Assert.Equal(Math.Sign(old.SweepAngle),Math.Sign(now.SweepAngle));Assert.Equal(old.Radius,now.Radius,6);
        Assert.True(SketchCircularContact.TryContactPoint(report.Solution!,seed.Constraints.OfType<CircularContactConstraint>().Single(),1e-7,out _,out _));
        Assert.True(report.Iterations>0);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void AnalyticalContactJacobianMatchesIndependentCentralDifferences(int variant)
    {
        var s=variant switch{0=>LineArc(.4),1=>Pair(CircularContactBranch.External,true,false,.4),
            2=>Pair(CircularContactBranch.External,true,true,.4),3=>Pair(CircularContactBranch.AContainsB,true,true,.4),
            4=>Pair(CircularContactBranch.BContainsA,true,true,.4),5=>Pair(CircularContactBranch.External,gap:.4),_=>LineArc(.4,reverse:true)};
        s=s with{Constraints=[s.Constraints.OfType<CircularContactConstraint>().Single()]};
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var system=Activator.CreateInstance(type,flags,null,[s,new SketchSolveOptions()],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        double weight=(double)type.GetField("scale",flags)!.GetValue(system)!/1e-7;
        for(int col=0;col<x.Length;col++)
        {
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();const double step=1e-6;plus[col]+=step;minus[col]-=step;
            // Compare derivatives before tolerance weighting. Differencing 1e8-weighted near-zero
            // derivatives magnifies floating-point cancellation, not a geometric derivative error.
            double numeric=(((double[])residual.Invoke(system,[plus])!)[0]/weight-((double[])residual.Invoke(system,[minus])!)[0]/weight)/(2*step);
            Assert.InRange(Math.Abs(numeric-jac[0,col]/weight),0,3e-9+Math.Abs(numeric)*3e-6);
        }
    }

    [Fact] public async Task WrongContainmentConcentricAndFixedConflictAreLocalFailures()
    {
        var wrong=Pair(CircularContactBranch.External);var relation=wrong.Constraints.OfType<CircularContactConstraint>().Single();
        wrong=wrong with{Constraints=wrong.Constraints.Select(c=>c.Id==relation.Id?relation with{Branch=CircularContactBranch.BContainsA}:c).ToImmutableArray()};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(wrong);Assert.Null(report.Solution);Assert.Equal(SketchSolveStatus.DidNotConverge,report.Status);
        var center=wrong.Circles[1].Center;wrong=wrong with{Points=wrong.Points.Select(p=>p.Id==center?p with{Position=P(wrong,wrong.Circles[0].Center)}:p).ToImmutableArray(),Constraints=wrong.Constraints.Select(c=>c.Id==relation.Id?relation:c).ToImmutableArray()};
        report=await new ManagedSketchConstraintSolver().SolveAsync(wrong);Assert.Null(report.Solution);Assert.Equal(SketchSolveStatus.DidNotConverge,report.Status);
        report=await new ManagedSketchConstraintSolver().SolveAsync(Fixed(Pair(CircularContactBranch.External,gap:1)));
        Assert.Null(report.Solution);Assert.Equal(SketchSolveStatus.DidNotConverge,report.Status);Assert.Empty(report.ConflictingConstraints);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void TargetsAndBranchesAreValidatedEvenWhenDisabledAndDeletionUsesStableIds(bool enabled)
    {
        var s=Pair(CircularContactBranch.External,true,true);var relation=s.Constraints.OfType<CircularContactConstraint>().Single();
        foreach(var invalid in new[]{relation with{A=relation.B},relation with{B=SketchEntityId.New()},relation with{Branch=(CircularContactBranch)99},relation with{Branch=CircularContactBranch.LineLeft}})
            Assert.Throws<CadValidationException>(()=>(s with{Constraints=[invalid with{IsEnabled=enabled}]}).Validate());
        var d=new SketchDraft(s);d.RemoveEntities([s.Arcs[0].Middle]);Assert.Empty(d.Value.Constraints.OfType<CircularContactConstraint>());d.Undo();Assert.Same(s,d.Value);d.Redo();Assert.Single(d.Value.Arcs);
        var bad=s with{Points=s.Points.Select(p=>p.Id==s.Arcs[0].Middle?p with{Position=new(0,0)}:p).ToImmutableArray()};
        Assert.Throws<CadValidationException>(()=>bad.Validate());
    }

    [Fact] public async Task DisabledRelationBudgetAndCancellationRetainExistingContracts()
    {
        var s=Fixed(LineArc(outsideArc:true));var relation=s.Constraints.OfType<CircularContactConstraint>().Single();
        s=s with{Constraints=s.Constraints.Select(c=>c.Id==relation.Id?c with{IsEnabled=false}:c).ToImmutableArray()};
        var solver=new ManagedSketchConstraintSolver();Assert.True((await solver.SolveAsync(s)).Succeeded);
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(s,new(MaxVariables:1))).Status);
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>solver.SolveAsync(s,cancellationToken:cancel.Token));
    }
    [Theory][InlineData(CircularContactBranch.AContainsB)][InlineData(CircularContactBranch.BContainsA)]
    public async Task InternalBranchesSolveWithoutSwappingTheContainingCurve(CircularContactBranch branch)
    {
        var seed=Pair(branch,gap:.4);seed=seed with{Constraints=seed.Constraints.Add(Fix(seed,seed.Circles[0].Center))};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(seed);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.True(SketchCircularContact.TryContactPoint(report.Solution!,seed.Constraints.OfType<CircularContactConstraint>().Single(),1e-7,out var contact,out _));
        Assert.Equal(branch==CircularContactBranch.AContainsB?5:-2,contact.X,5);
    }
    [Fact] public async Task GeometricallyTouchingEndpointsDoNotAcquireSharedPointIdentity()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Separate endpoints",RigidTransform3d.Identity));d.AddLine(new(0,0),new(5,0));
        d.AddArc(new(5,0),new(7,2),new(5,4));var line=d.Value.Lines[0];var arc=d.Value.Arcs[0];Assert.NotEqual(line.End,arc.Start);
        d.Change(s=>s with{Constraints=[new CircularContactConstraint(SketchConstraintId.New(),line.Id,arc.Id,CircularContactBranch.LineLeft)]});
        var s=Fixed(d.Value);var report=await new ManagedSketchConstraintSolver().SolveAsync(s);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(s.Points.Length,report.Solution!.Points.Length);Assert.NotEqual(line.End,arc.Start);Assert.Empty(report.Solution.Constraints.OfType<CoincidentConstraint>());
        Assert.True(SketchCircularContact.TryContactPoint(report.Solution,s.Constraints.OfType<CircularContactConstraint>().Single(),1e-7,out var contact,out _));Assert.Equal(new Point2d(5,0),contact);
    }
}
