using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class FiniteSurfaceNewBodyTests
{
    private static async Task MoveLimit(CadDocumentSession session,MemoryAssetStore assets,FeatureId id)
    {
        using var shape=OcctGeometryBridge.ReadShape(session.Snapshot.Features[id].Result,assets);
        using var transform=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(0,0,1));using var moved=shape.Transformed(transform);
        using var stored=OcctGeometryBridge.StoreShape(moved,assets);
        await session.ExecuteAsync(new RecomputeCommand(id,new ImportedRecipe(stored.Geometry)));
    }
    [Fact]
    public async Task LimitRevisionFreezesOriginalResultUntilCallerExplicitlyReselectsCurrentFace()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent limit revision"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var limit=recipe.Options!.LimitFace!.FeatureId;var part=session.Snapshot.Features[limit].PartId;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Independent",part));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            await MoveLimit(session,assets,limit);var frozen=session.Snapshot;
            Assert.True(frozen.Features[feature.Id].IsStale);Assert.Equal(feature.Result,frozen.Features[feature.Id].Result);
            Assert.Equal(recipe.Options,((ExtrudeRecipe)frozen.Features[feature.Id].Recipe).Options);
            var source=frozen.Features[limit];var selection=FiniteSurfaceExtrusionTests.Pick(frozen,limit,assets,_=>true);
            var explicitRecipe=recipe with{Options=recipe.Options with{LimitFace=selection,LimitGeometry=source.Result}};
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,explicitRecipe));var accepted=session.Snapshot;
            Assert.False(accepted.Features[feature.Id].IsStale);Assert.Empty(accepted.Features[feature.Id].Inputs);
            Assert.InRange(Math.Abs(112-accepted.Features[feature.Id].Result.VolumeMm3),0,1e-5);
            await session.UndoAsync();Assert.Same(frozen,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Fact]
    public async Task SameVolumeLocatedElsewhereCannotPassIndependentMaterialReplay()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent foreign result"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");
            using var foreign=await kernel.EvaluateAsync(new BoxRecipe(4,4,6,RigidTransform3d.Translate(100,0,0)),assets);
            Assert.InRange(Math.Abs(Expected("plane",6)-foreign.Geometry.VolumeMm3),0,1e-7);
            int count=assets.Count;await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,foreign.Geometry,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Fact]
    public async Task IndependentPartOwnsItsExplicitLimitAndDoesNotRebindWhenOriginalLimitMoves()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Cloned independent finite"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var limit=recipe.Options!.LimitFace!.FeatureId;var part=session.Snapshot.Features[limit].PartId;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Independent",part));var original=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Clone",RigidTransform3d.Translate(50,0,0)));
            var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
            var copied=session.Snapshot.Features.Values.Single(f=>f.PartId==clone.ResultPartId&&f.Recipe is ExtrudeRecipe);
            var options=((ExtrudeRecipe)copied.Recipe).Options!;Assert.Null(options.Source);Assert.Null(options.SupportFace);Assert.Empty(copied.Inputs);
            Assert.NotEqual(limit,options.LimitFace!.FeatureId);Assert.Equal(copied.PartId,session.Snapshot.Features[options.LimitFace.FeatureId].PartId);
            await MoveLimit(session,assets,limit);Assert.True(session.Snapshot.Features[original.Id].IsStale);Assert.False(session.Snapshot.Features[copied.Id].IsStale);
            Assert.Equal(copied.Result,session.Snapshot.Features[copied.Id].Result);string file=files.PathFor("cloned-newbody.cadoryx");await session.SaveAsync(storage,file);
            using var loaded=await storage.LoadAsync(file,assets);loaded.Snapshot.Validate();Assert.True(loaded.Snapshot.Features[original.Id].IsStale);Assert.False(loaded.Snapshot.Features[copied.Id].IsStale);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Fact]
    public async Task LinkedRadiusParameterUpdatesNewMaterialAndDrawingAndRejectsIncompleteFiniteDomainAtomically()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Driven independent cap"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var part=session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId].PartId;
            var draft=new SketchDraft(CadSketch.Create(part,"Independent circle",recipe.Placement));draft.AddCircle(new(5,5),2);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var reference=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe with{Profile=SketchProfile.FromCircle(new(5,5),2)},"Linked independent",part,sketchSource:reference));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);var sheet=TechnicalDrawingSheet.A4Landscape("Independent driver");
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
            var radius=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"3 mm");
            var book=new ParameterBook([radius],[new(Guid.NewGuid(),radius.Id,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<RadiusConstraint>().Single().Id))]);
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var driven=session.Snapshot;
            Assert.Empty(driven.Features[feature.Id].Inputs);Assert.InRange(Math.Abs(54*Math.PI-driven.Features[feature.Id].Result.VolumeMm3),0,1e-5);
            var view=driven.DrawingSheets[sheet.Id].Views.Single();Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Contains(view.Sources,s=>s.Feature==feature.Id&&s.Revision==driven.Features[feature.Id].Result.Revision);
            int count=assets.Count;var history=session.HistoryUsage;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[radius with{Expression="6 mm"}]})));
            Assert.Same(driven,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(history,session.HistoryUsage);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(driven,session.Snapshot);
            string file=files.PathFor("driven-newbody.cadoryx");await session.SaveAsync(storage,file);
            using(var loaded=await storage.LoadAsync(file,assets))
            await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                await reopened.ExecuteAsync(new SetParametersCommand(book with{Parameters=[radius with{Expression="2.5 mm"}]}));
                Assert.InRange(Math.Abs(37.5*Math.PI-reopened.Snapshot.Features[feature.Id].Result.VolumeMm3),0,1e-5);
                Assert.Null(reopened.Snapshot.DrawingSheets[sheet.Id].Views.Single().StaleReason);
            }
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Theory][InlineData("cancel")][InlineData("close")][InlineData("generation")]
    public async Task HeldExactNewBodyCannotPublishOrRetainPrivateSeedAfterLateCancelCloseOrGenerationChange(string kind)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Late independent finite"),assets,native,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var part=session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId].PartId;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Independent",part));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var document=new CadDocumentViewModel(session,native,new CadMessageLog());var gate=new HeldNewBodyKernel(native);var editor=new ExtrusionEditorViewModel(document,gate,feature.Id);
            editor.StartZ=6.5;
            var before=session.Snapshot;int count=assets.Count;Task pending=editor.PreviewCommand.ExecuteAsync(null);Task? close=null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.InRange(Math.Abs(88-gate.CompletedVolume),0,1e-5);
                // Exactly one completed candidate remains; its private seed has
                // already been released before this externally held native result.
                Assert.Equal(count+1,assets.Count);
                if(kind=="close")close=editor.DisposeAsync().AsTask();
                else if(kind=="generation")
                {await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(50,0,0)),"Concurrent edit",part));before=session.Snapshot;count++;Assert.True(editor.IsStale);}
                else editor.CancelCommand.Execute(null);
                gate.Release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(15));if(close is not null)await close.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(editor.CanConfirm);Assert.Null(document.PreviewScene);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            }
            finally{gate.Release.TrySetResult();await pending;await editor.DisposeAsync();document.Detach();}
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    private sealed class HeldNewBodyKernel(OcctGeometryKernel native):IGeometryKernel,IExactTopologyResolver
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal double CompletedVolume {get;private set;}
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {var result=await native.EvaluateAsync(recipe,assets,CancellationToken.None);CompletedVolume=result.Geometry.VolumeMm3;Entered.TrySetResult();await Release.Task;return result;}
        public Task ConfirmAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)=>native.ConfirmAsync(snapshot,selection,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,options,token);
    }
}
