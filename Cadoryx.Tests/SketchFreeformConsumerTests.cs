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

namespace Cadoryx.Tests;

public sealed class SketchFreeformConsumerTests
{
    internal static SketchFreeformGeometry Periodic(double factor)=>SketchFreeformGeometryTests.Geometry(3) with
    {
        Poles=SketchFreeformGeometryTests.Geometry(3).Poles.Select(p=>new Point2d(20+(p.X-12)*factor,p.Y*factor)).ToImmutableArray()
    };
    [Fact] public async Task NativeFreeformSourceRevisionFreezesOriginalBrepAndExplicitReselectionRebuildsConsumer()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var shape=SketchFreeformProjectionTests.NativeSource(2))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source",part));
            var source=session.Snapshot.Features.Values.Single();var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.BSplineCurve),RigidTransform3d.Identity,assets);
            var d=new SketchDraft(CadSketch.Create(part,"Linked",RigidTransform3d.Identity));var id=d.ProjectNative(evidence);d.SetConstruction([id],false);
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];var reference=SketchProfileReference.CreateMixed(s,[id]);
            await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Consumer",part,sketchSource:reference));
            var consumer=session.Snapshot.Features.Values.Single(f=>f.Id!=source.Id);var before=session.Snapshot;
            using(var shape=SketchFreeformProjectionTests.NativeSource(5))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))
                await session.ExecuteAsync(new RecomputeCommand(source.Id,new ImportedRecipe(stored.Geometry)));
            var frozen=session.Snapshot;Assert.True(frozen.Features[consumer.Id].IsStale);Assert.Same(consumer.Result,frozen.Features[consumer.Id].Result);
            Assert.Contains(source.Result.AssetId,frozen.ReferencedAssets());
            var storage=new CadDocumentStorage(edgeProjector:kernel);string path=files.PathFor("frozen-freeform.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))Assert.True(SketchNativeProjectionGeometry.HasStaleInputs(loaded.Snapshot,s.Id));
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(frozen,session.Snapshot);
            var sourceOnly=frozen with{Features=frozen.Features.Remove(consumer.Id)};var selection=SketchEllipseProjectionTests.Pick(sourceOnly,assets,CurveGeometryType.BSplineCurve);
            var replacement=await kernel.ProjectEdgeAsync(frozen,part,selection,s.Plane,assets);var edit=new SketchDraft(frozen.Sketches[s.Id]);edit.ProjectNative(replacement,replace:id);
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));Assert.False(session.Snapshot.Features[consumer.Id].IsStale);
            Assert.Equal(id,session.Snapshot.Sketches[s.Id].Freeforms.Single().Id);Assert.True(replacement.Curve.Freeform!.SameValues(session.Snapshot.Sketches[s.Id].Freeforms.Single().Geometry));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task FourNestedPeriodicRegionsAutoNestAndExactExtrusionRetainHoleIslandDefinitions()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Periodic regions",RigidTransform3d.Identity));foreach(double scale in new[]{4d,3,2,1})d.AddFreeform(Periodic(scale));
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
            var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                doc.SelectedTargetPart=part;doc.SelectedSketchId=s.Id;doc.StartSketchFeature("Extrude");
                doc.SelectedSketchProfile=doc.SketchProfiles.Single(p=>p.Source.MixedBoundaryIds.SequenceEqual([s.Freeforms[0].Id]));
                doc.AutoNestSketchRegionsCommand.Execute(null);var seed=doc.ExtrusionSeed();
                await session.ExecuteAsync(new AddBodyCommand(seed.Recipe with{Distance=3},"Nested periodic solid",part,sketchSource:seed.Source));
                var geometry=session.Snapshot.Features.Values.Single().Result;double expected=SketchFreeformProjectionTests.Area(Periodic(1))*(16-9+4-1)*3;
                Assert.InRange(Math.Abs(geometry.VolumeMm3-expected)/expected,0,1e-6);Assert.Equal(BodyKind.Compound,geometry.Kind);
                using var shape=OcctGeometryBridge.ReadShape(geometry,assets);Assert.True(shape.IsValid);
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FreeformLoftAndSweepSectionUseFullRationalPeriodicDefinition(bool loft)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Section",RigidTransform3d.Identity));d.AddFreeform(Periodic(1));
        var profile=SketchProfileBuilder.Mixed(d.Value,[d.Value.Freeforms[0].Id]);GeometryRecipe recipe=loft?
            new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[profile,profile])],Ruled:true):
            new SweepRecipe(new(profile,RigidTransform3d.Identity),new([new(new(0,0),new(0,5))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        {
            double expected=SketchFreeformProjectionTests.Area(Periodic(1))*5;Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-expected)/expected,0,1e-6);
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(shape.IsValid);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task RationalBezierPathUsesExplicitNativeIntervalAndStoredDirection()
    {
        var g=new SketchFreeformGeometry(SketchFreeformKind.Bezier,[new(0,0),new(0,5),new(10,10)],[1,.7071067811865476,1],[],[],2,false,0,1);
        var recipe=new SweepRecipe(new(SketchProfile.FromCircle(new(0,0),.1),RigidTransform3d.Identity),
            new([new(g.At(0),g.At(1)){Freeform=g}],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        {Assert.True(result.Geometry.VolumeMm3>0);using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(shape.IsValid);}
        Assert.Equal(0,assets.Count);
    }
}
