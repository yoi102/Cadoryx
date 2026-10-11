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
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Tests;

public sealed class SketchFreeformAuthoringTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AuthoredFreeformLinksRecomputeAndPreserveFullValuesWithUndoStorageAndSolverProtection(bool corruptSolver)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddFreeform(SketchFreeformGeometryTests.Geometry(2));
            if(corruptSolver)
            {
                await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(source.Value,new CorruptSolver())));
                Assert.Same(initial,session.Snapshot);return;
            }
            await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var s=session.Snapshot.Sketches[source.Value.Id];
            var target=new SketchDraft(CadSketch.Create(part,"Target",new(new(0,0,10),Quaterniond.FromAxisAngle(new(1,0,0),.4))));
            target.Project(s,s.Freeforms[0].Id);var id=target.Value.Freeforms[0].Id;target.SetConstruction([id],false);
            await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var t=session.Snapshot.Sketches[target.Value.Id];
            var reference=SketchProfileReference.CreateMixed(t,[id]);await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,t.Plane)),"Consumer",part,sketchSource:reference));
            var before=session.Snapshot;var feature=before.Features.Values.Single();var edit=new SketchDraft(s);
            edit.EditFreeform(s.Freeforms[0].Id,s.Freeforms[0].Geometry with{Poles=s.Freeforms[0].Geometry.Poles.Select(p=>new Point2d(p.X,p.Y*1.2)).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));var after=session.Snapshot;
            Assert.False(after.Features[feature.Id].IsStale);Assert.NotEqual(feature.Result.Revision,after.Features[feature.Id].Result.Revision);
            Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3/feature.Result.VolumeMm3-1.2),0,1e-6);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("author.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))Assert.True(after.Sketches[t.Id].Freeforms[0].Geometry.SameValues(loaded.Snapshot.Sketches[t.Id].Freeforms[0].Geometry));
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class CorruptSolver:ISketchConstraintSolver
    {
        public string Version=>"test-corrupt-freeform";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var report=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);var curve=sketch.Freeforms.Single();
            return report with{Solution=sketch with{Freeforms=[curve with{Geometry=curve.Geometry with{Reversed=true}}]}};
        }
    }
    [Theory][InlineData(0)][InlineData(2)][InlineData(3)]
    public async Task PickerAndFullNumericInputsRequireDetachAndConsumePendingChangesAtomically(int kind)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=SketchFreeformProjectionTests.NativeSource(kind))using(var stored=OcctGeometryBridge.StoreShape(source,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source",part));
            var before=session.Snapshot;var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new SketchEditorViewModel(doc,kernel,solver,CadSketch.Create(part,"Projected",RigidTransform3d.Translate(0,0,20)),true);
                var selection=SketchEllipseProjectionTests.Pick(before,assets,kind==0?CurveGeometryType.BezierCurve:CurveGeometryType.BSplineCurve);
                await using(var picker=editor.CreateNativeProjectionPicker())
                {
                    await picker.PickExactAsync(selection);Assert.True(picker.CanUse,picker.Status);Assert.Empty(editor.Sketch.Freeforms);
                    var expected=SketchFreeformGeometryTests.Geometry(kind);
                    Assert.Equal(string.Format(Strings.NativeFreeformProjectionReady,expected.Degree,expected.Poles.Length),picker.CurveSummary);
                    await picker.UseCommand.ExecuteAsync(null);
                }
                var curve=editor.Sketch.Freeforms.Single();editor.Select(curve.Id);Assert.True(editor.HasSelectedFreeform);Assert.Equal(curve.Geometry.Degree,editor.FreeformDegree);
                editor.FreeformWeightsText="1; 1; 1";editor.ApplyFreeformCommand.Execute(null);
                Assert.Equal(Strings.NativeProjectionDriven,editor.Status);Assert.True(curve.Geometry.SameValues(editor.Sketch.Freeforms[0].Geometry));
                editor.Select(curve.Id);editor.DetachProjectionCommand.Execute(null);editor.FreeformReversed=true;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.True(editor.Sketch.Freeforms.Single().Geometry.Reversed);
                Assert.Same(before,session.Snapshot);editor.UndoCommand.Execute(null);Assert.False(editor.Sketch.Freeforms.Single().Geometry.Reversed);
                editor.UndoCommand.Execute(null);Assert.Single(editor.Sketch.NativeProjections);editor.CancelCommand.Execute(null);Assert.Null(doc.PreviewScene);
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(-4,true)][InlineData(-5,true)][InlineData(-1,false)]
    public async Task FreeformRevolveAxisChecksUseExactCrossingsIncludingInteriorTangency(double controlX,bool reject)
    {
        var g=new SketchFreeformGeometry(SketchFreeformKind.Bezier,[new(4,0),new(controlX,3),new(4,6)],[],[],[],2,false,0,1);
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Meridian",RigidTransform3d.Identity));draft.AddFreeform(g);
        var recipe=new RevolveRecipe(SketchProfileBuilder.Mixed(draft.Value,[draft.Value.Freeforms[0].Id]),Math.Tau,RigidTransform3d.Identity);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        if(reject)await Assert.ThrowsAsync<CadValidationException>(async()=>{using var result=await kernel.EvaluateAsync(recipe,assets);});
        else using(await kernel.EvaluateAsync(recipe,assets)){ }
        Assert.Equal(0,assets.Count);
    }
}
