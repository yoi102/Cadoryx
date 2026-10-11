using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchBasicRelationTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Relations",RigidTransform3d.Identity));
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    private static FixPointConstraint Fix(CadSketch s,SketchEntityId id)=>new(SketchConstraintId.New(),id,P(s,id));
    [Fact] public async Task MidpointIsAffineAndFollowsMovingEndpointsWithConflictDiagnosticsAndBudget()
    {
        var d=Draft();d.AddLine(new(1,2),new(11,8));d.AddPoint(new(4,9));var line=d.Value.Lines[0];var target=d.Value.Points[^1].Id;
        var midpoint=new MidpointConstraint(SketchConstraintId.New(),target,line.Id);
        d.Change(s=>s with{Constraints=[Fix(s,line.Start),Fix(s,line.End),midpoint]});var solver=new ManagedSketchConstraintSolver();
        var report=await solver.SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(SketchSolveStatus.FullyConstrained,report.Status);
        Assert.Equal(6,P(report.Solution!,target).X,6);Assert.Equal(5,P(report.Solution!,target).Y,6);Assert.Equal(6,report.EquationCount);
        var changed=d.Value with{Constraints=d.Value.Constraints.Select(c=>c is FixPointConstraint f&&f.Point==line.End?f with{Position=new(15,12)}:c).ToImmutableArray()};
        report=await solver.SolveAsync(changed);Assert.True(report.Succeeded);Assert.Equal(8,P(report.Solution!,target).X,6);Assert.Equal(7,P(report.Solution!,target).Y,6);
        changed=changed with{Constraints=changed.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),target,new(0,0)))};
        report=await solver.SolveAsync(changed);Assert.Equal(SketchSolveStatus.Inconsistent,report.Status);Assert.Contains(midpoint.Id,report.ConflictingConstraints);
        report=await solver.SolveAsync(d.Value,new(MaxEquations:5));Assert.Equal(SketchSolveStatus.LimitExceeded,report.Status);
        var disabled=d.Value with{Constraints=d.Value.Constraints.SetItem(2,midpoint with{IsEnabled=false})};
        Assert.Equal(2,(await solver.SolveAsync(disabled)).DegreesOfFreedom);
    }
    [Theory] [InlineData(false,0)] [InlineData(true,0)] [InlineData(false,1000000)]
    public async Task SymmetrySolvesReflectionAboutAnObliqueAxisRegardlessOfAxisDirection(bool reverse,double translation)
    {
        var d=Draft();var start=new Point2d(translation+4,translation+1);var end=new Point2d(translation+8,translation+7);
        d.AddLine(reverse?end:start,reverse?start:end);d.AddPoint(new(translation+1,translation+5));d.AddPoint(new(translation+8,translation+9));
        var axis=d.Value.Lines[0];var a=d.Value.Points[^2].Id;var b=d.Value.Points[^1].Id;
        d.Change(s=>s with{Constraints=[Fix(s,axis.Start),Fix(s,axis.End),Fix(s,a),new SymmetryConstraint(SketchConstraintId.New(),a,b,axis.Id)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(0,report.DegreesOfFreedom);
        double t=(-3*4+4*6)/52d;var reflected=new Point2d(translation+2*(4+4*t)-1,translation+2*(1+6*t)-5);
        Assert.Equal(reflected.X,P(report.Solution!,b).X,6);Assert.Equal(reflected.Y,P(report.Solution!,b).Y,6);
        Assert.InRange(report.MaxNormalizedResidual,0,1);
        var conflict=d.Value with{Constraints=d.Value.Constraints.Add(Fix(d.Value,b))};Assert.False((await new ManagedSketchConstraintSolver().SolveAsync(conflict)).Succeeded);
    }
    [Fact] public async Task SymmetryAlsoSolvesTheAxisAndReportsRedundancyWithoutTreatingLocalFailureAsProof()
    {
        var d=Draft();d.AddLine(new(0,-5),new(1,5));d.AddPoint(new(-3,2));d.AddPoint(new(3,2));
        var axis=d.Value.Lines[0];var a=d.Value.Points[^2].Id;var b=d.Value.Points[^1].Id;
        d.Change(s=>s with{Constraints=[Fix(s,axis.Start),Fix(s,a),Fix(s,b),new LengthConstraint(SketchConstraintId.New(),axis.Id,10),
            new SymmetryConstraint(SketchConstraintId.New(),a,b,axis.Id)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(0,P(report.Solution!,axis.End).X,6);Assert.Equal(5,P(report.Solution!,axis.End).Y,6);Assert.NotEmpty(report.RedundantConstraints);
    }
    [Fact] public void TypedReferencesRejectWrongTargetsAndDeletionCleansAllThreeSymmetryTargets()
    {
        var d=Draft();d.AddLine(new(0,-5),new(0,5));d.AddPoint(new(-3,2));d.AddPoint(new(3,2));
        var axis=d.Value.Lines[0];var a=d.Value.Points[^2].Id;var b=d.Value.Points[^1].Id;var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[new MidpointConstraint(SketchConstraintId.New(),axis.Start,axis.Id)]}));
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[new SymmetryConstraint(SketchConstraintId.New(),a,b,a)]}));Assert.Same(before,d.Value);
        var symmetry=new SymmetryConstraint(SketchConstraintId.New(),a,b,axis.Id);d.Change(s=>s with{Constraints=[symmetry]});
        d.RemoveEntities([axis.Id]);Assert.Empty(d.Value.Constraints);d.Undo();Assert.Equal(symmetry,d.Value.Constraints.Single());
    }
    [Fact] public async Task BasicRelationsDriveAssociatedTrianglePreviewRecomputeUndoAndReopen()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Driven triangle",RigidTransform3d.Identity));d.AddLine(new(0,-5),new(0,5),construction:true);var axis=d.Value.Lines[0];
        d.AddPoint(new(-3,2));d.AddPoint(new(3,2));d.AddPoint(new(0,8));d.AddPoint(new(0,3));
        var a=d.Value.Points[^4].Id;var b=d.Value.Points[^3].Id;var top=d.Value.Points[^2].Id;var mid=d.Value.Points[^1].Id;
        d.AddLine(P(d.Value,a),P(d.Value,b),start:a,end:b);var bottom=d.Value.Lines[^1];
        d.AddLine(P(d.Value,b),P(d.Value,top),start:b,end:top);d.AddLine(P(d.Value,top),P(d.Value,a),start:top,end:a);
        var anchor=Fix(d.Value,a);
        d.Change(s=>s with{Constraints=[Fix(s,axis.Start),Fix(s,axis.End),anchor,Fix(s,top),
            new SymmetryConstraint(SketchConstraintId.New(),a,b,axis.Id),new MidpointConstraint(SketchConstraintId.New(),mid,bottom.Id)]});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var reference=SketchProfileReference.Create(sketch,SketchLoops.Find(sketch).Single());
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Triangle",part,sketchSource:reference));
        var before=session.Snapshot;Assert.Equal(36,before.Bodies.Values.Single().Geometry.VolumeMm3,5);var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
            editor.SelectedConstraint=editor.Constraints.Single(c=>c.Id==anchor.Id);editor.DimensionValue=-4;editor.DimensionY=1;editor.ApplyDimensionCommand.Execute(null);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
            Assert.Contains(document.PreviewScene!.Items,i=>Math.Abs(i.Geometry.VolumeMm3-56)<1e-5);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;Assert.Equal(56,after.Bodies.Values.Single().Geometry.VolumeMm3,5);Assert.Equal(1,P(after.Sketches[sketch.Id],mid).Y,6);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var conflict=after.Sketches[sketch.Id] with{Constraints=after.Sketches[sketch.Id].Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),b,new(99,99)))};
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(conflict,solver)));Assert.Same(after,session.Snapshot);
        var path=files.PathFor("driven-triangle.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(56,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);Assert.Equal(after.Sketches[sketch.Id].Constraints.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Constraints.ToArray());
    }
    [Fact] public async Task EditorAcceptsUnorderedTypedSelectionsAndPersistenceRejectsMasqueradedOldConstraints()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Relations",RigidTransform3d.Identity));d.AddLine(new(0,-5),new(0,5));
        d.AddPoint(new(-3,2));d.AddPoint(new(3,2));d.AddPoint(new(0,1));var axis=d.Value.Lines[0];var a=d.Value.Points[^3].Id;var b=d.Value.Points[^2].Id;var midpoint=d.Value.Points[^1].Id;
        d.Change(s=>s with{Constraints=[Fix(s,axis.Start),Fix(s,axis.End),Fix(s,a)]});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[d.Value.Id],false);
            editor.Select(axis.Id);editor.Select(midpoint,true);editor.ConstraintKind="Midpoint";editor.AddConstraintCommand.Execute(null);Assert.Single(editor.Sketch.Constraints.OfType<MidpointConstraint>());
            editor.Select(b);editor.Select(axis.Id,true);editor.Select(a,true);editor.ConstraintKind="Symmetry";editor.AddConstraintCommand.Execute(null);Assert.Single(editor.Sketch.Constraints.OfType<SymmetryConstraint>());
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var path=files.PathFor("relations.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Equal(after.Sketches[d.Value.Id].Constraints.ToArray(),loaded.Snapshot.Sketches[d.Value.Id].Constraints.ToArray());
            Assert.Equal(0,P(loaded.Snapshot.Sketches[d.Value.Id],midpoint).Y,6);
        }
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=7}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        var legacy=files.PathFor("legacy7.cadoryx");await storage.SaveAsync(before,assets,legacy);
        SketchLegacyPointFixture.Prepare(legacy);FormatEvolutionTests.RewriteManifest(legacy,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=7}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(legacy,assets);SketchLegacyPointFixture.SamePoints(before.Sketches[d.Value.Id],migrated.Snapshot.Sketches[d.Value.Id]);
    }
}
