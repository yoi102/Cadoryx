using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class IndependentAuthoringGraphTests
{
    [Fact] public async Task IndependentNativeProjectionRetainsBrepAndOwnsItsSourceFeature()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));var original=session.Snapshot.Features.Values.Single();
            var plane=new RigidTransform3d(new(0,0,20),Quaterniond.FromAxisAngle(new(1,0,0),.4));
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.Circle),plane,assets);
            var draft=new SketchDraft(CadSketch.Create(part,"Native source sketch",plane));var id=draft.ProjectNative(evidence);draft.SetConstruction([id],false);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var profile=SketchProfileReference.CreateMixed(sketch,[id]);await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,plane)),"Consumer",part,sketchSource:profile));
            var command=await Clone(session,part);var copiedPart=command.ResultPartId!.Value;var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==copiedPart);
            var source=session.Snapshot.Features.Values.Single(f=>f.PartId==copiedPart&&f.Name=="Source");var consumer=session.Snapshot.Features.Values.Single(f=>f.PartId==copiedPart&&f.Name=="Consumer");
            var link=Assert.Single(copied.NativeProjections);Assert.Equal(source.Id,link.Selection.FeatureId);Assert.Equal(original.Result,link.Source);Assert.Equal(id,link.TargetEntity);
            Assert.True(SketchNativeProjectionGeometry.IsCurrent(session.Snapshot,copied,link));Assert.Equal(copied.Id,consumer.SketchSource!.SketchId);
            var before=session.Snapshot;await session.ExecuteAsync(new RecomputeCommand(original.Id,new CylinderRecipe(7,10,RigidTransform3d.Identity)));
            Assert.True(SketchNativeProjectionGeometry.HasStaleInputs(session.Snapshot,sketch.Id));Assert.False(SketchNativeProjectionGeometry.HasStaleInputs(session.Snapshot,copied.Id));
            Assert.False(session.Snapshot.Features[consumer.Id].IsStale);Assert.Same(consumer.Result,session.Snapshot.Features[consumer.Id].Result);
            var storage=new CadDocumentStorage(edgeProjector:kernel);string path=files.PathFor("native-independent.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {loaded.Snapshot.Validate();var restored=loaded.Snapshot.Sketches[copied.Id];Assert.True(SketchNativeProjectionGeometry.IsCurrent(loaded.Snapshot,restored,restored.NativeProjections[0]));}
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
