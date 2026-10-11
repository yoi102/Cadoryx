using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchNativeProjectionTests
{
    private static readonly RigidTransform3d Plane=new(new(0,0,20),Quaterniond.FromAxisAngle(new Vector3d(1,1,1).Normalized(),.4));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeLineAndConicLinksProtectDraftConfirmSourceAndRetainAssets(bool conic)
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(conic?new CylinderRecipe(5,10,RigidTransform3d.Identity):new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Source",part));
        var selection=SketchEllipseProjectionTests.Pick(session.Snapshot,assets,conic?CurveGeometryType.Circle:CurveGeometryType.Line);
        var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,Plane,assets);
        var d=new SketchDraft(CadSketch.Create(part,"Linked",Plane));var id=d.ProjectNative(evidence);Assert.Single(d.Value.NativeProjections);
        if(conic)Assert.Throws<CadValidationException>(()=>d.EditEllipse(id,d.Value.Ellipses[0].Geometry with{Center=new(100,100)}));
        else Assert.Throws<CadValidationException>(()=>d.MovePoint(d.Value.Lines[0].Start,new(100,100)));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var committed=session.Snapshot;Assert.True(SketchNativeProjectionGeometry.IsCurrent(committed,committed.Sketches[d.Value.Id],committed.Sketches[d.Value.Id].NativeProjections[0]));
        var forged=evidence with{Curve=new(new(100,100),new(200,200))};
        // Construct a valid, but incorrect copied curve. Native reconfirmation must reject it before publication.
        if(conic){var e=evidence.Curve.Ellipse!.Value with{Center=new(100,100)};forged=forged with{Curve=new(e.At(0),e.At(1),Ellipse:e)};}
        var bad=new SketchDraft(CadSketch.Create(part,"Forged",Plane));bad.ProjectNative(forged);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(bad.Value,solver)));Assert.Same(committed,session.Snapshot);
        using var files=new TestFiles();var storage=new CadDocumentStorage(edgeProjector:kernel);string file=files.PathFor("linked.cadoryx");await session.SaveAsync(storage,file);
        using(var loaded=await storage.LoadAsync(file,assets)){Assert.Single(loaded.Snapshot.Sketches[d.Value.Id].NativeProjections);loaded.Snapshot.Validate();}
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(file,assets));
        d.DetachProjection(id);Assert.Empty(d.Value.NativeProjections);Assert.Empty(d.Value.Constraints);d.Undo();Assert.Single(d.Value.NativeProjections);
        d.RemoveEntities([id]);Assert.Empty(d.Value.NativeProjections);Assert.Empty(d.Value.Constraints);
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task SourceRevisionChangeFreezesConsumerAndExplicitReselectionPreservesAuthorIdentity()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));var source=session.Snapshot.Features.Values.Single();
        var selection=SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.Circle);var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,Plane,assets);
        var d=new SketchDraft(CadSketch.Create(part,"Linked",Plane));var id=d.ProjectNative(evidence);d.SetConstruction([id],false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var s=session.Snapshot.Sketches[d.Value.Id];var p=SketchProfileReference.CreateMixed(s,[id]);var recipe=p.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Consumer",part,sketchSource:p));var consumer=session.Snapshot.Features.Values.Single(f=>f.Id!=source.Id);var before=session.Snapshot;
        await session.ExecuteAsync(new RecomputeCommand(source.Id,new CylinderRecipe(7,10,RigidTransform3d.Identity)));var frozen=session.Snapshot;
        Assert.True(frozen.Features[consumer.Id].IsStale);Assert.Same(consumer.Result,frozen.Features[consumer.Id].Result);
        Assert.Equal(id,frozen.Sketches[s.Id].NativeProjections[0].TargetEntity);Assert.Contains(source.Result.AssetId,frozen.ReferencedAssets());
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(frozen.Sketches[s.Id],solver)));Assert.Same(frozen,session.Snapshot);
        string file=files.PathFor("frozen.cadoryx");var storage=new CadDocumentStorage(edgeProjector:kernel);await session.SaveAsync(storage,file);
        using(var loaded=await storage.LoadAsync(file,assets)){Assert.True(loaded.Snapshot.Features[consumer.Id].IsStale);Assert.True(SketchNativeProjectionGeometry.HasStaleInputs(loaded.Snapshot,s.Id));}
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(frozen,session.Snapshot);
        var sourceOnly=frozen with{Features=frozen.Features.Remove(consumer.Id)};var current=SketchEllipseProjectionTests.Pick(sourceOnly,assets,CurveGeometryType.Circle);
        var replacement=await kernel.ProjectEdgeAsync(frozen,part,current,Plane,assets);var reselected=new SketchDraft(frozen.Sketches[s.Id]);Assert.Equal(id,reselected.ProjectNative(replacement,replace:id));
        await session.ExecuteAsync(new UpsertSketchCommand(reselected.Value,solver));var after=session.Snapshot;Assert.False(after.Features[consumer.Id].IsStale);
        Assert.InRange(Math.Abs(after.Features[consumer.Id].Result.VolumeMm3/consumer.Result.VolumeMm3-49d/25),0,1e-8);
        Assert.Equal(id,after.Features[consumer.Id].SketchSource!.MixedBoundaryIds[0]);await session.UndoAsync();Assert.Same(frozen,session.Snapshot);
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Theory] [InlineData("cache")] [InlineData("fingerprint")] [InlineData("plane")] [InlineData("legacy")]
    public async Task NativeProjectionStorageRechecksOriginalBrepAndRejectsForgedCachesWithoutLeaks(string mutation)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));
        var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.Circle),Plane,assets);
        var d=new SketchDraft(CadSketch.Create(part,"Linked",Plane));d.ProjectNative(evidence);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var storage=new CadDocumentStorage(edgeProjector:kernel);string file=files.PathFor("corrupt.cadoryx");await session.SaveAsync(storage,file);int count=assets.Count;
        if(mutation=="legacy")FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=12}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(file,"sketches",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackSketches>(bytes);var sketch=data.Sketches.Single();var projection=sketch.NativeProjections![0];
            if(mutation=="cache")sketch=sketch with{Ellipses=[sketch.Ellipses![0] with{Geometry=sketch.Ellipses[0].Geometry with{Center=[100,100]}}]};
            else if(mutation=="fingerprint")sketch=sketch with{NativeProjections=[projection with{Selection=projection.Selection with{Fingerprint=new string('0',64)}}]};
            else sketch=sketch with{NativeProjections=[projection with{Plane=projection.Plane with{X=projection.Plane.X+10}}]};
            return MessagePackSerializer.Serialize(data with{Sketches=[sketch]});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(count,assets.Count);
    }
    [Fact] public async Task NativeProjectionCannotCreateFeatureSketchDependencyCycle()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));d.AddCircle(new(10,0),3);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var s=session.Snapshot.Sketches[d.Value.Id];var p=SketchProfileReference.CreateCircle(s,s.Circles[0].Id);await session.ExecuteAsync(new AddBodyCommand(p.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Consumer",part,sketchSource:p));
        var before=session.Snapshot;var evidence=await kernel.ProjectEdgeAsync(before,part,SketchEllipseProjectionTests.Pick(before,assets,CurveGeometryType.Circle),s.Plane,assets);
        var cycle=new SketchDraft(s);cycle.ProjectNative(evidence);int count=assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(cycle.Value,solver)));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
}
