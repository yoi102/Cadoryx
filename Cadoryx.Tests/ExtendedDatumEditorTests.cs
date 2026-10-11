using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExtendedDatumEditorTests
{
    [Fact] public async Task PointAndAxisInputsCannotBeSkippedAndPreviewCancelAndConfirmShareOneTransaction()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new Cadoryx.Kernel.Abstractions.MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try
        {
            var before=session.Snapshot;
            await using(var editor=new DatumEditorViewModel(document,kernel))
            {
                Assert.Null(editor.NativeSource);editor.PointName="Point";editor.PointX=3;Assert.True(editor.HasPendingInput);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);editor.ApplyPointCommand.Execute(null);Assert.Single(editor.Points);Assert.False(editor.HasPendingInput);
                editor.AxisName="Axis";editor.AxisOffset=5;editor.ApplyAxisCommand.Execute(null);Assert.Single(editor.Axes);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Equal(3,editor.Points.Single().Point.Position.X);Assert.Equal(5,editor.Axes.Single().Axis.Frame.Translation.Z);Assert.Same(before,session.Snapshot);
                editor.PointX=4;Assert.False(editor.CanConfirm);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);editor.ApplyPointCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                editor.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using(var editor=new DatumEditorViewModel(document,kernel))
            {editor.PointName="Point";editor.PointZ=8;editor.ApplyPointCommand.Execute(null);editor.AxisName="Axis";editor.ApplyAxisCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);}
            Assert.Equal(8,session.Snapshot.Datums.Points.Values.Single().Position.Z);Assert.Single(session.Snapshot.Datums.Axes);await session.UndoAsync();Assert.Same(before,session.Snapshot);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task EditingCompleteCrossTypeRecipesRetainsIdsReferencesAndDetachValues()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new Cadoryx.Kernel.Abstractions.MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var p=new CadDatumPoint(Guid.NewGuid(),part,"P",DatumPointKind.Independent,new(1,2,3));var q=new CadDatumPoint(Guid.NewGuid(),part,"Q",DatumPointKind.Offset,new(0,0,8),p.Id);
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",DatumAxisKind.TwoPoints,p.Id,q.Id,Reverse:true,Offset:2);
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Points=DatumBook.Empty.Points.Add(p.Id,p).Add(q.Id,q),Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));
        var doc=new CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try
        {
            await using var editor=new DatumEditorViewModel(doc,kernel);editor.SelectedAxis=editor.Axes.Single();editor.SelectedPoint=editor.Points.Single(r=>r.Point.Id==q.Id);Assert.False(editor.HasPendingInput);
            editor.ApplyAxisCommand.Execute(null);editor.ApplyPointCommand.Execute(null);Assert.False(editor.HasPendingInput);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            Assert.Equal(axis.Id,editor.Axes.Single().Axis.Id);Assert.Equal(p.Id,editor.Points.Single(r=>r.Point.Id==q.Id).Point.First);
            var oldAxis=editor.Axes.Single().Axis.Frame;var oldPoint=editor.Points.Single(r=>r.Point.Id==q.Id).Point.Position;
            editor.DetachAxisCommand.Execute(null);editor.DetachPointCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            Assert.True(DatumPlanes.SameFrame(oldAxis,editor.Axes.Single().Axis.Frame));Assert.Equal(oldPoint,editor.Points.Single(r=>r.Point.Id==q.Id).Point.Position);
            Assert.Equal(DatumAxisKind.Independent,editor.Axes.Single().Axis.Kind);Assert.Equal(DatumPointKind.Independent,editor.Points.Single(r=>r.Point.Id==q.Id).Point.Kind);
        }
        finally{doc.Detach();}
    }
    [Fact] public async Task SelectedAngularPlanePreservesExactRadiansAndHingeAxisOnUnchangedApply()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new Cadoryx.Kernel.Abstractions.MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Hinge",DatumAxisKind.Basis,Basis:DatumBasisAxis.X);
        var p=new CadDatumPlane(Guid.NewGuid(),part,"Angle",DatumPlaneKind.Angle,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:.173846927651){HingeAxis=axis.Id};
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis),Planes=DatumBook.Empty.Planes.Add(p.Id,p)}));
        var doc=new CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try{await using var editor=new DatumEditorViewModel(doc,kernel);Assert.False(editor.HasPendingInput);editor.ApplyPlaneCommand.Execute(null);Assert.Equal(p.Value,editor.Planes.Single().Plane.Value);Assert.Equal(axis.Id,editor.Planes.Single().Plane.HingeAxis);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);}
        finally{doc.Detach();}
    }
    [Fact] public async Task ExternalGenerationInvalidatesAllStagedAxisPointAndPlaneCandidates()
    {
        var(initial,_)=SketchCommandTests.Document();var assets=new Cadoryx.Kernel.Abstractions.MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var doc=new CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try{await using var editor=new DatumEditorViewModel(doc,kernel);editor.AxisName="Axis";editor.ApplyAxisCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await session.ExecuteAsync(ResourceCommands.AddPart(DefinitionId.New(),"Changed"));Assert.True(editor.IsStale);Assert.False(editor.CanConfirm);await editor.ConfirmCommand.ExecuteAsync(null);Assert.Empty(session.Snapshot.Datums.Axes);}
        finally{doc.Detach();}
    }
}
