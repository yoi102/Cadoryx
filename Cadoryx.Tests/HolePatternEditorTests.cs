using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class HolePatternEditorTests
{
    [Theory][InlineData(RepetitionKind.Linear,true)][InlineData(RepetitionKind.Circular,true)]
    [InlineData(RepetitionKind.Mirror,true)][InlineData(RepetitionKind.Mirror,false)]
    public async Task ActualLayoutPreviewCancelCommitAndUndoModifyOneBase(RepetitionKind kind,bool include)
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;
        await using(var cancel=new RepetitionEditorViewModel(f.Document,f.Kernel))
        {
            Configure(cancel,kind,include);await cancel.PreviewCommand.ExecuteAsync(null);Assert.True(cancel.CanConfirm,cancel.Status);
            Assert.Same(before,f.Session.Snapshot);Assert.Single(f.Document.PreviewScene!.Items);
            cancel.CancelCommand.Execute(null);Assert.Null(f.Document.PreviewScene);Assert.Same(before,f.Session.Snapshot);
        }
        await using var editor=new RepetitionEditorViewModel(f.Document,f.Kernel);Configure(editor,kind,include);
        Assert.Equal(RepetitionOutput.Fused,editor.Output);Assert.False(editor.CanSetOutput);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        editor.Step=0;Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
        if(kind==RepetitionKind.Linear)editor.Step=8;
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        var after=f.Session.Snapshot;after.Validate();var body=Assert.Single(after.Bodies.Values);int count=kind==RepetitionKind.Mirror?(include?2:1):3;
        Assert.Equal(4500-count*5*Math.PI,body.Geometry.VolumeMm3,5);Assert.IsType<HolePatternRecipe>(after.Features[body.Producer!.Value].Recipe);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Theory][InlineData(RepetitionKind.Linear)][InlineData(RepetitionKind.Circular)]
    public async Task CompleteEditingPreservesExactQuaternionRadiansAndSurvivingIdentities(RepetitionKind kind)
    {
        await using var f=new Fixture();await f.Initialize();
        var frame=new RigidTransform3d(kind==RepetitionKind.Circular?new(10,10,5):Vector3d.Zero,Quaterniond.FromEulerDegrees(0,0,13.232423534531));
        double step=kind==RepetitionKind.Linear?5.1234567890123:Math.PI/7+1e-13;
        var layout=new RepetitionRecipe(f.Seed.Result,kind,frame,3,step,[Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,layout),"Existing"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);FeatureId? requested=null;
        f.Document.RepetitionEditRequested+=(_,id)=>requested=id;f.Document.EditFeature(pattern.Id);Assert.Equal(pattern.Id,requested);
        await using(var editor=new RepetitionEditorViewModel(f.Document,f.Kernel,pattern.Id))
        {
            Assert.True(editor.ReplayHole);Assert.False(editor.CanSetReplay);Assert.False(editor.CanChooseSource);Assert.Equal("Seed",editor.SourceDescription);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var unchanged=(HolePatternRecipe)f.Session.Snapshot.Features[pattern.Id].Recipe;
        Assert.Equal(layout.Frame,unchanged.Layout.Frame);Assert.Equal(step,unchanged.Layout.Step);Assert.True(layout.CopyIds.SequenceEqual(unchanged.Layout.CopyIds));
        await using(var editor=new RepetitionEditorViewModel(f.Document,f.Kernel,pattern.Id))
        {
            editor.Count=2;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        await using(var editor=new RepetitionEditorViewModel(f.Document,f.Kernel,pattern.Id))
        {
            editor.Count=3;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var grown=(HolePatternRecipe)f.Session.Snapshot.Features[pattern.Id].Recipe;
        Assert.True(layout.CopyIds.Take(2).SequenceEqual(grown.Layout.CopyIds.Take(2)));Assert.NotEqual(layout.CopyIds[2],grown.Layout.CopyIds[2]);
    }

    [Fact] public async Task NonHoleSourceAndChangedGenerationRejectConfirmationAtomically()
    {
        await using var f=new Fixture();await f.Initialize(false);var box=f.Session.Snapshot;
        await using(var invalid=new RepetitionEditorViewModel(f.Document,f.Kernel))
        {invalid.ReplayHole=true;await invalid.PreviewCommand.ExecuteAsync(null);Assert.False(invalid.CanConfirm);Assert.Same(box,f.Session.Snapshot);Assert.Null(f.Document.PreviewScene);}
        await f.AddHole();await using var editor=new RepetitionEditorViewModel(f.Document,f.Kernel);Configure(editor,RepetitionKind.Linear,true);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));
        Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.False(editor.ConfirmCommand.CanExecute(null));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task CancelOrDisposeDuringLateNativeWorkReleasesCandidate(bool close)
    {
        var kernel=new GatedKernel();await using var f=new Fixture(kernel);await f.Initialize();var before=f.Session.Snapshot;int assets=f.Assets.Count;
        await using var editor=new RepetitionEditorViewModel(f.Document,kernel);Configure(editor,RepetitionKind.Linear,true);kernel.Gate=true;
        var preview=editor.PreviewCommand.ExecuteAsync(null);await kernel.Started.Task;Task? disposal=null;
        if(close)disposal=editor.DisposeAsync().AsTask();else editor.CancelCommand.Execute(null);kernel.Release.SetResult();await preview;if(disposal is not null)await disposal;
        Assert.Same(before,f.Session.Snapshot);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.Equal(assets,f.Assets.Count);
    }

    private static void Configure(RepetitionEditorViewModel editor,RepetitionKind kind,bool include)
    {
        editor.ReplayHole=true;editor.Kind=kind;editor.Count=3;editor.Step=8;editor.IncludeSource=include;
        if(kind==RepetitionKind.Circular){editor.OriginX=10;editor.OriginY=10;editor.OriginZ=5;editor.FullCircle=true;}
        if(kind==RepetitionKind.Mirror){editor.OriginX=15;editor.RotationY=90;}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public IGeometryKernel Kernel {get;}public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}
        public FeatureDefinition Seed {get;private set;}=null!;
        public Fixture(IGeometryKernel? kernel=null){Kernel=kernel??new OcctGeometryKernel();Session=new(DocumentSnapshot.Create("Replay editor"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public async Task Initialize(bool hole=true){await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));if(hole)await AddHole();}
        public async Task AddHole()
        {var plate=Session.Snapshot.Bodies.Values.Single();await Session.ExecuteAsync(new AddHoleCommand(plate.Id,new(plate.Geometry,RigidTransform3d.Translate(0,0,5),new(5,5),2),"Seed"));Seed=Session.Snapshot.Features.Values.Single(p=>p.Recipe is HoleRecipe);}
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class GatedKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public bool Gate {get;set;}public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Gate&&r is HolePatternRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
