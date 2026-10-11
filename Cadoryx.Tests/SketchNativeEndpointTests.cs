using System.Collections.Immutable;
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

public sealed class SketchNativeEndpointTests
{
    [Fact] public async Task ExplicitNativeReselectionRetainsEndpointIdsAndRecomputesTheCompleteBoundary()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=SketchFreeformProjectionTests.NativeSource(0))using(var stored=OcctGeometryBridge.StoreShape(source,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source",part));
            var original=session.Snapshot.Features.Values.Single();var plane=RigidTransform3d.Translate(0,0,20);
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.BezierCurve),plane,assets);
            var d=new SketchDraft(CadSketch.Create(part,"Native endpoints",plane));var id=d.ProjectNative(evidence);d.SetConstruction([id],false);d.ExposeCurveEndpoints(id);SketchExactEndpointTests.Close(d,id,false);
            var ends=SketchFixedCurveEndpoints.All(d.Value).Single().Ends;await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
            var reference=SketchProfileReference.CreateMixed(s,[id,s.Lines[0].Id]);await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,plane)),"Consumer",part,sketchSource:reference));
            var consumer=session.Snapshot.Features.Values.Single(f=>f.Name=="Consumer");
            using(var source=SketchFreeformProjectionTests.NativeSource(1))using(var stored=OcctGeometryBridge.StoreShape(source,assets))
                await session.ExecuteAsync(new RecomputeCommand(original.Id,new ImportedRecipe(stored.Geometry)));
            Assert.True(session.Snapshot.Features[consumer.Id].IsStale);var frozen=session.Snapshot;
            var sourceOnly=frozen with{Features=frozen.Features.Clear().Add(original.Id,frozen.Features[original.Id])};
            var current=await kernel.ProjectEdgeAsync(frozen,part,SketchEllipseProjectionTests.Pick(sourceOnly,assets,CurveGeometryType.BezierCurve),plane,assets);
            var reselection=new SketchDraft(frozen.Sketches[s.Id]);reselection.ProjectNative(current,replace:id);
            Assert.Equal(ends,SketchFixedCurveEndpoints.All(reselection.Value).Single().Ends);await session.ExecuteAsync(new UpsertSketchCommand(reselection.Value,solver));
            var accepted=session.Snapshot;Assert.False(accepted.Features[consumer.Id].IsStale);Assert.NotEqual(consumer.Result.Revision,accepted.Features[consumer.Id].Result.Revision);
            var updated=accepted.Sketches[s.Id];var(a,b)=SketchFixedCurveEndpoints.Positions(updated,id);
            Assert.InRange((updated.Points.Single(p=>p.Id==ends.Start).Position.X-a.X),-1e-7,1e-7);
            Assert.InRange((updated.Points.Single(p=>p.Id==ends.End).Position.Y-b.Y),-1e-7,1e-7);
            string path=files.PathFor("native-ends.cadoryx");var storage=new CadDocumentStorage(edgeProjector:kernel);await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets)){Assert.Equal(ends,SketchFixedCurveEndpoints.All(loaded.Snapshot.Sketches[s.Id]).Single().Ends);loaded.Snapshot.Validate();}
            await session.UndoAsync();Assert.Same(frozen,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
}

public sealed partial class IndependentAuthoringGraphTests
{
    [Fact] public async Task IndependentNumericBoundaryOwnsItsEndpointAnchorsAndAuthorCurve()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,id,ids)=SketchExactEndpointTests.Loop(part,2,true);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var source=session.Snapshot.Sketches[d.Value.Id];
            var reference=SketchProfileReference.CreateMixed(source,ids);await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,source.Plane)),"Consumer",part,sketchSource:reference));
            var clone=await Clone(session,part);var copiedPart=clone.ResultPartId!.Value;var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==copiedPart);
            var originalEnds=SketchFixedCurveEndpoints.All(source).Single().Ends;Assert.Equal(originalEnds,SketchFixedCurveEndpoints.All(copied).Single().Ends);
            var output=session.Snapshot.Features.Values.Single(f=>f.PartId==copiedPart);var edit=new SketchDraft(source);
            edit.EditFreeform(id,source.Freeforms.Single().Geometry with{Poles=source.Freeforms.Single().Geometry.Poles.Select(p=>new Point2d(p.X+1,p.Y+1)).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));Assert.Same(copied,session.Snapshot.Sketches[copied.Id]);Assert.Same(output.Result,session.Snapshot.Features[output.Id].Result);
            var storage=new CadDocumentStorage();string path=files.PathFor("independent-ends.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets)){Assert.Equal(originalEnds,SketchFixedCurveEndpoints.All(loaded.Snapshot.Sketches[copied.Id]).Single().Ends);loaded.Snapshot.Validate();}
        }
        Assert.Equal(0,assets.Count);
    }
}
