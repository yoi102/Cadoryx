using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSolverTests
{
    private readonly ManagedSketchConstraintSolver solver=new();
    [Fact] public async Task RectangleSolvesDimensionsWithZeroDofAndStableIdentities()
    {
        var sketch=SketchTestData.Rectangle();var before=sketch.Points;
        var result=await solver.SolveAsync(sketch);
        Assert.True(result.Succeeded,result.Diagnostic+" "+result.MaxNormalizedResidual);
        Assert.Equal(SketchSolveStatus.FullyConstrained,result.Status);Assert.Equal(0,result.DegreesOfFreedom);Assert.Equal(8,result.Rank);
        var points=result.Solution!.Points;Near(0,points[0].Position.X);Near(0,points[0].Position.Y);
        Near(40,points[1].Position.X);Near(30,points[3].Position.Y);
        Assert.Equal(sketch.Id,result.Solution.Id);Assert.Equal(sketch.Points.Select(p=>p.Id),points.Select(p=>p.Id));
        Assert.Equal(sketch.Constraints,result.Solution.Constraints);Assert.Equal(before,sketch.Points);
    }
    [Fact] public async Task UnanchoredRectangleRetainsTwoTranslationDofs()
    {
        var sketch=SketchTestData.Rectangle();sketch=sketch with{Constraints=sketch.Constraints.Where(c=>c is not FixPointConstraint).ToImmutableArray()};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic);
        Assert.Equal(SketchSolveStatus.UnderConstrained,result.Status);Assert.Equal(2,result.DegreesOfFreedom);
    }
    [Fact] public async Task RedundantEquationDoesNotConsumeAnotherDofOrBecomeAConflict()
    {
        var sketch=SketchTestData.Rectangle();sketch=sketch with{Constraints=sketch.Constraints.Add(new OffsetXConstraint(SketchConstraintId.New(),sketch.Points[3].Id,sketch.Points[2].Id,40))};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic);Assert.Equal(0,result.DegreesOfFreedom);
        Assert.NotEmpty(result.RedundantConstraints);Assert.Empty(result.ConflictingConstraints);Assert.Equal(8,result.Rank);
    }
    [Fact] public async Task AffineConflictReportsOnlyTheInconsistentSubset()
    {
        var p=new SketchPoint(SketchEntityId.New(),new(1,2));var q=new SketchPoint(SketchEntityId.New(),new(7,8));
        var a=new FixPointConstraint(SketchConstraintId.New(),p.Id,new(0,0));var b=new FixPointConstraint(SketchConstraintId.New(),p.Id,new(10,0));
        var sketch=SketchTestData.Empty() with{Points=[p,q],Constraints=[a,b,new FixPointConstraint(SketchConstraintId.New(),q.Id,new(7,8))]};
        var result=await solver.SolveAsync(sketch);Assert.Equal(SketchSolveStatus.Inconsistent,result.Status);Assert.Null(result.Solution);Assert.Null(result.DegreesOfFreedom);
        Assert.True(result.ConflictingConstraints.ToHashSet().SetEquals([a.Id,b.Id]));Assert.True(result.MaxNormalizedResidual>1);
    }
    [Theory][InlineData(2,8)][InlineData(-2,-8)]
    public async Task NonlinearDistanceKeepsTheNearbySolutionBranch(double seed,double expected)
    {
        var p=new SketchPoint(SketchEntityId.New(),new(0,0));var q=new SketchPoint(SketchEntityId.New(),new(seed,3));
        var sketch=SketchTestData.Empty() with{Points=[p,q],Constraints=[new FixPointConstraint(SketchConstraintId.New(),p.Id,new(0,0)),
            new OffsetYConstraint(SketchConstraintId.New(),p.Id,q.Id,6),new DistanceConstraint(SketchConstraintId.New(),p.Id,q.Id,10)]};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic+" "+result.MaxNormalizedResidual);
        Assert.Equal(0,result.DegreesOfFreedom);Near(expected,result.Solution!.Points[1].Position.X);Near(6,result.Solution.Points[1].Position.Y);
    }
    [Fact] public async Task SingularNonlinearSeedIsNotClaimedToBeAnImpossibleSketch()
    {
        var p=new SketchPoint(SketchEntityId.New(),new(0,0));var q=new SketchPoint(SketchEntityId.New(),new(0,0));
        var sketch=SketchTestData.Empty() with{Points=[p,q],Constraints=[new DistanceConstraint(SketchConstraintId.New(),p.Id,q.Id,10)]};
        var result=await solver.SolveAsync(sketch);Assert.Equal(SketchSolveStatus.DidNotConverge,result.Status);
        Assert.Null(result.Solution);Assert.Null(result.DegreesOfFreedom);Assert.Empty(result.ConflictingConstraints);
    }
    [Fact] public async Task CircleUsesCenterAndRadiusAsThreeIndependentVariables()
    {
        var center=new SketchPoint(SketchEntityId.New(),new(2,4));var circle=new SketchCircle(SketchEntityId.New(),center.Id,5);
        var sketch=SketchTestData.Empty() with{Points=[center],Circles=[circle]};var free=await solver.SolveAsync(sketch);Assert.Equal(3,free.DegreesOfFreedom);
        sketch=sketch with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),center.Id,new(10,20)),new RadiusConstraint(SketchConstraintId.New(),circle.Id,7)]};
        var solved=await solver.SolveAsync(sketch);Assert.True(solved.Succeeded,solved.Diagnostic);Assert.Equal(0,solved.DegreesOfFreedom);Near(7,solved.Solution!.Circles[0].Radius);
    }
    [Theory][InlineData("parallel")][InlineData("perpendicular")][InlineData("equal-length")]
    public async Task LineRelationsRemoveOneLocalDof(string kind)
    {
        var points=new[]{new Point2d(0,0),new(10,1),new(2,5),new(7,12)}.Select(p=>new SketchPoint(SketchEntityId.New(),p)).ToImmutableArray();
        var a=new SketchLine(SketchEntityId.New(),points[0].Id,points[1].Id);var b=new SketchLine(SketchEntityId.New(),points[2].Id,points[3].Id);
        SketchConstraint c=kind switch{"parallel"=>new ParallelConstraint(SketchConstraintId.New(),a.Id,b.Id),"perpendicular"=>new PerpendicularConstraint(SketchConstraintId.New(),a.Id,b.Id),_=>new EqualLengthConstraint(SketchConstraintId.New(),a.Id,b.Id)};
        var result=await solver.SolveAsync(SketchTestData.Empty() with{Points=points,Lines=[a,b],Constraints=[c]});
        Assert.True(result.Succeeded,result.Diagnostic+" "+result.MaxNormalizedResidual);Assert.Equal(7,result.DegreesOfFreedom);
        var q=result.Solution!.Points;var u=new Point2d(q[1].Position.X-q[0].Position.X,q[1].Position.Y-q[0].Position.Y);var v=new Point2d(q[3].Position.X-q[2].Position.X,q[3].Position.Y-q[2].Position.Y);
        double ul=double.Hypot(u.X,u.Y),vl=double.Hypot(v.X,v.Y);
        if(kind=="parallel")Assert.InRange(Math.Abs((u.X*v.Y-u.Y*v.X)/ul/vl),0,1e-9);
        else if(kind=="perpendicular")Assert.InRange(Math.Abs((u.X*v.X+u.Y*v.Y)/ul/vl),0,1e-9);
        else Near(ul,vl);
    }
    [Fact] public async Task CoincidentAndEqualRadiusHaveExpectedRanks()
    {
        var p=new SketchPoint(SketchEntityId.New(),new(1,2));var q=new SketchPoint(SketchEntityId.New(),new(3,4));
        var c=new SketchCircle(SketchEntityId.New(),p.Id,2);var d=new SketchCircle(SketchEntityId.New(),q.Id,4);
        var sketch=SketchTestData.Empty() with{Points=[p,q],Circles=[c,d],Constraints=[new CoincidentConstraint(SketchConstraintId.New(),p.Id,q.Id),new EqualRadiusConstraint(SketchConstraintId.New(),c.Id,d.Id)]};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic);Assert.Equal(3,result.DegreesOfFreedom);
        Near(result.Solution!.Circles[0].Radius,result.Solution.Circles[1].Radius);
    }
    [Fact] public async Task LengthConstraintAndHorizontalLineSolveTogether()
    {
        var p=new SketchPoint(SketchEntityId.New(),new(0,0));var q=new SketchPoint(SketchEntityId.New(),new(4,1));var line=new SketchLine(SketchEntityId.New(),p.Id,q.Id);
        var sketch=SketchTestData.Empty() with{Points=[p,q],Lines=[line],Constraints=[new FixPointConstraint(SketchConstraintId.New(),p.Id,new(0,0)),new HorizontalConstraint(SketchConstraintId.New(),line.Id),new LengthConstraint(SketchConstraintId.New(),line.Id,10)]};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic);Assert.Equal(0,result.DegreesOfFreedom);Near(10,result.Solution!.Points[1].Position.X);
    }
    [Fact] public async Task DisabledConstraintDoesNotConstrainButStillRequiresValidReferences()
    {
        var sketch=SketchTestData.Rectangle();var anchor=(FixPointConstraint)sketch.Constraints[0];
        sketch=sketch with{Constraints=sketch.Constraints.SetItem(0,anchor with{IsEnabled=false})};
        Assert.Equal(2,(await solver.SolveAsync(sketch)).DegreesOfFreedom);
        sketch=sketch with{Constraints=sketch.Constraints.SetItem(0,anchor with{IsEnabled=false,Point=SketchEntityId.New()})};
        Assert.Equal(SketchSolveStatus.InvalidInput,(await solver.SolveAsync(sketch)).Status);
    }
    [Theory][InlineData(1e6)][InlineData(-1e6)]
    public async Task LargeLocalOriginDoesNotChangeDimensions(double offset)
    {
        var sketch=SketchTestData.Rectangle();sketch=sketch with{Points=sketch.Points.Select(p=>p with{Position=new(p.Position.X+offset,p.Position.Y+offset)}).ToImmutableArray(),
            Constraints=sketch.Constraints.SetItem(0,((FixPointConstraint)sketch.Constraints[0]) with{Position=new(offset,offset)})};
        var result=await solver.SolveAsync(sketch);Assert.True(result.Succeeded,result.Diagnostic);Near(40,result.Solution!.Points[1].Position.X-result.Solution.Points[0].Position.X);
    }
    [Fact] public async Task InvalidNumbersDuplicateIdsAndWrongKindsAreRejected()
    {
        var sketch=SketchTestData.Rectangle();
        Assert.Equal(SketchSolveStatus.InvalidInput,(await solver.SolveAsync(sketch with{Points=sketch.Points.SetItem(0,sketch.Points[0] with{Position=new(double.NaN,0)})})).Status);
        Assert.Equal(SketchSolveStatus.InvalidInput,(await solver.SolveAsync(sketch with{Points=sketch.Points.Add(sketch.Points[0])})).Status);
        Assert.Equal(SketchSolveStatus.InvalidInput,(await solver.SolveAsync(sketch with{Constraints=[new RadiusConstraint(SketchConstraintId.New(),sketch.Lines[0].Id,1)]})).Status);
    }
    [Fact] public async Task BudgetAndCancellationAreExplicit()
    {
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(SketchTestData.Rectangle(),new(MaxVariables:4))).Status);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>solver.SolveAsync(SketchTestData.Rectangle(),cancellationToken:cancel.Token));
        Assert.DoesNotContain(typeof(ManagedSketchConstraintSolver).Assembly.GetReferencedAssemblies(),a=>a.Name!.Contains("Occt"));
    }
    [Fact] public async Task PolygonBridgeUsesSharedIdentityAndValidatesClosure()
    {
        var solved=(await solver.SolveAsync(SketchTestData.Rectangle())).Solution!;var profile=SketchProfileBuilder.Polygon(solved,solved.Lines.Select(l=>l.Id));
        Assert.Equal(4,profile.Points.Length);Near(40,profile.Points[1].X);
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Polygon(solved,solved.Lines.Take(3).Select(l=>l.Id)));
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Polygon(solved with{Lines=solved.Lines.SetItem(0,solved.Lines[0] with{IsConstruction=true})},solved.Lines.Select(l=>l.Id)));
    }
    [Theory][InlineData(0.00001)][InlineData(0.001)][InlineData(1)]
    public async Task RigidDistanceNetworkRetainsTranslationAndRotationDofsAtDifferentScales(double scale)
    {
        var p=new[]{new Point2d(0,0),new(2,1),new(3,5),new(-1,2)}.Select(p=>new SketchPoint(SketchEntityId.New(),new(p.X*scale,p.Y*scale))).ToImmutableArray();
        var constraints=ImmutableArray.CreateBuilder<SketchConstraint>();
        for(int i=0;i<p.Length;i++)for(int j=i+1;j<p.Length;j++)constraints.Add(new DistanceConstraint(SketchConstraintId.New(),p[i].Id,p[j].Id,double.Hypot(p[j].Position.X-p[i].Position.X,p[j].Position.Y-p[i].Position.Y)));
        var report=await solver.SolveAsync(SketchTestData.Empty() with{Points=p,Constraints=constraints.ToImmutable()});
        Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(3,report.DegreesOfFreedom);Assert.Equal(5,report.Rank);Assert.NotEmpty(report.RedundantConstraints);
    }
    internal static void Near(double expected,double actual)=>Assert.InRange(Math.Abs(expected-actual),0,1e-7);
}
internal static class SketchTestData
{
    internal static CadSketch Empty()=>CadSketch.Create(DefinitionId.New(),"Sketch",RigidTransform3d.Identity);
    internal static CadSketch Rectangle(DefinitionId? part=null)
    {
        var p=new[]{new Point2d(1,2),new(12,1),new(11,7),new(2,9)}.Select(p=>new SketchPoint(SketchEntityId.New(),p)).ToImmutableArray();
        var l=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),p[i].Id,p[(i+1)%4].Id)).ToImmutableArray();
        return new(SketchId.New(),part??DefinitionId.New(),"Rectangle",RigidTransform3d.Identity,p,l,[],[
            new FixPointConstraint(SketchConstraintId.New(),p[0].Id,new(0,0)),new HorizontalConstraint(SketchConstraintId.New(),l[0].Id),
            new VerticalConstraint(SketchConstraintId.New(),l[1].Id),new HorizontalConstraint(SketchConstraintId.New(),l[2].Id),new VerticalConstraint(SketchConstraintId.New(),l[3].Id),
            new OffsetXConstraint(SketchConstraintId.New(),p[0].Id,p[1].Id,40),new OffsetYConstraint(SketchConstraintId.New(),p[0].Id,p[3].Id,30)]);
    }
}
