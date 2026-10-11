using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class FeatureTimelineTests
{
    [Theory][InlineData(false)][InlineData(true)] public async Task CursorMotionOrCloseDiscardsLateNativeInsertionAndReleasesItsAssets(bool close)
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;int count=f.Assets.Count;
        await f.Session.RollbackFeaturesAsync(f.Part,1);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(new DelayedInsertion(started,release,
            new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"Late",f.Part)),f.Session.FeatureCursor!));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if(close)
            {
                var closing=f.Session.DisposeAsync().AsTask();release.SetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);await closing;Assert.Equal(0,f.Assets.Count);
            }
            else
            {
                await f.Session.RollbackFeaturesAsync(f.Part,2);release.SetResult();
                await Assert.ThrowsAsync<StaleDocumentException>(()=>pending);Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
                Assert.Equal(2,f.Session.FeatureCursor!.Position);
            }
        }
        finally
        {
            // Always release the deliberate test barrier before fixture disposal,
            // including failed readiness/assertions. The original 20s bound stays.
            release.TrySetResult();
        }
    }

    [Fact] public async Task InsertionPreservesAssociatedDrawingSourcesAndAuthorFieldsAcrossReopen()
    {
        await using var f=new Fixture();await f.Initialize();var sheet=TechnicalDrawingSheet.A4Landscape("History drawing");var view=Guid.NewGuid();
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Front",DrawingViewKind.Front,new(80,100),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Detail",DrawingViewKind.Detail,new(180,100),2,
            parentView:view,detailCenter:new(0,0),detailRadius:10));
        var doc=f.Session.Snapshot;var body=doc.Bodies[f.Cut.OutputBodyId];
        using(var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets))using(var map=OcctSharp.RepairSnapshot.Create(shape))
        {
            var source=await f.Kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single(o=>o.DefinitionId==f.Part).Path,
                body.Id,map.Topology.First(t=>t.Kind==OcctSharp.ShapeKind.Face).Selection.Index,map.Fingerprint,f.Assets);
            var datum=new DrawingDatumSymbol(Guid.NewGuid(),view,"A",source,new(30,30));
            await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(sheet.Id,datum));
            await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(sheet.Id,
                new DrawingFeatureControlFrame(Guid.NewGuid(),view,
                    await f.Kernel.ResolveDrawingTopologyAsync(doc,source.Path,body.Id,map.Topology.First(t=>t.Kind==OcctSharp.ShapeKind.Face&&t.Selection.Index!=source.FullTopologyIndex).Selection.Index,map.Fingerprint,f.Assets),
                    new(60,30),DrawingGeometricCharacteristic.Position,.1){Datums=[datum.Id]}));
        }
        using var files=new TestFiles();var path=files.PathFor("drawing.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        // Loaded parent/detail sources have distinct array storage, while their exact values agree.
        using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);
        await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
        var before=reopened.Snapshot;await reopened.RollbackFeaturesAsync(f.Part,1);
        await reopened.ExecuteAsync(new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"New",f.Part),reopened.FeatureCursor!));
        var after=reopened.Snapshot;after.Validate();
        Assert.Equal(sheet.Title,after.DrawingSheets[sheet.Id].Title);
        for(int i=0;i<2;i++)Assert.Equal(before.DrawingSheets[sheet.Id].Views[i].Sources.AsEnumerable(),after.DrawingSheets[sheet.Id].Views[i].Sources.AsEnumerable());
        Assert.All(after.DrawingSheets[sheet.Id].Views,v=>Assert.Null(v.StaleReason));
        Assert.Equal(before.DrawingSheets[sheet.Id].DatumSymbols.ToArray(),after.DrawingSheets[sheet.Id].DatumSymbols.ToArray());
        Assert.Null(after.DrawingSheets[sheet.Id].ControlFrames[0].StaleReason);
        await reopened.UndoAsync();Assert.Same(before,reopened.Snapshot);
    }

    [Fact] public async Task InvalidStoredChronologyRequiresExplicitOrderRepairBeforeRollback()
    {
        await using var f=new Fixture();await f.Initialize();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Unordered legacy history",doc=>doc with
            {Definitions=doc.Definitions.SetItem(f.Part,((PartDefinition)doc.Definitions[f.Part]) with{Features=[f.Cut.Id,f.Base.Id,f.Tool.Id]})}));
        var before=f.Session.Snapshot;long generation=f.Session.Generation;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.RollbackFeaturesAsync(f.Part,2));
        Assert.Same(before,f.Session.Snapshot);Assert.False(f.Session.IsRolledBack);Assert.Equal(generation,f.Session.Generation);
        await f.Session.ExecuteAsync(new ReorderFeaturesCommand(f.Part,[f.Base.Id,f.Tool.Id,f.Cut.Id]));
        await f.Session.RollbackFeaturesAsync(f.Part,1);Assert.Equal(f.Base.OutputBodyId,f.Session.DisplaySnapshot.Bodies.Values.Single().Id);
    }

    [Fact] public async Task CursorInsertionInAnotherPartOrChangingRetainedFeatureStyleRejectsAtomically()
    {
        await using var f=new Fixture();await f.Initialize();var other=DefinitionId.New();var slot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Other part",doc=>doc with{Definitions=doc.Definitions
            .Add(other,new PartDefinition(other,"Other",[],[])).SetItem(doc.RootAssemblyId,((AssemblyDefinition)doc.Definitions[doc.RootAssemblyId]) with
                {Children=((AssemblyDefinition)doc.Definitions[doc.RootAssemblyId]).Children.Add(new(slot,other,"Other",RigidTransform3d.Identity))})}));
        var before=f.Session.Snapshot;int count=f.Assets.Count;await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(
            new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Identity),"Wrong part",other),cursor)));
        Assert.Equal(count,f.Assets.Count);Assert.Same(before,f.Session.Snapshot);
        using var prepared=await new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"New",f.Part),cursor)
            .PrepareAsync(new(before,f.Session.Generation,f.Assets,f.Kernel),default);
        var cut=before.Features[f.Cut.Id];var forged=prepared.Snapshot with{Features=prepared.Snapshot.Features.SetItem(cut.Id,cut with
            {OutputMetadata=cut.OutputMetadata! with{Name="Changed retained tail"}})};
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new CommitPreparedDocumentCommand(forged,f.Session.Generation,"Forged",cursor)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(cursor,f.Session.FeatureCursor);
    }

    [Fact] public async Task SuppressedOrStalePrefixesStayHiddenAndLockedInsertionIsAtomic()
    {
        await using var f=new Fixture();await f.Initialize();
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Base.Id,true));var suppressed=f.Session.Snapshot;
        await f.Session.RollbackFeaturesAsync(f.Part,1);Assert.Empty(f.Session.DisplaySnapshot.Bodies);
        await f.Session.RollbackFeaturesAsync(f.Part,2);Assert.Equal(f.Tool.OutputBodyId,f.Session.DisplaySnapshot.Bodies.Values.Single().Id);
        Assert.Same(suppressed,f.Session.Snapshot);await f.Session.ReturnToHistoryEndAsync();await f.Session.UndoAsync();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Lock",doc=>doc with{Layers=doc.Layers.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsLocked=true})}));
        var before=f.Session.Snapshot;int assets=f.Assets.Count;await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(
            new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Identity),"Locked",f.Part),cursor)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(cursor,f.Session.FeatureCursor);Assert.Equal(assets,f.Assets.Count);
    }

    [Fact] public async Task ForgedCursorInsertionCannotChangeDocumentLayerBodyOrDrawingAuthorData()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;
        await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;
        using var prepared=await new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"New",f.Part),cursor)
            .PrepareAsync(new(before,f.Session.Generation,f.Assets,f.Kernel),default);
        var valid=prepared.Snapshot;var layer=before.Layers.Keys.Single();
        var forgeries=new[]{valid with{Name="Replaced document"},valid with{Layers=valid.Layers.SetItem(layer,valid.Layers[layer] with{IsLocked=true})},
            valid with{Bodies=valid.Bodies.SetItem(f.Cut.OutputBodyId,valid.Bodies[f.Cut.OutputBodyId] with{Name="Changed tail"})},
            valid with{DrawingSheets=valid.DrawingSheets.Add(Guid.NewGuid(),TechnicalDrawingSheet.A4Landscape("Forged drawing"))}};
        foreach(var forged in forgeries)
        {
            await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new CommitPreparedDocumentCommand(forged,f.Session.Generation,"Forged",cursor)));
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(cursor,f.Session.FeatureCursor);
        }
        await f.Session.ExecuteAsync(new CommitPreparedDocumentCommand(valid,f.Session.Generation,"New",cursor));f.Session.Snapshot.Validate();
    }

    [Fact] public async Task SharedPartOccurrencesKeepTheirPathsAndOtherPartsStayAtTheEnd()
    {
        await using var f=new Fixture();await f.Initialize();var other=DefinitionId.New();var sharedSlot=ComponentSlotId.New();var otherSlot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Shared history sample",doc=>
        {
            var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
            return doc with{Definitions=doc.Definitions.Add(other,new PartDefinition(other,"Other",[],[])).SetItem(root.Id,root with
                {Children=root.Children.Add(new(sharedSlot,f.Part,"Shared",RigidTransform3d.Translate(20,0,0))).Add(new(otherSlot,other,"Other",RigidTransform3d.Translate(50,0,0)))})};
        }));
        await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,3,RigidTransform3d.Identity),"Other body",other));
        var before=f.Session.Snapshot;int count=f.Assets.Count;var otherBody=before.Bodies.Values.Single(b=>b.PartId==other);
        await f.Session.RollbackFeaturesAsync(f.Part,1);var view=f.Session.DisplaySnapshot;var scene=CadScene.FromDocument(view);
        Assert.Equal(3,scene.Items.Length);Assert.Equal(2,scene.Items.Count(i=>i.BodyId==f.Base.OutputBodyId));
        Assert.Contains(scene.Items,i=>i.BodyId==f.Base.OutputBodyId&&i.Path.Slots[^1]==sharedSlot&&i.WorldTransform==RigidTransform3d.Translate(20,0,0));
        Assert.Contains(scene.Items,i=>i.BodyId==otherBody.Id&&i.Geometry==otherBody.Geometry);Assert.Equal(before.Definitions[other],view.Definitions[other]);
        Assert.Equal(count,f.Assets.Count);Assert.Same(before,f.Session.Snapshot);
        await f.Session.ReturnToHistoryEndAsync();Assert.Same(before,f.Session.DisplaySnapshot);
    }
    [Fact] public async Task CursorDisplaysCachedIntermediateBodiesWithoutMutationAndProtectsTemporaryOutput()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;int assets=f.Assets.Count;var usage=f.Session.HistoryUsage;
        using var files=new TestFiles();
        await f.Session.RollbackFeaturesAsync(f.Part,1);Assert.True(f.Session.IsRolledBack);Assert.Same(before,f.Session.Snapshot);
        var view=f.Session.DisplaySnapshot;Assert.True(view.IsTemporaryHistoryView);Assert.Equal(1000,view.Bodies.Values.Single().Geometry.VolumeMm3,6);
        Assert.Equal(f.Base.OutputBodyId,view.Bodies.Values.Single().Id);Assert.Equal(f.Base.Result,view.Bodies.Values.Single().Geometry);
        Assert.Equal(1000,CadScene.FromDocument(view).Items.Single().Geometry.VolumeMm3,6);Assert.Equal(assets,f.Assets.Count);Assert.Equal(usage,f.Session.HistoryUsage);
        await Assert.ThrowsAsync<CadValidationException>(()=>new CadDocumentStorage().SaveAsync(view,f.Assets,files.PathFor("temporary.cadoryx")));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Kernel.ExportAsync(view,f.Assets,files.PathFor("temporary.step")));
        // Saving the session still captures the unchanged complete graph, never its temporary view.
        await f.Session.SaveAsync(new CadDocumentStorage(),files.PathFor("complete.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("complete.cadoryx"),f.Assets))
        {Assert.Equal(before.StateId,loaded.Snapshot.StateId);Assert.Equal(f.Cut.Result,loaded.Snapshot.Features[f.Cut.Id].Result);Assert.Single(loaded.Snapshot.Bodies);}
        Assert.False(f.Session.IsDirty);
        await f.Session.RollbackFeaturesAsync(f.Part,0);Assert.Empty(f.Session.DisplaySnapshot.Bodies);
        await f.Session.RollbackFeaturesAsync(f.Part,2);Assert.Equal(2,f.Session.DisplaySnapshot.Bodies.Count);
        await f.Session.ReturnToHistoryEndAsync();Assert.False(f.Session.IsRolledBack);Assert.Same(before,f.Session.DisplaySnapshot);Assert.Equal(usage,f.Session.HistoryUsage);
    }

    [Fact] public async Task NewHoleUsesIntermediateSolidAndPreservesTheExactExistingDownstreamBranch()
    {
        await using var f=new Fixture();await f.Initialize();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Tail style",doc=>doc with
            {Bodies=doc.Bodies.SetItem(f.Cut.OutputBodyId,doc.Bodies[f.Cut.OutputBodyId] with{Appearance=new(0xFF123456),Name="Styled tail",IsVisible=false})}));
        var before=f.Session.Snapshot;
        await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;
        var operation=new AddHoleCommand(f.Base.OutputBodyId,new(f.Base.Result,RigidTransform3d.Translate(0,0,10),new(7,7),2,Through:false,Depth:2),"Inserted hole");
        await f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(operation,cursor));var after=f.Session.Snapshot;after.Validate();Assert.False(f.Session.IsRolledBack);
        var added=after.Features.Values.Single(p=>p.Recipe is HoleRecipe);var part=(PartDefinition)after.Definitions[f.Part];
        Assert.Equal(new[]{f.Base.Id,added.Id,f.Tool.Id,f.Cut.Id},part.Features);Assert.Equal(f.Base.Id,added.Inputs.Single());
        Assert.Equal(f.Cut,after.Features[f.Cut.Id]);Assert.Equal(2,after.Bodies.Count);Assert.Contains(f.Cut.OutputBodyId,after.Bodies.Keys);
        Assert.Equal(before.Bodies[f.Cut.OutputBodyId],after.Bodies[f.Cut.OutputBodyId]);
        Assert.Equal(1000-2*Math.PI,added.Result.VolumeMm3,5);
        using var files=new TestFiles();await f.Session.SaveAsync(new CadDocumentStorage(),files.PathFor("inserted.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("inserted.cadoryx"),f.Assets))
        {Assert.Equal(part.Features.AsEnumerable(),((PartDefinition)loaded.Snapshot.Definitions[f.Part]).Features.AsEnumerable());Assert.Equal(f.Cut.Result,loaded.Snapshot.Features[f.Cut.Id].Result);}
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Fact] public async Task IllegalReorderAndCursorEditingRejectAtomicallyWhileIndependentOrderPreservesOperands()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;int assets=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new ReorderFeaturesCommand(f.Part,[f.Cut.Id,f.Base.Id,f.Tool.Id])));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);
        await f.Session.ExecuteAsync(new ReorderFeaturesCommand(f.Part,[f.Tool.Id,f.Base.Id,f.Cut.Id]));var reordered=f.Session.Snapshot;
        Assert.Equal(f.Cut,reordered.Features[f.Cut.Id]);Assert.Equal(f.Base,reordered.Features[f.Base.Id]);Assert.Equal(f.Tool,reordered.Features[f.Tool.Id]);Assert.Equal(assets,f.Assets.Count);
        Assert.Equal(before.Features[f.Cut.Id].Inputs,reordered.Features[f.Cut.Id].Inputs);
        using var files=new TestFiles();await f.Session.SaveAsync(new CadDocumentStorage(),files.PathFor("reordered.cadoryx"));
        using(var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("reordered.cadoryx"),f.Assets))
            Assert.Equal(new[]{f.Tool.Id,f.Base.Id,f.Cut.Id},((PartDefinition)loaded.Snapshot.Definitions[f.Part]).Features);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(f.Base.Id,new BoxRecipe(11,10,10,RigidTransform3d.Identity))));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(new RecomputeCommand(f.Base.Id,new BoxRecipe(11,10,10,RigidTransform3d.Identity)),cursor)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(cursor,f.Session.FeatureCursor);Assert.Equal(assets,f.Assets.Count);
    }

    [Fact] public async Task CursorMotionInvalidatesPreparedCommitAndCannotBypassInsertionValidation()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;
        await f.Session.RollbackFeaturesAsync(f.Part,1);var cursor=f.Session.FeatureCursor!;long generation=f.Session.Generation;
        using var prepared=await UpdateAssociatedSections.PrepareCommandAsync(new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"New",f.Part),cursor),new(before,generation,f.Assets,f.Kernel),default);
        await f.Session.RollbackFeaturesAsync(f.Part,2);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,"New",cursor)));Assert.Same(before,f.Session.Snapshot);
        var current=f.Session.FeatureCursor!;
        var forged=before.WithNewState() with{Features=before.Features.SetItem(f.Base.Id,f.Base with{Name="Changed existing"})};
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new CommitPreparedDocumentCommand(forged,f.Session.Generation,"Forged",current)));Assert.Same(before,f.Session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.RollbackFeaturesAsync(f.Part,4));Assert.Equal(current,f.Session.FeatureCursor);
    }

    [Fact] public async Task IndirectDatumAndSketchPrerequisitesMakeChronologyReorderIllegal()
    {
        await using var f=new Fixture();await f.Initialize();
        var plane=new CadDatumPlane(Guid.NewGuid(),f.Part,"Authored top",DatumPlaneKind.Offset,DatumPlaneSource.Box(f.Base.Id,BoxBoundary.ZMax));
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=DatumBook.Empty.Planes.Add(plane.Id,plane)}));plane=f.Session.Snapshot.Datums.Planes[plane.Id];
        var d=new SketchDraft(CadSketch.Create(f.Part,"Driven section",plane.Frame));d.AddRectangle(new(0,0),new(2,2));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver(),datum:plane.Id));
        var sketch=f.Session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
        await f.Session.ExecuteAsync(new AddBodyCommand(source.Resolve(f.Session.Snapshot,f.Part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Driven",f.Part,sketchSource:source));
        var linked=f.Session.Snapshot.Features.Values.Single(p=>p.SketchSource is not null);var before=f.Session.Snapshot;
        Assert.Contains(f.Base.Id,DatumPlanes.FeatureDependencies(before)[linked.Id]);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new ReorderFeaturesCommand(f.Part,[linked.Id,f.Base.Id,f.Tool.Id,f.Cut.Id])));Assert.Same(before,f.Session.Snapshot);
        await f.Session.RollbackFeaturesAsync(f.Part,0);var cursor=f.Session.FeatureCursor!;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new InsertFeatureAtCursorCommand(new AddBodyCommand(source.Resolve(before,f.Part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Future datum",f.Part,sketchSource:source),cursor)));Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task ModelingPanelPreviewCancelConfirmAndUndoInsertAtTheDisplayedCursor()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;var document=new CadDocumentViewModel(f.Session,f.Kernel,new CadMessageLog());
        try
        {
            await f.Session.RollbackFeaturesAsync(f.Part,1);Assert.True(document.IsReadOnly);Assert.Equal(f.Base.OutputBodyId,document.Scene.Items.Single().BodyId);
            document.EditFeature(f.Cut.Id);Assert.Null(document.EditingFeature);Assert.Equal(Cadoryx.Lang.Strings.Strings.HistoryEditRequiresEnd,document.ToolStatus);
            document.ToolKind="Cylinder";document.ObjectName="Inserted cylinder";document.SizeX=1;document.SizeZ=2;document.PositionX=20;
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);Assert.Same(before,f.Session.Snapshot);
            document.CancelCommand.Execute(null);Assert.Null(document.PreviewScene);Assert.True(f.Session.IsRolledBack);
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);await document.ConfirmCommand.ExecuteAsync(null);
            var after=f.Session.Snapshot;after.Validate();Assert.False(f.Session.IsRolledBack);Assert.False(document.IsReadOnly);
            var added=after.Features.Values.Single(p=>p.Name=="Inserted cylinder");Assert.Equal(added.Id,((PartDefinition)after.Definitions[f.Part]).Features[1]);
            Assert.Equal(f.Cut,after.Features[f.Cut.Id]);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        }
        finally{document.Detach();}
    }

    private sealed class DelayedInsertion(TaskCompletionSource started,TaskCompletionSource release,ICadDocumentCommand inner):ICadDocumentCommand
    {
        public string Name=>inner.Name;
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var edit=await inner.PrepareAsync(context,token);started.SetResult();
            try{await release.Task.WaitAsync(token);return edit;}catch{edit.Dispose();throw;}
        }
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}
        public DefinitionId Part {get;private set;}public FeatureDefinition Base {get;private set;}=null!;public FeatureDefinition Tool {get;private set;}=null!;public FeatureDefinition Cut {get;private set;}=null!;
        public Fixture()=>Session=new(DocumentSnapshot.Create("History"),Assets,Kernel,new InlineSessionDispatcher());
        public async Task Initialize()
        {
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Base"));Base=Session.Snapshot.Features.Values.Single();Part=Base.PartId;
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(2,2,2)),"Tool",Part));Tool=Session.Snapshot.Features.Values.Single(f=>f.Id!=Base.Id);
            await Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[Base.OutputBodyId,Tool.OutputBodyId]));Cut=Session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            Base=Session.Snapshot.Features[Base.Id];Tool=Session.Snapshot.Features[Tool.Id];
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
