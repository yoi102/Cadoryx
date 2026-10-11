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
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class FiniteSurfaceExtrusionTests
{
    [Theory][InlineData("plane")][InlineData("sphere")][InlineData("cylinder")][InlineData("reverse")][InlineData("cut")]
    public async Task RotatedTranslatedOriginalSupportsRetainExactIndependentVolumes(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var frame=new RigidTransform3d(new(30,-40,70),Quaterniond.FromEulerDegrees(23,61,-37));
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Transformed finite stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind,frame);var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Transformed until",target.PartId,target.Id));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            double expected=kind switch
            {
                "sphere"=>500+Math.PI*4*13-2*Math.PI/3*(216-Math.Pow(32,1.5)),
                "cylinder"=>500+4*(52-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
                "cut"=>1872,_=>612
            };
            Assert.InRange(Math.Abs(expected-feature.Result.VolumeMm3),0,1e-5);
            await kernel.ConfirmFiniteSurfaceExtrusionAsync((ExtrudeRecipe)feature.Recipe,feature.Result,assets);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("sphere")][InlineData("cylinder")]
    public async Task ExplicitFarCannotSilentlyAcceptNearCap(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Wrong analytic branch"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind);recipe=recipe with{Options=recipe.Options! with{SurfaceBranch=ExtrusionSurfaceBranch.Far}};
            var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);var before=session.Snapshot;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Far",target.PartId,target.Id)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("tangent")][InlineData("miss")][InlineData("unsupported")]
    public async Task TangencyMissingDirectionalIntersectionAndUnsupportedTypeCannotPublishMaterial(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Unproved cap"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"sphere");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            if(kind=="unsupported")
            {
                using var cone=ShapeFactory.CreateCone(4,2,8);using var native=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(5,5,12));
                using var moved=cone.Transformed(native);using var stored=OcctGeometryBridge.StoreShape(moved,assets);
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Cone stop",target.PartId));
                var limit=session.Snapshot.Features.Values.Single(f=>f.Name=="Cone stop");
                var selection=Pick(session.Snapshot,limit.Id,assets,f=>f.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cone);
                recipe=recipe with{Options=recipe.Options! with{LimitFace=selection,LimitGeometry=limit.Result}};
            }
            else
            {
                // The support must cover the whole profile so this reaches the
                // stop/tangency checks rather than failing at support coverage.
                var sourceId=recipe.Options!.SupportFace!.FeatureId;
                await session.ExecuteAsync(new RecomputeCommand(sourceId,new BoxRecipe(16,16,5,RigidTransform3d.Translate(-3,-3,0))));
                target=session.Snapshot.Bodies.Values.Single(b=>b.Producer==sourceId);
                var support=Pick(session.Snapshot,sourceId,assets,f=>f.GetBoundingBox().Minimum.Z>4.999);
                recipe=recipe with{Profile=SketchProfile.FromCircle(new(5,5),kind=="tangent"?6:7),Options=recipe.Options with{Source=target.Geometry,SupportFace=support}};
            }
            var before=session.Snapshot;int count=assets.Count;var history=session.HistoryUsage;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Rejected",target.PartId,target.Id)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(history,session.HistoryUsage);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]public async Task LinkedCircleParameterDrivesFiniteCapAndDrawingAtomicallyAcrossReopen()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Driven finite cap"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            var draft=new SketchDraft(CadSketch.Create(target.PartId,"Circle",recipe.Placement));draft.AddCircle(new(5,5),2);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var reference=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe with{Profile=SketchProfile.FromCircle(new(5,5),2)},"Linked",target.PartId,target.Id,reference));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);var sheet=TechnicalDrawingSheet.A4Landscape("Surface driver");
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
            var radius=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"3 mm");
            var book=new ParameterBook([radius],[new(Guid.NewGuid(),radius.Id,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<RadiusConstraint>().Single().Id))]);
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var driven=session.Snapshot;
            Assert.InRange(Math.Abs(500+63*Math.PI-driven.Features[feature.Id].Result.VolumeMm3),0,1e-5);
            var view=driven.DrawingSheets[sheet.Id].Views.Single();Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Contains(view.Sources,s=>s.Feature==feature.Id&&s.Revision==driven.Features[feature.Id].Result.Revision);
            int count=assets.Count;var history=session.HistoryUsage;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[radius with{Expression="6 mm"}]})));
            Assert.Same(driven,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(history,session.HistoryUsage);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(driven,session.Snapshot);
            string path=files.PathFor("driven.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                await reopened.ExecuteAsync(new SetParametersCommand(book with{Parameters=[radius with{Expression="2.5 mm"}]}));
                Assert.InRange(Math.Abs(500+43.75*Math.PI-reopened.Snapshot.Features[feature.Id].Result.VolumeMm3),0,1e-5);
                Assert.Null(reopened.Snapshot.DrawingSheets[sheet.Id].Views.Single().StaleReason);reopened.Snapshot.Validate();
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]public async Task IndependentPartOwnsBothExactSelectionsAndSurvivesOriginalSourceFreeze()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent finite stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));var original=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,target.PartId,slot,"Independent",RigidTransform3d.Translate(50,0,0)));
            var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
            var feature=session.Snapshot.Features.Values.Single(f=>f.PartId==clone.ResultPartId&&f.Recipe is ExtrudeRecipe);
            var options=((ExtrudeRecipe)feature.Recipe).Options!;
            Assert.NotEqual(recipe.Options!.SupportFace!.FeatureId,options.SupportFace!.FeatureId);
            Assert.NotEqual(recipe.Options.LimitFace!.FeatureId,options.LimitFace!.FeatureId);
            Assert.Equal(feature.PartId,session.Snapshot.Features[options.SupportFace.FeatureId].PartId);
            Assert.Equal(feature.PartId,session.Snapshot.Features[options.LimitFace.FeatureId].PartId);
            Assert.Equal(recipe.Options.Source,options.Source);Assert.Equal(recipe.Options.LimitGeometry,options.LimitGeometry);
            await session.ExecuteAsync(new RecomputeCommand(recipe.Options.SupportFace.FeatureId,new BoxRecipe(10,10,6,RigidTransform3d.Identity)));
            Assert.True(session.Snapshot.Features[original.Id].IsStale);Assert.False(session.Snapshot.Features[feature.Id].IsStale);
            Assert.Equal(612,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
            string path=files.PathFor("independent.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.True(loaded.Snapshot.Features[original.Id].IsStale);Assert.False(loaded.Snapshot.Features[feature.Id].IsStale);}
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]public async Task SuppressionReplayAndPrecancelKeepExactAuthorAndMaterial()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Suppressed finite stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"cylinder");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var before=session.Snapshot;int count=assets.Count;using(var cancel=new CancellationTokenSource())
            {cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,feature.Recipe),cancel.Token));}
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            await session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,true));Assert.True(session.Snapshot.Features[feature.Id].IsSuppressed);
            string path=files.PathFor("suppressed.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                await reopened.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,false));
                Assert.False(reopened.Snapshot.Features[feature.Id].IsSuppressed);Assert.Equal(feature.Result.VolumeMm3,reopened.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
                Assert.Equal(recipe.Options,((ExtrudeRecipe)reopened.Snapshot.Features[feature.Id].Recipe).Options);
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]public async Task ChangedStopFreezesOldAuthorUntilExplicitSelectionAndCanStoreBothStates()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Changed finite stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            using(var stop=SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,new(new(5,5,14),new(1,0,0),new(0,1,0)),new(-5,5,-5,5)))
            using(var stored=OcctGeometryBridge.StoreShape(stop,assets))
                await session.ExecuteAsync(new RecomputeCommand(recipe.Options!.LimitFace!.FeatureId,new ImportedRecipe(stored.Geometry)));
            Assert.True(session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(feature.Result,session.Snapshot.Features[feature.Id].Result);
            string frozen=files.PathFor("frozen.cadoryx");await session.SaveAsync(storage,frozen);
            using(var loaded=await storage.LoadAsync(frozen,assets)){Assert.True(loaded.Snapshot.Features[feature.Id].IsStale);Assert.Equal(recipe.Options,((ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Options);}
            var limiter=session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId];var selection=Pick(session.Snapshot,limiter.Id,assets,_=>true);
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Options=recipe.Options with{LimitFace=selection,LimitGeometry=limiter.Result}}));
            Assert.False(session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(644,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
            string selected=files.PathFor("reselected.cadoryx");await session.SaveAsync(storage,selected);
            using(var loaded=await storage.LoadAsync(selected,assets)){loaded.Snapshot.Validate();Assert.False(loaded.Snapshot.Features[feature.Id].IsStale);}
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("cancel")][InlineData("close")][InlineData("generation")]
    public async Task HeldExactNativeSurfaceResultCannotPublishAfterCancelCloseOrGenerationChange(string kind)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Late finite cap"),assets,native,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var document=new CadDocumentViewModel(session,native,new CadMessageLog());var gate=new HeldSurfaceKernel(native);var editor=new ExtrusionEditorViewModel(document,gate,feature.Id);
            var before=session.Snapshot;int count=assets.Count;Task pending=editor.PreviewCommand.ExecuteAsync(null);Task? close=null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if(kind=="close")close=editor.DisposeAsync().AsTask();
                else if(kind=="generation")
                {
                    await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(50,0,0)),"Concurrent edit",target.PartId));
                    before=session.Snapshot;count++;Assert.True(editor.IsStale);
                }
                else editor.CancelCommand.Execute(null);
                gate.Release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(15));if(close is not null)await close.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(editor.CanConfirm);Assert.Null(document.PreviewScene);Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            }
            finally{gate.Release.TrySetResult();await pending;await editor.DisposeAsync();document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }

    private sealed class HeldSurfaceKernel(OcctGeometryKernel native):IGeometryKernel,IExactTopologyResolver
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;
        public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            var result=await native.EvaluateAsync(recipe,assets,CancellationToken.None);Entered.TrySetResult();await Release.Task;return result;
        }
        public Task ConfirmAsync(DocumentSnapshot snapshot,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)=>native.ConfirmAsync(snapshot,selection,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,options,token);
    }
}
