using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class GuidedEditorTests
{
    private static RigidTransform3d PathFrame=>new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0));
    private static SweepRecipe Sweep()=>new(new(SketchProfile.FromCircle(new(0,0),1),RigidTransform3d.Identity),new([new(new(0,10),new(0,0))],PathFrame));
    private static LoftRecipe Loft()
    {
        var root=Guid.NewGuid();var hole=Guid.NewGuid();
        return new([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5),RigidTransform3d.Translate(0,0,10)],
            [new(hole,root,Enumerable.Repeat(SketchProfile.FromCircle(new(0,0),1),3).ToImmutableArray()),
             new(root,null,Enumerable.Repeat(SketchProfile.FromCircle(new(0,0),3),3).ToImmutableArray())],Ruled:true);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExistingCompleteRecipeSurvivesEditAndCancel(bool loft)
    {
        await using var f=new Fixture();GeometryRecipe recipe=loft?Loft():Sweep() with{Path=new([new(new(0,0),new(0,10))],PathFrame)};
        await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"Existing",f.Part));var feature=f.Session.Snapshot.Features.Values.Single();var before=f.Session.Snapshot;
        await using var editor=new GuidedEditorViewModel(f.Document,f.Kernel,loft?GuidedEditorKind.Loft:GuidedEditorKind.Sweep,feature.Id);
        var rebuilt=editor.BuildRecipe();
        if(loft)
        {
            var actual=Assert.IsType<LoftRecipe>(rebuilt);var expected=(LoftRecipe)recipe;
            Assert.Equal(expected.Stations.AsEnumerable(),actual.Stations.AsEnumerable());Assert.Equal(expected.Channels.Select(c=>c.Id),actual.Channels.Select(c=>c.Id));
            Assert.Equal(expected.Channels.Select(c=>c.Parent),actual.Channels.Select(c=>c.Parent));Assert.True(actual.Ruled);
            for(int i=0;i<actual.Channels.Length;i++)Assert.Equal(expected.Channels[i].Profiles.AsEnumerable(),actual.Channels[i].Profiles.AsEnumerable());
        }
        else{var actual=Assert.IsType<SweepRecipe>(rebuilt);Assert.Equal(((SweepRecipe)recipe).Section,actual.Section);Assert.True(SketchMixedProfile.SameLoop(((SweepRecipe)recipe).Path.Curves,actual.Path.Curves));}
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
        editor.Output=GuidedOutput.Sheet;Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
        editor.CancelCommand.Execute(null);Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task ExplicitDirectedSketchPathDetachAndCommitUseOneTransaction()
    {
        await using var f=new Fixture();var section=new SketchDraft(CadSketch.Create(f.Part,"Section",RigidTransform3d.Identity));section.AddCircle(new(0,0),1);
        var path=new SketchDraft(CadSketch.Create(f.Part,"Path",PathFrame));path.AddLine(new(0,10),new(0,0));
        await f.Store(section.Value);await f.Store(path.Value);var before=f.Session.Snapshot;
        await using(var editor=new GuidedEditorViewModel(f.Document,f.Kernel,GuidedEditorKind.Sweep))
        {
            Assert.Null(editor.SelectedSweepProfile);Assert.Null(editor.SelectedPathSketch);Assert.Empty(editor.PathEdges);
            editor.SelectedSweepProfile=editor.Profiles.Single(p=>p.Section.Source?.SketchId==section.Value.Id);
            editor.SelectedPathSketch=editor.PathSketches.Single(p=>p.Value==path.Value.Id);editor.SelectedAvailableEdge=Assert.Single(editor.AvailableEdges);editor.AddEdgeCommand.Execute(null);
            Assert.False(editor.CanSetSectionFrame);Assert.False(editor.CanSetPathFrame);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,f.Session.Snapshot);
            editor.PathEdges[0].Reverse=true;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            var linked=Assert.IsType<SweepRecipe>(editor.BuildRecipe());Assert.True(linked.Path.Source!.Edges[0].Reverse);
            editor.DetachPathCommand.Execute(null);editor.DetachSectionCommand.Execute(null);Assert.False(editor.CanConfirm);
            var frozen=Assert.IsType<SweepRecipe>(editor.BuildRecipe());Assert.Null(frozen.Section.Source);Assert.Null(frozen.Path.Source);
            Assert.True(SketchMixedProfile.SameLoop(linked.Path.Curves,frozen.Path.Curves));Assert.Equal(linked.Section.Profile,frozen.Section.Profile);Assert.True(editor.CanSetPathFrame&&editor.CanSetSectionFrame);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var accepted=f.Session.Snapshot;Assert.Equal(10*Math.PI,accepted.Bodies.Values.Single().Geometry.VolumeMm3,5);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(accepted,f.Session.Snapshot);
    }
    [Fact] public async Task LoftMissingAssignmentReorderAndLeafRemovalCannotDropCorrespondence()
    {
        await using var f=new Fixture();await f.Session.ExecuteAsync(new AddBodyCommand(Loft(),"Loft",f.Part));var feature=f.Session.Snapshot.Features.Values.Single();var before=f.Session.Snapshot;
        await using var editor=new GuidedEditorViewModel(f.Document,f.Kernel,GuidedEditorKind.Loft,feature.Id);
        editor.SelectedStation=editor.Stations[1];editor.MoveStationUpCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,f.Session.Snapshot);
        editor.MoveStationDownCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        editor.SelectedChannel=editor.Channels.Single(c=>c.Parent is not null);var hole=editor.SelectedChannel.Id;editor.AddChildCommand.Execute(null);var island=editor.SelectedChannel!.Id;
        Assert.Throws<CadValidationException>(()=>editor.BuildRecipe());Assert.False(editor.CanConfirm);
        editor.SelectedChannel=editor.Channels.Single(c=>c.Id==hole);editor.RemoveChannelCommand.Execute(null);Assert.Contains(editor.Channels,c=>c.Id==hole);
        editor.SelectedChannel=editor.Channels.Single(c=>c.Id==island);editor.RemoveChannelCommand.Execute(null);Assert.DoesNotContain(editor.Channels,c=>c.Id==island);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Equal(80*Math.PI,f.Document.PreviewScene!.Items.Single().Geometry.VolumeMm3,4);
    }
    [Fact] public async Task SourceAssignmentsAndDetachedStationPreserveAuthoredOrder()
    {
        await using var f=new Fixture();var sketches=new List<CadSketch>();
        foreach(double z in new[]{0d,5,10}){var draft=new SketchDraft(CadSketch.Create(f.Part,"Station "+z,RigidTransform3d.Translate(0,0,z)));draft.AddCircle(new(0,0),2);await f.Store(draft.Value);sketches.Add(draft.Value);}
        await using var editor=new GuidedEditorViewModel(f.Document,f.Kernel,GuidedEditorKind.Loft);
        foreach(var sketch in sketches){editor.AddStationCommand.Execute(null);editor.SelectedAssignmentProfile=editor.Profiles.Single(p=>p.Section.Source?.SketchId==sketch.Id);editor.AssignProfileCommand.Execute(null);}
        var linked=Assert.IsType<LoftRecipe>(editor.BuildRecipe());Assert.Equal(sketches.Select(s=>(SketchId?)s.Id),linked.Channels[0].Sources.Select(s=>s?.SketchId));
        editor.SelectedStation=editor.Stations[1];Assert.False(editor.SelectedStation.CanSetFrame);editor.DetachStationCommand.Execute(null);Assert.True(editor.SelectedStation.CanSetFrame);
        var detached=Assert.IsType<LoftRecipe>(editor.BuildRecipe());Assert.Null(detached.Channels[0].Sources[1]);Assert.Equal(linked.Stations.AsEnumerable(),detached.Stations.AsEnumerable());Assert.Equal(linked.Channels[0].Profiles.AsEnumerable(),detached.Channels[0].Profiles.AsEnumerable());
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
    }
    [Fact] public async Task GenerationChangeInvalidatesPreparedCandidateAndBlocksCommit()
    {
        await using var f=new Fixture();var recipe=Sweep() with{Path=new([new(new(0,0),new(0,10))],PathFrame)};await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"Sweep",f.Part));
        await using var editor=new GuidedEditorViewModel(f.Document,f.Kernel,GuidedEditorKind.Sweep,f.Session.Snapshot.Features.Values.Single().Id);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));var current=f.Session.Snapshot;
        Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.False(editor.ConfirmCommand.CanExecute(null));Assert.Same(current,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ClosingOrCancelingDuringLateNativeResultReleasesAllCandidateAssets(bool close)
    {
        var kernel=new GatedKernel();await using var f=new Fixture(kernel);var recipe=Sweep() with{Path=new([new(new(0,0),new(0,10))],PathFrame)};
        await f.Session.ExecuteAsync(new AddBodyCommand(recipe,"Sweep",f.Part));kernel.Gate=true;var before=f.Session.Snapshot;int assets=f.Assets.Count;
        await using var editor=new GuidedEditorViewModel(f.Document,kernel,GuidedEditorKind.Sweep,before.Features.Values.Single().Id);
        var preview=editor.PreviewCommand.ExecuteAsync(null);await kernel.Started.Task;Task? closing=null;
        if(close)closing=editor.DisposeAsync().AsTask();else editor.CancelCommand.Execute(null);
        kernel.Release.SetResult();await preview;if(closing is not null)await closing;
        Assert.Same(before,f.Session.Snapshot);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);Assert.Equal(assets,f.Assets.Count);
    }
    [Fact] public void UntouchedFramePreservesExactQuaternionUntilNumericEdit()
    {
        var frame=new GuidedFrameViewModel();var original=new RigidTransform3d(new(1,2,3),Quaterniond.FromEulerDegrees(31,89.999,47));frame.Load(original);
        Assert.Equal(original,frame.Value);frame.X=4;Assert.Equal(4,frame.Value.Translation.X);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public IGeometryKernel Kernel {get;}public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}public DefinitionId Part {get;}
        public Fixture(IGeometryKernel? kernel=null)
        {var(initial,part)=SketchCommandTests.Document();Part=part;Kernel=kernel??new OcctGeometryKernel();Session=new(initial,Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public Task Store(CadSketch sketch)=>Session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class GatedKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public bool Gate {get;set;}public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Gate&&r is SweepRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
