using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SignedDistanceDriverRefreshesDrawingAndStepAndFailedDriverIsAtomic(bool rotated)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(rotated:rotated);await f.Apply(await f.Push(2));var feature=f.Edited;
        var sheet=TechnicalDrawingSheet.A4Landscape("Face drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var parameter=new CadParameter(Guid.NewGuid(),"pull",ParameterDimension.Length,"-3 mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(feature.Id,"Distance"));
        var before=f.Session.Snapshot;await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=f.Session.Snapshot;after.Validate();
        Assert.Equal(1440,after.Features[feature.Id].Result.VolumeMm3,4);Assert.Equal(-3,((PlanarPushPullRecipe)after.Features[feature.Id].Recipe).Distance);
        Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.NotEmpty(v.Strokes);Assert.Equal(after.Features[feature.Id].Result.AssetId,v.Sources.Single().Asset);});
        int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new SetParametersCommand(new([parameter with{Expression="0 mm"}],[binding]))));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((PlanarPushPullRecipe)after.Features[feature.Id].Recipe) with{Distance=1})));Assert.Same(after,f.Session.Snapshot);
        var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);string path=files.PathFor("driven.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets)){Assert.Equal(-3,ParameterTargets.Read(loaded.Snapshot,binding.Target).Value);TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("drawing.pdf"));Assert.True(new FileInfo(files.PathFor("drawing.pdf")).Length>1000);}
        var step=files.PathFor("face.step");await f.Kernel.ExportAsync(after,f.Assets,step);using(var loaded=await f.Kernel.ImportAsync(step,f.Assets)){Assert.Equal(1440,loaded.Snapshot.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3),4);loaded.Snapshot.Validate();}
        Assert.Equal(count,f.Assets.Count);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SuppressionRestoresSourceAndExplicitFinalEdgeCreatesValidSuccessor(bool removal)
    {
        await using var f=await Fixture.Create(bores:removal?1:0);GeometryRecipe recipe=removal?new DefeatureRecipe(f.Source.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)):await f.Push(2);
        await f.Apply(recipe);var feature=f.Edited;
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,true));Assert.Equal(f.Source.Result,f.Session.Snapshot.Bodies.Values.Single().Geometry);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,false));var restored=f.Session.Snapshot.Features[feature.Id];
        Assert.Equal(feature.Result.AssetId,restored.Result.AssetId);Assert.Equal(restored.Result,f.Session.Snapshot.Bodies.Values.Single().Geometry);feature=restored;
        var edge=f.SelectMany(s=>{var b=s.GetBoundingBox();return Math.Abs(b.Maximum.Z)<1e-5&&Math.Abs(b.Maximum.X)<1e-5&&b.SizeY>11.99;},feature,ShapeKind.Edge).Single();
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(edge,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));var following=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HistoryFilletRecipe);
        Assert.False(following.IsStale);Assert.Equal(feature.Id,following.Inputs.Single());f.Session.Snapshot.Validate();
        if(!removal)
        {
            await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((PlanarPushPullRecipe)recipe) with{Distance=3}));var frozen=f.Session.Snapshot;
            // A pick on the old final result is not an original-source history
            // reference. Result-to-result identity is unproved, so retain it frozen.
            Assert.True(frozen.Features[following.Id].IsStale);Assert.Equal(following.Result,frozen.Features[following.Id].Result);
            var current=frozen.Features[feature.Id];var fresh=f.SelectMany(s=>{var b=s.GetBoundingBox();return Math.Abs(b.Maximum.Z)<1e-5&&Math.Abs(b.Maximum.X)<1e-5&&b.SizeY>11.99;},current,ShapeKind.Edge).Single();
            await f.Session.ExecuteAsync(new RebindExactLocalFeatureCommand(following.Id,fresh,null,.1));Assert.False(f.Session.Snapshot.Features[following.Id].IsStale);f.Session.Snapshot.Validate();
        }
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task EditorShowsExactSelectionSignedDirectionAndAtomicPreviewConfirmOrCancel(bool removal,bool commit)
    {
        await using var f=await Fixture.Create(bores:removal?1:0);var before=f.Session.Snapshot;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,removal?FaceFeatureKind.Defeature:FaceFeatureKind.PlanarPushPull);
        editor.SelectedSource=Assert.Single(editor.Sources);
        if(removal){foreach(var face in f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder))await editor.PickExactAsync(face);}
        else{await editor.PickExactAsync(f.Top());editor.PushPullDistance=-2;Assert.Contains("1",editor.PlanarNormalLabel);Assert.Equal(Vector3d.UnitZ,((PlanarPushPullRecipe)editor.BuildRecipe()).OutwardNormal);}
        Assert.Single(editor.ClosingFaces);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
        Assert.Equal(removal?1800:1560,f.Document.PreviewScene!.Items.Single().Geometry.VolumeMm3,4);
        if(commit){await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(removal?1800:1560,f.Edited.Result.VolumeMm3,4);}
        else{editor.CancelCommand.Execute(null);Assert.Same(before,f.Session.Snapshot);Assert.Null(f.Document.PreviewScene);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task TreeEditingPreservesFullRecipeAndRejectsWrongEditorKind(bool removal)
    {
        await using var f=await Fixture.Create(bores:removal?2:0);GeometryRecipe recipe=removal?new DefeatureRecipe(f.Source.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)):await f.Push(-2);
        await f.Apply(recipe);var feature=f.Edited;FeatureId? requested=null;f.Document.FaceFeatureEditRequested+=(_,id)=>requested=id;f.Document.EditFeature(feature.Id);Assert.Equal(feature.Id,requested);
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,removal?FaceFeatureKind.Defeature:FaceFeatureKind.PlanarPushPull,feature.Id);
        Assert.False(editor.CanChooseSource);Assert.Equal(FaceFeatureRecipes.Faces(recipe).ToArray(),editor.Faces.ToArray());
        if(!removal)Assert.Equal(recipe,editor.BuildRecipe());await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        Assert.Throws<CadValidationException>(()=>new FaceFeatureEditorViewModel(f.Document,f.Kernel,FaceFeatureKind.SolidOffset,feature.Id));
    }
    [Fact]public async Task ChangedPickOrDistanceInvalidatesCandidateAndCannotReuseAnotherFacesNormal()
    {
        await using var f=await Fixture.Create(bores:1);await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,FaceFeatureKind.PlanarPushPull);editor.SelectedSource=Assert.Single(editor.Sources);
        await editor.PickExactAsync(f.Top());await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);editor.PushPullDistance=0;Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
        var wall=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Single();await editor.PickExactAsync(wall);Assert.Single(editor.ClosingFaces);Assert.Equal(f.Top(),editor.ClosingFaces[0]);
        editor.ClearFacesCommand.Execute(null);editor.ClosingFaces.Add(wall);Assert.Throws<CadValidationException>(()=>editor.BuildRecipe());await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
    }
    [Theory][InlineData("cancel")][InlineData("close")][InlineData("document")][InlineData("source")]
    public async Task LatePlanarDiscoveryCannotPublishAfterCancelCloseOrCapturedStateChanges(string change)
    {
        await using var f=await Fixture.Create();var gated=new GatedFaceKernel(f.Kernel){Discovery=true};await using var editor=new FaceFeatureEditorViewModel(f.Document,gated,FaceFeatureKind.PlanarPushPull);editor.SelectedSource=Assert.Single(editor.Sources);
        var pending=editor.PickExactAsync(f.Top());await gated.Started.Task;Task? closing=null;
        switch(change){case "cancel":editor.CancelCommand.Execute(null);break;case "close":closing=editor.DisposeAsync().AsTask();Assert.False(closing.IsCompleted);break;
            case "document":await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));break;default:editor.SelectedSource=null;break;}
        gated.Release.SetResult();await pending;if(closing is not null)await closing;
        Assert.Empty(editor.Faces);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.Single(f.Session.Snapshot.Bodies);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task LateNativePreviewDisposesCandidateAfterCancelOrClose(bool removal,bool close)
    {
        await using var f=await Fixture.Create(bores:removal?1:0);var gated=new GatedFaceKernel(f.Kernel){Evaluation=true};var before=f.Session.Snapshot;int count=f.Assets.Count;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,gated,removal?FaceFeatureKind.Defeature:FaceFeatureKind.PlanarPushPull);editor.SelectedSource=Assert.Single(editor.Sources);
        await editor.PickExactAsync(removal?f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Single():f.Top());
        var pending=editor.PreviewCommand.ExecuteAsync(null);await gated.Started.Task;Task? closing=null;if(close)closing=editor.DisposeAsync().AsTask();else editor.CancelCommand.Execute(null);
        gated.Release.SetResult();await pending;if(closing is not null)await closing;
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
    }
    private sealed class GatedFaceKernel(OcctGeometryKernel inner):IGeometryKernel,IExactTopologyResolver,IFaceEditResolver
    {
        public bool Discovery{get;init;}public bool Evaluation{get;init;}public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Vector3d> InspectPlanarFaceAsync(DocumentSnapshot s,ExactTopologySelection f,IAssetStore a,CancellationToken t=default)
        {if(Discovery){Started.TrySetResult();await Release.Task;}return await inner.InspectPlanarFaceAsync(s,f,a,CancellationToken.None);}
        public Task ConfirmFaceEditRecipeAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)=>inner.ConfirmFaceEditRecipeAsync(r,a,t);
        public Task ConfirmAsync(DocumentSnapshot s,ExactTopologySelection f,IAssetStore a,CancellationToken t=default)=>inner.ConfirmAsync(s,f,a,t);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Evaluation&&r is PlanarPushPullRecipe or DefeatureRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
