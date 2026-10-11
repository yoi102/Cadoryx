using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSolverIsolationTests
{
    [Theory][InlineData(27)][InlineData(83)][InlineData(144)][InlineData(146)][InlineData(201)]
    public async Task DrivingOffsetCirclePreservesIndependentOriginalGeometryExactly(int seed)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Independent radius",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(40,30));draft.AddCircle(new(20,15),5);
        var offset=draft.OffsetRegions([draft.Value.Lines[0].Id,draft.Value.Circles[0].Id],1,SketchOffsetJoin.Miter);
        var circle=draft.Value.Circles.Single(c=>c.Id==offset.Roots.Single().Children.Single().CircleId);
        var center=draft.Value.Points.Single(p=>p.Id==circle.Center);
        draft.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),center.Id,center.Position))
            .Add(new RadiusConstraint(SketchConstraintId.New(),circle.Id,circle.Radius))});
        var authored=WithFixedIds(draft.Value,seed);var solver=new ManagedSketchConstraintSolver();
        var accepted=await solver.SolveAsync(authored);Assert.True(accepted.Succeeded,accepted.Diagnostic);
        Assert.Equal(authored.Points.ToArray(),accepted.Solution!.Points.ToArray());
        Assert.Equal(authored.Circles.ToArray(),accepted.Solution.Circles.ToArray());
        var requested=accepted.Solution with{Constraints=accepted.Solution.Constraints.Select(c=>
            c is RadiusConstraint r&&r.Circle==authored.Circles[1].Id?r with{Radius=3}:c).ToImmutableArray()};
        var driven=await solver.SolveAsync(requested);Assert.True(driven.Succeeded,driven.Diagnostic);
        Assert.Equal(5,driven.Solution!.Circles[0].Radius);
        Assert.Equal(authored.Points.ToArray(),driven.Solution.Points.ToArray());
        Assert.Equal(authored.Lines.ToArray(),driven.Solution.Lines.ToArray());
        Assert.Equal(authored.Circles[0],driven.Solution.Circles[0]);
        Assert.Equal(3,driven.Solution.Circles[1].Radius,7);
        Assert.InRange(driven.MaxNormalizedResidual,0,1);
    }

    [Fact]
    public async Task SatisfiedConstraintsDoNotRoundTripAuthoredCoordinatesOrRadius()
    {
        var p=new SketchPoint(SketchEntityId.New(),new(0,0));var q=new SketchPoint(SketchEntityId.New(),new(41,0));
        var center=new SketchPoint(SketchEntityId.New(),new(7,7));var circle=new SketchCircle(SketchEntityId.New(),center.Id,7);
        var s=CadSketch.Create(DefinitionId.New(),"Exact unchanged values",RigidTransform3d.Identity) with
        {Points=[p,q,center],Circles=[circle],Constraints=[new RadiusConstraint(SketchConstraintId.New(),circle.Id,7)]};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(s);
        Assert.True(result.Succeeded,result.Diagnostic);Assert.Equal(0,result.Iterations);
        Assert.Equal(s.Points.ToArray(),result.Solution!.Points.ToArray());
        Assert.Equal(s.Circles.ToArray(),result.Solution.Circles.ToArray());
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task InitiallySatisfiedRelationTransmitsChangesOnlyWhenEnabled(bool enabled)
    {
        var center=new SketchPoint(SketchEntityId.New(),new(0,0));
        var a=new SketchCircle(SketchEntityId.New(),center.Id,4);var b=new SketchCircle(SketchEntityId.New(),center.Id,4);
        var untouched=new SketchCircle(SketchEntityId.New(),center.Id,7);
        var s=CadSketch.Create(DefinitionId.New(),"Connected radii",RigidTransform3d.Identity) with
        {Points=[center,new(SketchEntityId.New(),new(41,0))],Circles=[a,b,untouched],Constraints=[
            new RadiusConstraint(SketchConstraintId.New(),a.Id,3),new EqualRadiusConstraint(SketchConstraintId.New(),a.Id,b.Id,enabled),
            new RadiusConstraint(SketchConstraintId.New(),untouched.Id,7)]};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(s);Assert.True(result.Succeeded,result.Diagnostic);
        Assert.Equal(3,result.Solution!.Circles[0].Radius,7);Assert.Equal(enabled?3:4,result.Solution.Circles[1].Radius,7);
        Assert.Equal(untouched,result.Solution.Circles[2]);Assert.InRange(result.MaxNormalizedResidual,0,1);
    }

    // Stable permutations cover the order-sensitive SVD failure without random retries.
    private static CadSketch WithFixedIds(CadSketch s,int seed)
    {
        var random=new Random(seed);Guid Id(){var bytes=new byte[16];random.NextBytes(bytes);return new Guid(bytes);}
        var ids=s.Points.Select(p=>p.Id).Concat(s.Lines.Select(l=>l.Id)).Concat(s.Circles.Select(c=>c.Id))
            .ToDictionary(id=>id,_=>new SketchEntityId(Id()));
        SketchEntityId E(SketchEntityId id)=>ids[id];
        var constraints=s.Constraints.Select(c=>
        {
            var id=new SketchConstraintId(Id());return c switch
            {
                FixPointConstraint v=>(SketchConstraint)new FixPointConstraint(id,E(v.Point),v.Position),
                HorizontalConstraint v=>new HorizontalConstraint(id,E(v.Line)),VerticalConstraint v=>new VerticalConstraint(id,E(v.Line)),
                OffsetXConstraint v=>new OffsetXConstraint(id,E(v.A),E(v.B),v.Offset),OffsetYConstraint v=>new OffsetYConstraint(id,E(v.A),E(v.B),v.Offset),
                RadiusConstraint v=>new RadiusConstraint(id,E(v.Circle),v.Radius),_=>throw new InvalidOperationException(c.GetType().Name)
            };
        }).ToImmutableArray();
        return s with{Id=new SketchId(Id()),PartId=new DefinitionId(Id()),Points=s.Points.Select(p=>p with{Id=E(p.Id)}).ToImmutableArray(),
            Lines=s.Lines.Select(l=>l with{Id=E(l.Id),Start=E(l.Start),End=E(l.End)}).ToImmutableArray(),
            Circles=s.Circles.Select(c=>c with{Id=E(c.Id),Center=E(c.Center)}).ToImmutableArray(),Constraints=constraints};
    }
}
