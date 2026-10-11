using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExtrusionPatternEditorTests
{
    [Theory][InlineData(ExtrusionOperation.Fuse)][InlineData(ExtrusionOperation.Cut)]
    public async Task MutuallyExclusiveReplayPreviewCommitCompleteEditAndShrinkPreserveSeed(ExtrusionOperation operation)
    {
        await using var f=new Fixture();await f.Initialize(operation);var before=f.Session.Snapshot;
        await using(var editor=new RepetitionEditorViewModel(f.Document,f.Kernel))
        {
            editor.ReplayHole=true;editor.ReplayExtrusion=true;Assert.False(editor.ReplayHole);Assert.False(editor.CanSetOutput);
            editor.Count=3;editor.Step=8;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
            editor.CancelCommand.Execute(null);Assert.Null(f.Document.PreviewScene);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var committed=f.Session.Snapshot;var feature=committed.Features.Values.Single(p=>p.Recipe is ExtrusionPatternRecipe);var recipe=(ExtrusionPatternRecipe)feature.Recipe;
        Assert.Equal(operation==ExtrusionOperation.Fuse?4536:4476,feature.Result.VolumeMm3,5);var seed=f.Session.Snapshot.Features[recipe.Seed];
        FeatureId? requested=null;f.Document.RepetitionEditRequested+=(_,id)=>requested=id;f.Document.EditFeature(feature.Id);Assert.Equal(feature.Id,requested);
        await using(var editor=new RepetitionEditorViewModel(f.Document,f.Kernel,feature.Id))
        {
            Assert.True(editor.ReplayExtrusion);Assert.False(editor.CanSetReplay);Assert.False(editor.CanChooseSource);Assert.Equal("Seed",editor.SourceDescription);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);editor.Count=2;Assert.False(editor.CanConfirm);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var after=f.Session.Snapshot;after.Validate();var changed=(ExtrusionPatternRecipe)after.Features[feature.Id].Recipe;
        Assert.True(recipe.SameTemplate(changed.Template));Assert.Equal(seed,after.Features[recipe.Seed]);Assert.True(recipe.Layout.CopyIds.Take(2).SequenceEqual(changed.Layout.CopyIds));
        await f.Session.UndoAsync();Assert.Same(committed,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task InvalidReplaySelectionAndStaleGenerationDoNotPublishCandidate()
    {
        await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Fuse,false);var box=f.Session.Snapshot;
        await using(var invalid=new RepetitionEditorViewModel(f.Document,f.Kernel))
        {invalid.ReplayExtrusion=true;await invalid.PreviewCommand.ExecuteAsync(null);Assert.False(invalid.CanConfirm);Assert.Same(box,f.Session.Snapshot);}
        await f.AddSeed(ExtrusionOperation.Fuse);await using var editor=new RepetitionEditorViewModel(f.Document,f.Kernel);
        editor.ReplayExtrusion=true;editor.Step=8;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}
        public Fixture(){Session=new(DocumentSnapshot.Create("Operation editor"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public async Task Initialize(ExtrusionOperation operation,bool seed=true){await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));if(seed)await AddSeed(operation);}
        public Task AddSeed(ExtrusionOperation operation)
        {
            var body=Session.Snapshot.Bodies.Values.Single();var profile=new SketchProfile([new(0,0),new(2,0),new(2,2),new(0,2)]);
            return Session.ExecuteAsync(new AddExtrusionCommand(new(profile,operation==ExtrusionOperation.Fuse?3:2,RigidTransform3d.Translate(5,5,5),
                new(operation==ExtrusionOperation.Fuse?ExtrusionSide.Positive:ExtrusionSide.Negative,Operation:operation,Source:body.Geometry)),"Seed",targetBody:body.Id));
        }
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
