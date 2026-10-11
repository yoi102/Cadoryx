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

public sealed class SketchProjectionTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ProjectionDepthLimitDoesNotDependOnDictionaryOrder(bool reverse)
    {
        var part=DefinitionId.New();var chain=new List<CadSketch>();
        var source=new SketchDraft(CadSketch.Create(part,"0",RigidTransform3d.Identity));source.AddCircle(new(10,0),2);
        chain.Add(source.Value);
        for(int i=1;i<=65;i++)
        {
            var target=new SketchDraft(CadSketch.Create(part,i.ToString(),RigidTransform3d.Identity));
            target.Project(chain[^1],chain[^1].Circles[0].Id);chain.Add(target.Value);
        }
        var allowed=chain.Take(65);if(reverse)allowed=allowed.Reverse();
        Assert.Equal(65,SketchProjectionGeometry.Order(allowed.ToDictionary(s=>s.Id)).Count);
        var tooDeep=reverse?chain.AsEnumerable().Reverse():chain;
        Assert.Throws<CadValidationException>(()=>SketchProjectionGeometry.Order(tooDeep.ToDictionary(s=>s.Id)));
    }
    [Theory] [InlineData("line")] [InlineData("circle")] [InlineData("arc")]
    public void ProjectionUsesPlaneTransformsAnchorsAndExplicitDetach(string kind)
    {
        var part=DefinitionId.New();var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Translate(1,2,9)));
        if(kind=="line")source.AddLine(new(0,0),new(10,0));else if(kind=="circle")source.AddCircle(new(10,0),2);else source.AddArc(new(8,0),new(10,2),new(12,0));
        var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Translate(3,4,5)));
        var id=SketchAnalyticalCurve.Ids(source.Value).Single();target.Project(source.Value,id);var p=target.Value.Projections.Single();
        var first=SketchProjectionGeometry.Points(target.Value,p.TargetEntity)[0];
        var original=SketchProjectionGeometry.Points(source.Value,id)[0];
        Assert.Equal(new Point2d(P(source.Value,original).X-2,P(source.Value,original).Y-2),P(target.Value,first));
        var before=target.Value;Assert.Throws<CadValidationException>(()=>target.MovePoint(first,new(50,60)));Assert.Same(before,target.Value);
        Assert.Throws<CadValidationException>(()=>target.TrimCurve(p.TargetEntity,new(8,0)));Assert.Same(before,target.Value);
        target.OffsetCurve(p.TargetEntity,1);Assert.Single(target.Value.Projections);
        target.Undo();target.DetachProjection(p.TargetEntity);Assert.Empty(target.Value.Projections);Assert.Empty(target.Value.Constraints);
        target.MovePoint(first,new(50,60));Assert.Equal(new Point2d(50,60),P(target.Value,first));
    }
    [Fact] public void ObliqueLinesAndEllipsesProjectExactlyAndCollapsedLinesAreRejected()
    {
        var part=DefinitionId.New();var source=new SketchDraft(CadSketch.Create(part,"Source",new(new(1,2,5),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/3))));
        source.AddLine(new(0,0),new(0,10));var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Identity));
        target.Project(source.Value,source.Value.Lines[0].Id);var line=target.Value.Lines[0];Assert.Equal(7,P(target.Value,line.End).Y,7);
        source.AddCircle(new(10,0),2);target.Project(source.Value,source.Value.Circles[0].Id);
        Assert.Equal(1,target.Value.Ellipses.Single().Geometry.Principal().MinorRadius,7);var before=target.Value;
        var normal=new SketchDraft(CadSketch.Create(part,"Normal",new(new(0,0,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2))));
        normal.AddLine(new(0,0),new(0,10));Assert.Throws<CadValidationException>(()=>target.Project(normal.Value,normal.Value.Lines[0].Id));Assert.Same(before,target.Value);
        var reversed=new SketchDraft(CadSketch.Create(part,"Reversed",new(new(0,0,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI))));
        reversed.AddArc(new(8,0),new(10,2),new(12,0));target.Project(reversed.Value,reversed.Value.Arcs[0].Id);
        Assert.Equal(-2,P(target.Value,target.Value.Arcs[^1].Middle).Y,7);
    }
    [Fact] public async Task ProjectionChainRecomputesBodiesAndPreservesSourceEditorPreviewIdentityUndoAndReopen()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddCircle(new(10,0),2);
        await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var s=session.Snapshot.Sketches[source.Value.Id];
        var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Translate(0,0,5)));target.Project(s,s.Circles[0].Id);
        await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var t=session.Snapshot.Sketches[target.Value.Id];
        var chain=new SketchDraft(CadSketch.Create(part,"Chain",RigidTransform3d.Translate(0,0,10)));chain.Project(t,t.Circles[0].Id);
        chain.SetConstruction([chain.Value.Circles[0].Id],false);await session.ExecuteAsync(new UpsertSketchCommand(chain.Value,solver));var c=session.Snapshot.Sketches[chain.Value.Id];
        var reference=SketchProfileReference.CreateCircle(c,c.Circles[0].Id);
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,c.Plane)),"Projection body",part,sketchSource:reference));
        var feature=Assert.Single(session.Snapshot.Features.Values);var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,s,false);editor.Select(s.Circles[0].Id);editor.CircleRadius=3;editor.ApplyCoordinatesCommand.Execute(null);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Equal(s.Id,editor.Sketch.Id);
            Assert.Same(before,session.Snapshot);Assert.Contains(document.PreviewScene!.Items,i=>Math.Abs(i.Geometry.VolumeMm3-18*Math.PI)<1e-6);
            await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;after.Validate();Assert.Equal(18*Math.PI,after.Features[feature.Id].Result.VolumeMm3,6);
        Assert.Equal(after.Sketches[s.Id].Revision,after.Sketches[t.Id].Projections[0].SourceRevision);
        Assert.Equal(after.Sketches[t.Id].Revision,after.Sketches[c.Id].Projections[0].SourceRevision);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var path=files.PathFor("projection.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))
        await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            loaded.Snapshot.Validate();var d=new SketchDraft(loaded.Snapshot.Sketches[s.Id]);d.MovePoint(d.Value.Circles[0].Center,new(20,4));
            await reopened.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
            Assert.Equal(new Point2d(20,4),P(reopened.Snapshot.Sketches[c.Id],c.Circles[0].Center));
        }
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(e=>e.Kind=="sketches"?e with{SchemaVersion=6}:e).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RemoveSketchCommand(s.Id)));Assert.Same(after,session.Snapshot);
    }
    [Fact] public async Task SourceSplitConflictAndCycleRejectTheWholeTransaction()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddLine(new(0,0),new(10,0));
        source.AddLine(new(3,-2),new(3,2));source.AddLine(new(7,-2),new(7,2));
        await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var s=session.Snapshot.Sketches[source.Value.Id];
        var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Identity));target.Project(s,s.Lines[0].Id);
        var line=target.Value.Lines[0];target.Change(v=>v with{Constraints=v.Constraints.Add(new LengthConstraint(SketchConstraintId.New(),line.Id,10))});
        await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var t=session.Snapshot.Sketches[target.Value.Id];var before=session.Snapshot;
        var split=new SketchDraft(s);split.TrimCurve(s.Lines[0].Id,new(5,0));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(split.Value,solver)));Assert.Same(before,session.Snapshot);
        var move=new SketchDraft(s);move.MovePoint(s.Lines[0].End,new(12,0));
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(move.Value,solver)));Assert.Same(before,session.Snapshot);
        var cycle=new SketchDraft(s);cycle.Project(t,t.Lines[0].Id);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(cycle.Value,solver)));Assert.Same(before,session.Snapshot);
        var foreign=t with{PartId=DefinitionId.New()};Assert.Throws<CadValidationException>(()=>new SketchDraft(s).Project(foreign,t.Lines[0].Id));
        var stale=t with{Projections=t.Projections.SetItem(0,t.Projections[0] with{SourceRevision=Guid.NewGuid()})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(stale,solver)));Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task IndependentPartRemapsProjectionSourcesInsideItsCopiedSketchGraph()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddCircle(new(10,0),2);
        await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var s=session.Snapshot.Sketches[source.Value.Id];
        var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Identity));target.Project(s,s.Circles[0].Id);await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));
        var slot=((AssemblyDefinition)initial.Definitions[initial.RootAssemblyId]).Children[0].Id;
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new OccurrencePath(initial.Id,[]),part,ComponentSlotId.New(),"Second",RigidTransform3d.Translate(30,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(new OccurrencePath(initial.Id,[slot]));await session.ExecuteAsync(command);
        var clone=session.Snapshot.Sketches.Values.Single(v=>v.PartId==command.ResultPartId&&v.Name=="Target");
        Assert.NotEqual(s.Id,clone.Projections[0].SourceSketch);Assert.Equal(clone.PartId,session.Snapshot.Sketches[clone.Projections[0].SourceSketch].PartId);session.Snapshot.Validate();
    }
}
