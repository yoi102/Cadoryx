using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyRepetitionEditorTests
{
    [Fact] public async Task MirrorPreviewPreservesPreparedAssetsAndParameterSourcePreviewCommitsOnce()
    {
        await using var f=new Fixture();await f.Initialize();var source=f.Session.Snapshot.Features.Values.Single();
        await using(var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source))
        {
            editor.Kind=RepetitionKind.Mirror;Assert.Equal(2,editor.Count);Assert.False(editor.CanSetCount);Assert.False(editor.CanSetStep);
            editor.OriginZ=8;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            var derived=f.Document.PreviewScene!.Items.Single(i=>!i.Path.Equals(f.Source)).Geometry;
            await editor.ConfirmCommand.ExecuteAsync(null);var pattern=Assert.Single(f.Session.Snapshot.AssemblyPatterns.Values);
            Assert.Equal(derived,f.Session.Snapshot.Bodies[pattern.Mirrors.Single().Bodies.Single().Target].Geometry);
        }
        await using(var editor=new ParameterEditorViewModel(f.Document,f.Kernel))
        {
            editor.NewParameterCommand.Execute(null);editor.ParameterName="width";editor.Expression="7 mm";editor.ApplyParameterCommand.Execute(null);
            editor.DrivingTarget=editor.Targets.Single(t=>t.Target==ParameterTarget.Feature(source.Id,"X"));editor.BindDimensionCommand.Execute(null);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            var pattern=Assert.Single(f.Session.Snapshot.AssemblyPatterns.Values);var body=pattern.Mirrors.Single().Bodies.Single().Target;
            var candidate=f.Document.PreviewScene!.Items.Single(i=>i.BodyId==body).Geometry;
            await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(candidate,f.Session.Snapshot.Bodies[body].Geometry);Assert.Equal(42,candidate.VolumeMm3,8);
        }
        f.Session.Snapshot.Validate();
    }

    [Theory][InlineData(RepetitionKind.Linear)][InlineData(RepetitionKind.Circular)]
    public async Task PreviewCommitEditAndDetachKeepInstancesAndExactUndo(RepetitionKind kind)
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;int count=f.Assets.Count;
        await using(var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source))
        {
            editor.Kind=kind;editor.Step=kind==RepetitionKind.Linear?8:40;editor.Count=3;await editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);Assert.Equal(3,f.Document.PreviewScene!.Items.Length);Assert.Equal(count,f.Assets.Count);
            editor.CancelCommand.Execute(null);Assert.Null(f.Document.PreviewScene);await editor.PreviewCommand.ExecuteAsync(null);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var committed=f.Session.Snapshot;var p=Assert.Single(committed.AssemblyPatterns.Values);Assert.Single(committed.Bodies);Assert.Equal(count,f.Assets.Count);
        var generated=new OccurrencePath(committed.Id,[p.CopySlots[0]]);
        f.Document.Selection.SelectOccurrence(generated);Assert.False(f.Document.Placement.CanMove);Assert.False(f.Document.Placement.CanMakePartIndependent);
        Assert.False(Cadoryx.Rendering.Occt.OccurrenceDrag.CanMove(committed,generated));Assert.True(Cadoryx.Rendering.Occt.OccurrenceDrag.CanMove(committed,f.Source));
        await using(var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,generated))
        {
            Assert.False(editor.IsNew);Assert.False(editor.CanChooseSource);Assert.False(editor.CanChangeKind);Assert.Equal(f.Source,editor.SelectedSource!.Path);
            editor.Count=4;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var edited=f.Session.Snapshot;Assert.True(p.CopySlots.SequenceEqual(edited.AssemblyPatterns[p.Id].CopySlots.Take(2)));
        await using(var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source))
        {
            await editor.DetachCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(edited,f.Session.Snapshot);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        Assert.Empty(f.Session.Snapshot.AssemblyPatterns);Assert.Equal(4,f.Session.Snapshot.EnumerateOccurrences().Count());
        await f.Session.UndoAsync();Assert.Same(edited,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(committed,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task CompleteEditPreservesQuaternionAndRadianAngleWithoutRoundtripDrift()
    {
        await using var f=new Fixture();await f.Initialize();var id=Guid.NewGuid();double angle=.432123456789;
        var frame=new RigidTransform3d(new(2,3,4),Quaterniond.FromAxisAngle(new(1,2,3),.63123456789));
        var p=new AssemblyRepetition(id,f.Session.Snapshot.RootAssemblyId,f.Source.Slots.Single(),"Precise",RepetitionKind.Circular,frame,3,angle,[ComponentSlotId.New(),ComponentSlotId.New()]);
        await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Source,p));await using var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        var after=f.Session.Snapshot.AssemblyPatterns[id];Assert.Equal(frame,after.Frame);Assert.Equal(angle,after.Step);
    }

    [Fact] public async Task InvalidInputsAndChangedGenerationNeverCommitPreview()
    {
        await using var f=new Fixture();await f.Initialize();await using var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source);
        var before=f.Session.Snapshot;editor.Step=0;await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,f.Session.Snapshot);
        editor.Step=8;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        editor.Count=4;Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{Visible=false}));Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
        await editor.ConfirmCommand.ExecuteAsync(null);Assert.Empty(f.Session.Snapshot.AssemblyPatterns);
    }

    [Fact] public async Task EmptyDocumentHasNoInventedSourceAndCloseClearsPreparedScene()
    {
        await using var f=new Fixture();await using(var empty=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel))
        {Assert.Empty(empty.Sources);await empty.PreviewCommand.ExecuteAsync(null);Assert.False(empty.CanConfirm);}
        await f.Initialize();var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        f.Document.Detach();await editor.DisposeAsync();Assert.False(editor.CanEdit);Assert.Null(f.Document.PreviewScene);Assert.Empty(f.Session.Snapshot.AssemblyPatterns);
    }

    [Fact] public async Task ParameterEditorListsArrayAndAxisTargetsAndCommitsPreparedPoseChanges()
    {
        await using var f=new Fixture();await f.Initialize();var part=f.Session.Snapshot.Bodies.Values.Single().PartId;
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Named axis",DatumAxisKind.Independent,Direction:Vector3d.UnitZ);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));
        await using(var editor=new AssemblyRepetitionEditorViewModel(f.Document,f.Kernel,f.Source))
        {editor.SelectedDatum=editor.Datums.Single(d=>d.Datum==axis.Id);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);}
        var pattern=Assert.Single(f.Session.Snapshot.AssemblyPatterns.Values);
        await using(var editor=new ParameterEditorViewModel(f.Document,f.Kernel))
        {
            Assert.Contains(editor.Targets,t=>t.Target.Kind==ParameterTargetKind.Datum&&t.Target.Owner==axis.Id);
            var target=editor.Targets.Single(t=>t.Target==ParameterTarget.AssemblyPattern(pattern.Id,"Spacing"));Assert.Equal(ParameterDimension.Length,target.Dimension);
            editor.NewParameterCommand.Execute(null);editor.ParameterName="spacing";editor.Expression="12 mm";editor.ApplyParameterCommand.Execute(null);
            editor.DrivingTarget=target;editor.BindDimensionCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        Assert.Equal(12,f.Session.Snapshot.AssemblyPatterns[pattern.Id].Step);f.Session.Snapshot.Validate();
    }

    [Fact] public async Task NativeBodyEditAndIndependentSourceKeepSharedGeometryAndDrawingPlacementAssociative()
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();var body=f.Session.Snapshot.Bodies.Values.Single();
        var p=new AssemblyRepetition(Guid.NewGuid(),f.Session.Snapshot.RootAssemblyId,f.Source.Slots.Single(),"Copies",RepetitionKind.Linear,RigidTransform3d.Identity,3,8,[ComponentSlotId.New(),ComponentSlotId.New()]);
        int assets=f.Assets.Count;await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Source,p));Assert.Equal(assets,f.Assets.Count);
        var copied=new OccurrencePath(f.Session.Snapshot.Id,[p.CopySlots[0]]);var sheet=TechnicalDrawingSheet.A4Landscape("Array instance");
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1,copied));
        await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(f.Source,RigidTransform3d.Translate(5,2,0)));var view=f.Session.Snapshot.DrawingSheets[sheet.Id].Views.Single();
        Assert.Null(view.StaleReason);Assert.Equal(new Vector3d(13,2,0),view.Sources.Single().WorldTransform.Translation);
        var independent=AssemblyOccurrenceCommands.MakePartIndependent(f.Source);await f.Session.ExecuteAsync(independent);var cloned=f.Session.Snapshot;
        Assert.All(cloned.EnumerateOccurrences(),o=>Assert.Equal(independent.ResultPartId,o.DefinitionId));Assert.Equal(body.Geometry.AssetId,cloned.Bodies.Values.Single(b=>b.PartId==independent.ResultPartId).Geometry.AssetId);
        var source=cloned.Features.Values.Single(b=>b.PartId==independent.ResultPartId);await f.Session.ExecuteAsync(new RecomputeCommand(source.Id,new BoxRecipe(6,3,2,RigidTransform3d.Identity)));
        var current=f.Session.Snapshot;Assert.Equal(36,current.Bodies.Values.Single(b=>b.PartId==independent.ResultPartId).Geometry.VolumeMm3,8);
        Assert.All(CadScene.FromDocument(current).Items,i=>Assert.Equal(36,i.Geometry.VolumeMm3,8));Assert.Null(current.DrawingSheets[sheet.Id].Views.Single().StaleReason);
        var file=files.PathFor("native-array.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),file);using var loaded=await new CadDocumentStorage().LoadAsync(file,f.Assets);loaded.Snapshot.Validate();
        Assert.Single(loaded.Snapshot.AssemblyPatterns);Assert.Equal(3,CadScene.FromDocument(loaded.Snapshot).Items.Length);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}public CadDocumentViewModel Document {get;}
        public OccurrencePath Source {get;private set;}=null!;
        public Fixture(){Session=new(DocumentSnapshot.Create("Array editor"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        public async Task Initialize(){await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));Source=Session.Snapshot.EnumerateOccurrences().Single().Path;}
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
