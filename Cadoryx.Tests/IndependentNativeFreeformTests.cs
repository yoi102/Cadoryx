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
    [Fact] public async Task IndependentFreeformProjectionOwnsItsAuthorSourceAndRetainsFullCopiedGeometry()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var shape=SketchFreeformProjectionTests.NativeSource(2))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source",part));
            var original=session.Snapshot.Features.Values.Single();var plane=RigidTransform3d.Translate(0,0,20);
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.BSplineCurve),plane,assets);
            var d=new SketchDraft(CadSketch.Create(part,"Projected",plane));var id=d.ProjectNative(evidence);d.SetConstruction([id],false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
            var sketch=session.Snapshot.Sketches[d.Value.Id];var reference=SketchProfileReference.CreateMixed(sketch,[id]);
            await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,plane)),"Consumer",part,sketchSource:reference));
            var clone=await Clone(session,part);var copiedPart=clone.ResultPartId!.Value;var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==copiedPart);
            var copiedSource=session.Snapshot.Features.Values.Single(f=>f.PartId==copiedPart&&f.Name=="Source");var consumer=session.Snapshot.Features.Values.Single(f=>f.PartId==copiedPart&&f.Name=="Consumer");
            Assert.Equal(copiedSource.Id,copied.NativeProjections.Single().Selection.FeatureId);Assert.Equal(original.Result,copied.NativeProjections.Single().Source);
            Assert.True(evidence.Curve.Freeform!.SameValues(copied.Freeforms.Single().Geometry));var before=session.Snapshot;
            using(var shape=SketchFreeformProjectionTests.NativeSource(5))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))
                await session.ExecuteAsync(new RecomputeCommand(original.Id,new ImportedRecipe(stored.Geometry)));
            Assert.True(SketchNativeProjectionGeometry.HasStaleInputs(session.Snapshot,sketch.Id));Assert.False(SketchNativeProjectionGeometry.HasStaleInputs(session.Snapshot,copied.Id));
            Assert.False(session.Snapshot.Features[consumer.Id].IsStale);Assert.Same(consumer.Result,session.Snapshot.Features[consumer.Id].Result);
            var storage=new CadDocumentStorage(edgeProjector:kernel);string path=files.PathFor("independent-freeform.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {Assert.True(copied.Freeforms.Single().Geometry.SameValues(loaded.Snapshot.Sketches[copied.Id].Freeforms.Single().Geometry));loaded.Snapshot.Validate();}
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
