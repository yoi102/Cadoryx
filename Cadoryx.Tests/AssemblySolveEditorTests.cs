using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using RepairSnapshot=OcctSharp.RepairSnapshot;
using ShapeKind=OcctSharp.ShapeKind;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblySolveEditorTests
{
    [Fact] public async Task NativeVerifiedPreviewCancelAndConfirmPreserveExactPosesAndOneUndo()
    {
        await using var f=new Fixture();await f.Initialize();var editor=f.Document.AssemblyConstraints;var baseline=f.Session.Snapshot;
        await editor.PreviewSolveCommand.ExecuteAsync(null);Assert.True(editor.HasSolvePreview,editor.Status);
        var displayed=f.Document.PreviewScene!.Items.ToDictionary(i=>i.Path,i=>i.WorldTransform);
        Assert.Same(baseline,f.Session.Snapshot);editor.CancelSolvePreviewCommand.Execute(null);Assert.Null(f.Document.PreviewScene);
        await editor.PreviewSolveCommand.ExecuteAsync(null);Assert.True(editor.HasSolvePreview,editor.Status);
        await editor.ConfirmSolveCommand.ExecuteAsync(null);Assert.False(editor.HasSolvePreview);Assert.Null(f.Document.PreviewScene);
        Assert.All(f.Document.Scene.Items,i=>Assert.Equal(displayed[i.Path],i.WorldTransform));
        await f.Session.UndoAsync();Assert.Same(baseline,f.Session.Snapshot);
        await editor.PreviewSolveCommand.ExecuteAsync(null);Assert.True(editor.HasSolvePreview,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(baseline.Settings.Grid with{Visible=false}));
        Assert.False(editor.HasSolvePreview);var changed=f.Session.Snapshot;
        await editor.ConfirmSolveCommand.ExecuteAsync(null);Assert.Same(changed,f.Session.Snapshot);
    }

    [Fact] public async Task UndoBackToIdenticalSnapshotCannotPublishALateNativePreview()
    {
        await using var f=new Fixture();await f.Initialize();var baseline=f.Session.Snapshot;f.Kernel.Delay=true;
        var pending=f.Document.AssemblyConstraints.PreviewSolveCommand.ExecuteAsync(null);
        await f.Kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(baseline.Settings.Grid with{Visible=false}));await f.Session.UndoAsync();
        Assert.Same(baseline,f.Session.Snapshot);f.Kernel.Release.TrySetResult();await pending;
        Assert.False(f.Document.AssemblyConstraints.HasSolvePreview);Assert.Null(f.Document.PreviewScene);Assert.Same(baseline,f.Session.Snapshot);
    }

    [Fact] public async Task ClosingRetainsInputLeaseUntilLateNativeResultAndNeverReopensAPreview()
    {
        await using var f=new Fixture();await f.Initialize();f.Kernel.Delay=true;
        var pending=f.Document.AssemblyConstraints.PreviewSolveCommand.ExecuteAsync(null);
        await f.Kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));f.Document.Detach();await f.Session.DisposeAsync();
        Assert.True(f.Assets.Count>0);f.Kernel.Release.TrySetResult();await pending;
        Assert.Equal(0,f.Assets.Count);Assert.Null(f.Document.PreviewScene);Assert.False(f.Document.AssemblyConstraints.HasSolvePreview);
    }

    [Fact] public async Task ForgedAnalyticDatumIsRejectedBeforeShowingANativePreview()
    {
        await using var f=new Fixture();await f.Initialize();var relation=f.Session.Snapshot.AssemblyConstraints.Values.Single();
        var bad=relation.PrimaryDatum! with{LocalPoint=relation.PrimaryDatum!.LocalPoint+new Vector3d(1,0,0)};
        await f.Session.ExecuteAsync(new EditDocumentCommand("Forged stored datum",doc=>doc with
        {AssemblyConstraints=doc.AssemblyConstraints.SetItem(relation.Id,relation with{PrimaryDatum=bad,PrimaryLocalPoint=bad.LocalPoint})}));
        var baseline=f.Session.Snapshot;await f.Document.AssemblyConstraints.PreviewSolveCommand.ExecuteAsync(null);
        Assert.False(f.Document.AssemblyConstraints.HasSolvePreview);Assert.Null(f.Document.PreviewScene);Assert.Same(baseline,f.Session.Snapshot);
        Assert.Contains("differs",f.Document.AssemblyConstraints.Status);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public DelayedKernel Kernel {get;}=new();
        public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}
        public Fixture(){Session=new(DocumentSnapshot.Create("Solve editor"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public async Task Initialize()
        {
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Shared box"));
            var body=Session.Snapshot.Bodies.Values.Single();var first=Session.Snapshot.EnumerateOccurrences().Single().Path;
            var slot=ComponentSlotId.New();await Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(Session.Snapshot.Id,[]),body.PartId,slot,"Second",RigidTransform3d.Translate(8,0,0)));
            var second=new OccurrencePath(Session.Snapshot.Id,[slot]);
            using var shape=OcctGeometryBridge.ReadShape(body.Geometry,Assets);using var map=RepairSnapshot.Create(shape);
            var index=map.Topology.First(t=>t.Kind==ShapeKind.Face).Selection.Index;
            var a=await Kernel.ResolveAssemblyDatumAsync(Session.Snapshot,first,body.Id,index,map.Fingerprint,Assets);
            var b=await Kernel.ResolveAssemblyDatumAsync(Session.Snapshot,second,body.Id,index,map.Fingerprint,Assets);
            await Session.ExecuteAsync(AssemblyConstraintCommands.AddDatumPair(AssemblyConstraintId.New(),"Exact plane origins",AssemblyConstraintKind.Coincident,a,b));
        }
        public async ValueTask DisposeAsync(){Kernel.Release.TrySetResult();Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class DelayedKernel:IGeometryKernel,IAssemblyDatumResolver
    {
        private readonly OcctGeometryKernel native=new();public bool Delay;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)=>native.EvaluateAsync(recipe,assets,cancellationToken);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)=>native.ImportAsync(path,assets,cancellationToken);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)=>native.ExportAsync(snapshot,assets,path,cancellationToken);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken cancellationToken=default)=>native.ExportAsync(snapshot,assets,path,options,cancellationToken);
        public async Task<AssemblyDatumReference> ResolveAssemblyDatumAsync(DocumentSnapshot document,OccurrencePath path,BodyId bodyId,int index,string fingerprint,IAssetStore assets,CancellationToken token=default)
        {
            if(Delay){Entered.TrySetResult();await Release.Task;} // Simulates a native operation returning after cancellation.
            return await native.ResolveAssemblyDatumAsync(document,path,bodyId,index,fingerprint,assets,CancellationToken.None);
        }
    }
}
