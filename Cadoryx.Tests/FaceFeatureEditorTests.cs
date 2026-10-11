using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class FaceFeatureEditorTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitFacesPreviewInvalidationCommitAndUndoAreAtomic(bool draft)
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,draft?FaceFeatureKind.Draft:FaceFeatureKind.Shell);
        Assert.Null(editor.SelectedSource);Assert.Empty(editor.Faces);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
        editor.SelectedSource=Assert.Single(editor.Sources);editor.PickExact(draft?f.Left:f.Top);
        if(draft){editor.SelectedProgram!.AngleDegrees=.05*180/Math.PI;editor.PickExact(f.Right);editor.SelectedProgram!.AngleDegrees=.1*180/Math.PI;}
        else{editor.PickExact(f.Top);Assert.Single(editor.ClosingFaces);}
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
        Assert.Equal(draft?1800-12*15*15*(Math.Tan(.05)+Math.Tan(.1))/2:680,f.Document.PreviewScene!.Items.Single().Geometry.VolumeMm3,4);
        if(draft)editor.SelectedProgram!.PullZ=0;else editor.Thickness=6;
        Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
        if(draft)editor.SelectedProgram!.PullZ=1;else editor.Thickness=1;
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        var after=f.Session.Snapshot;after.Validate();Assert.Single(after.Bodies);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CompleteRecipeAndStableProgramIdentitySurviveTreeEdit(bool draft)
    {
        await using var f=new Fixture();await f.Initialize();GeometryRecipe recipe=draft?f.Draft():new ShellRecipe(f.Source.Result,[f.Top,f.Bottom],1);
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Existing"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        FeatureId? requested=null;f.Document.FaceFeatureEditRequested+=(_,id)=>requested=id;f.Document.EditFeature(feature.Id);Assert.Equal(feature.Id,requested);
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,draft?FaceFeatureKind.Draft:FaceFeatureKind.Shell,feature.Id);
        Assert.False(editor.CanChooseSource);Assert.Equal("Existing",editor.ObjectName);
        var actual=editor.BuildRecipe();Assert.Equal(FaceFeatureRecipes.Faces(recipe),FaceFeatureRecipes.Faces(actual));
        if(draft)
        {
            Assert.Equal(((DraftRecipe)recipe).Programs.AsEnumerable(),((DraftRecipe)actual).Programs.AsEnumerable());
            editor.SelectedProgram=editor.Programs[0];var id=editor.SelectedProgram.Id;var original=editor.SelectedProgram.Value;
            editor.ReplaceFaceCommand.Execute(null);editor.PickExact(f.Left);Assert.Equal(id,editor.SelectedProgram.Id);Assert.Equal(original,editor.SelectedProgram.Value);
            editor.Programs.Move(0,1);Assert.Equal(id,((DraftRecipe)editor.BuildRecipe()).Programs[1].Id);
        }
        var before=f.Session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);editor.CancelCommand.Execute(null);Assert.Same(before,f.Session.Snapshot);Assert.Null(f.Document.PreviewScene);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task StaleSourceRequiresEveryFaceToBeExplicitlyReplaced(bool draft)
    {
        await using var f=new Fixture();await f.Initialize();var recipe=draft?(GeometryRecipe)f.Draft():new ShellRecipe(f.Source.Result,[f.Top],1);
        await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,recipe,"Feature"));var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Id!=f.Source.Id);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));await f.Refresh();var frozen=f.Session.Snapshot;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,draft?FaceFeatureKind.Draft:FaceFeatureKind.Shell,feature.Id);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(frozen,f.Session.Snapshot);
        if(draft)
        {
            var ids=editor.Programs.Select(p=>p.Id).ToArray();editor.SelectedProgram=editor.Programs[0];editor.ReplaceFaceCommand.Execute(null);editor.PickExact(f.Left);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
            editor.SelectedProgram=editor.Programs[1];editor.ReplaceFaceCommand.Execute(null);editor.PickExact(f.Right);Assert.Equal(ids,editor.Programs.Select(p=>p.Id));
        }
        else{editor.ClearFacesCommand.Execute(null);editor.PickExact(f.Top);}
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);Assert.False(f.Session.Snapshot.Features[feature.Id].IsStale);
        await f.Session.UndoAsync();Assert.Same(frozen,f.Session.Snapshot);
    }
    [Fact] public async Task DatumLinkLocksFrameAndExplicitDetachPreservesIt()
    {
        await using var f=new Fixture();await f.Initialize();var datum=new CadDatumPlane(Guid.NewGuid(),f.Source.PartId,"Neutral",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:2);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=System.Collections.Immutable.ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,FaceFeatureKind.Draft);editor.SelectedSource=Assert.Single(editor.Sources);editor.PickExact(f.Left);
        editor.SelectedDatum=Assert.Single(editor.Datums);editor.LinkDatumCommand.Execute(null);Assert.False(editor.SelectedProgram!.CanSetNeutral);var linked=editor.SelectedProgram.Value;
        Assert.Equal(datum.Id,linked.Datum);Assert.Equal(2,linked.NeutralPlane.Translation.Z);
        editor.DetachDatumCommand.Execute(null);Assert.True(editor.SelectedProgram.CanSetNeutral);var detached=editor.SelectedProgram.Value;
        Assert.Null(detached.Datum);Assert.Equal(linked.NeutralPlane,detached.NeutralPlane);Assert.Equal(linked.Id,detached.Id);Assert.Equal(linked.Face,detached.Face);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
    }
    [Fact] public async Task ChangedGenerationDiscardsPreparedCandidateAndRejectsFurtherCommit()
    {
        await using var f=new Fixture();await f.Initialize();await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,FaceFeatureKind.Shell);
        editor.SelectedSource=Assert.Single(editor.Sources);editor.PickExact(f.Top);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
        Assert.False(editor.ConfirmCommand.CanExecute(null));
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task CancelOrCloseDuringLateNativeWorkDisposesCandidate(bool draft,bool close)
    {
        var kernel=new GatedKernel();await using var f=new Fixture(kernel);await f.Initialize();var before=f.Session.Snapshot;int assets=f.Assets.Count;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,kernel,draft?FaceFeatureKind.Draft:FaceFeatureKind.Shell);editor.SelectedSource=Assert.Single(editor.Sources);editor.PickExact(draft?f.Left:f.Top);kernel.Gate=true;
        var preview=editor.PreviewCommand.ExecuteAsync(null);await kernel.Started.Task;Task? closing=null;
        if(close)closing=editor.DisposeAsync().AsTask();else editor.CancelCommand.Execute(null);kernel.Release.SetResult();await preview;if(closing is not null)await closing;
        Assert.Same(before,f.Session.Snapshot);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.Equal(assets,f.Assets.Count);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public IGeometryKernel Kernel {get;}public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}
        public FeatureDefinition Source {get;private set;}=null!;public ExactTopologySelection Top {get;private set;}=null!;public ExactTopologySelection Bottom {get;private set;}=null!;public ExactTopologySelection Left {get;private set;}=null!;public ExactTopologySelection Right {get;private set;}=null!;
        public Fixture(IGeometryKernel? kernel=null){Kernel=kernel??new OcctGeometryKernel();Session=new(DocumentSnapshot.Create("Faces"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public async Task Initialize(){await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));await Refresh();}
        public Task Refresh()
        {
            Source=Session.Snapshot.Features.Values.Single(f=>f.Recipe is BoxRecipe);using var shape=OcctGeometryBridge.ReadShape(Source.Result,Assets);using var map=RepairSnapshot.Create(shape);
            ExactTopologySelection Pick(Func<BoundingBox3d,bool> predicate)
            {
                var row=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return predicate(face.GetBoundingBox());});
                return new(Session.Snapshot.Id,Source.Id,Source.Result.Revision,Source.Result.AssetId,map.Fingerprint,row.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
            }
            Top=Pick(b=>Math.Abs(b.Minimum.Z-15)<1e-5&&Math.Abs(b.Maximum.Z-15)<1e-5);Bottom=Pick(b=>Math.Abs(b.Minimum.Z)<1e-5&&Math.Abs(b.Maximum.Z)<1e-5);
            Left=Pick(b=>Math.Abs(b.Minimum.X)<1e-5&&Math.Abs(b.Maximum.X)<1e-5);Right=Pick(b=>Math.Abs(b.Minimum.X-Source.Result.Bounds.Max.X)<1e-5&&Math.Abs(b.Maximum.X-Source.Result.Bounds.Max.X)<1e-5);return Task.CompletedTask;
        }
        public DraftRecipe Draft()=>new(Source.Result,[new(Guid.NewGuid(),Left,.05,RigidTransform3d.Identity,Vector3d.UnitZ),new(Guid.NewGuid(),Right,-.1,RigidTransform3d.Identity,Vector3d.UnitZ)]);
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class GatedKernel:IGeometryKernel,IExactTopologyResolver
    {
        private readonly OcctGeometryKernel inner=new();public bool Gate {get;set;}public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConfirmAsync(DocumentSnapshot s,ExactTopologySelection p,IAssetStore a,CancellationToken t=default)=>inner.ConfirmAsync(s,p,a,t);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Gate&&r is ShellRecipe or DraftRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
