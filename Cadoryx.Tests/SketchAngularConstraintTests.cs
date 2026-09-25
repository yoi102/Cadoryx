using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchAngularConstraintTests
{
    [Fact] public async Task TangencyMovesCircleToItsAuthoredSideOfTheLine()
    {
        var sketch=CadSketch.Create(DefinitionId.New(),"Tangency",RigidTransform3d.Identity);
        var draft=new SketchDraft(sketch);draft.AddLine(new(0,0),new(10,0));draft.AddCircle(new(5,3),2);
        var line=draft.Value.Lines[0];var circle=draft.Value.Circles[0];var center=circle.Center;
        draft.Change(s=>s with{Constraints=[..s.Constraints,
            new FixPointConstraint(SketchConstraintId.New(),line.Start,new(0,0)),
            new FixPointConstraint(SketchConstraintId.New(),line.End,new(10,0)),
            new OffsetXConstraint(SketchConstraintId.New(),line.Start,center,5),
            new TangentConstraint(SketchConstraintId.New(),line.Id,circle.Id)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(draft.Value);
        Assert.NotNull(report.Solution);
        var actual=report.Solution!.Points.Single(p=>p.Id==center).Position;
        Assert.Equal(5,actual.X,5);Assert.Equal(2,actual.Y,5);
    }

    [Fact] public async Task AngleDimensionSolvesAndPersistsWithExplicitSchemaMigration()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var draft=new SketchDraft(CadSketch.Create(part,"Angle",RigidTransform3d.Identity));
        draft.AddLine(new(0,0),new(10,0));draft.AddLine(new(0,0),new(6,8));
        var first=draft.Value.Lines[0];var second=draft.Value.Lines[1];
        draft.Change(s=>s with{Constraints=[..s.Constraints,
            new FixPointConstraint(SketchConstraintId.New(),first.Start,new(0,0)),
            new FixPointConstraint(SketchConstraintId.New(),first.End,new(10,0)),
            new FixPointConstraint(SketchConstraintId.New(),second.Start,new(0,0)),
            new LengthConstraint(SketchConstraintId.New(),second.Id,10),
            new AngleConstraint(SketchConstraintId.New(),first.Id,second.Id,Math.PI/3)]});
        var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(draft.Value);
        Assert.NotNull(report.Solution);
        var end=report.Solution!.Points.Single(p=>p.Id==second.End).Position;
        Assert.Equal(5,end.X,4);Assert.Equal(5*Math.Sqrt(3),end.Y,4);
        Assert.Throws<CadValidationException>(()=>(draft.Value with{Constraints=draft.Value.Constraints.Add(
            new AngleConstraint(SketchConstraintId.New(),first.Id,second.Id,0))}).Validate());

        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("angle.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("angle.cadoryx"),assets))
            Assert.Single(loaded.Snapshot.Sketches[draft.Value.Id].Constraints.OfType<AngleConstraint>());
        FormatEvolutionTests.RewriteManifest(files.PathFor("angle.cadoryx"),m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=2}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(files.PathFor("angle.cadoryx"),assets));
    }

    [Fact] public async Task OlderSketchSchemaWithoutAngularConstraintsStillMigrates()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(SketchTestData.Rectangle(part),new ManagedSketchConstraintSolver()));
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("old-sketch.cadoryx"));
        FormatEvolutionTests.RewriteManifest(files.PathFor("old-sketch.cadoryx"),m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=2}:s).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("old-sketch.cadoryx"),assets);
        Assert.Single(loaded.Snapshot.Sketches);
    }
}
