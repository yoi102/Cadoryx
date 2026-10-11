using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class ExactHistoryContinuationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ArbitraryOriginalTopologyCrossesPlainHoleAndSecondBooleanOperandAndReopens(bool imported)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exact history"),assets,kernel,new InlineSessionDispatcher()))
        {
            using(var geometry=await kernel.EvaluateAsync(new BoxRecipe(10,12,15,RigidTransform3d.Identity),assets))
            {
                GeometryRecipe recipe=imported?new ImportedRecipe(geometry.Geometry):new BoxRecipe(10,12,15,RigidTransform3d.Identity);
                await session.ExecuteAsync(new AddBodyCommand(recipe,"Source"));
            }
            var original=session.Snapshot.Features.Values.Single();var selections=Pick(session.Snapshot,original,assets);
            await session.ExecuteAsync(new AddHoleCommand(original.OutputBodyId,new(original.Result,RigidTransform3d.Translate(0,0,15),new(5,6),2,true,0),"Hole"));
            var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
            Assert.NotEqual(original.OutputBodyId,hole.OutputBodyId);Assert.False(session.Snapshot.Bodies.ContainsKey(original.OutputBodyId));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(40,0,0)),"Side",original.PartId));
            var side=session.Snapshot.Features.Values.Single(f=>f.Name=="Side");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[side.OutputBodyId,hole.OutputBodyId]));
            var last=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            Assert.Equal(1,TopologyHistoryPaths.Plan(session.Snapshot,original.Id,last.Id).Steps[^1].SourceArgument);
            var results=new Dictionary<ExactTopologySelection,HistoryResolution>();
            foreach(var selected in selections)
            {
                var result=await kernel.TraceExactAsync(session.Snapshot,selected,last.Id,assets);results.Add(selected,result);
                if(result.Status==HistoryResolutionStatus.Resolved)
                {
                    Assert.Equal(2,result.PathLength);Assert.Equal(2,result.CompletedSteps);Assert.Null(result.StoppedAt);
                    Assert.Equal(last.Result.Revision,result.Target!.Revision);Assert.Equal(last.Result.AssetId,result.Target.Asset);
                    using var finalShape=OcctGeometryBridge.ReadShape(last.Result,assets);using var finalMap=RepairSnapshot.Create(finalShape);
                    Assert.Equal(selected.Kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge,finalMap.Topology[result.Target.FullTopologyIndex].Kind);
                }
                else Assert.Null(result.Target);
            }
            Assert.Contains(results,p=>p.Key.Kind==HistoryShapeKind.Face&&p.Value.Status==HistoryResolutionStatus.Resolved);
            Assert.Contains(results,p=>p.Key.Kind==HistoryShapeKind.Edge&&p.Value.Status==HistoryResolutionStatus.Resolved);
            if(!imported)Assert.Equal(HistoryResolutionStatus.Resolved,(await kernel.TraceAsync(session.Snapshot,
                TopologyReference.Box(session.Snapshot,original.Id,BoxBoundary.XMin),last.Id,assets)).Status);
            var saved=files.PathFor("exact.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,saved);
            using(var loaded=await storage.LoadAsync(saved,assets))foreach(var pair in results)
                Assert.Equal(pair.Value,await kernel.TraceExactAsync(loaded.Snapshot,pair.Key,last.Id,assets));
            var before=session.Snapshot;var exact=results.First(p=>p.Value.Status==HistoryResolutionStatus.Resolved).Key;
            using(var changedGeometry=await kernel.EvaluateAsync(new BoxRecipe(11,12,15,RigidTransform3d.Identity),assets))
                await session.ExecuteAsync(new RecomputeCommand(original.Id,imported?new ImportedRecipe(changedGeometry.Geometry):new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
            Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.TraceExactAsync(session.Snapshot,exact,last.Id,assets)).Status);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            Assert.Equal(results[exact],await kernel.TraceExactAsync(session.Snapshot,exact,last.Id,assets));
            await session.RedoAsync();Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.TraceExactAsync(session.Snapshot,exact,last.Id,assets)).Status);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task GeneratedHoleWallStartsAnExactChainAndFinalCompleteSizeIsReinspected()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Generated origin"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,15,RigidTransform3d.Identity),"Cylinder"));
            var cylinder=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(cylinder.OutputBodyId,new(cylinder.Result,RigidTransform3d.Translate(0,0,15),new(0,0),2,true,0),"Hole"));
            var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
            var selected=Pick(session.Snapshot,hole,assets).Single(s=>
            {
                if(s.Kind!=HistoryShapeKind.Face)return false;
                using var shape=OcctGeometryBridge.ReadShape(hole.Result,assets);using var map=RepairSnapshot.Create(shape);using var face=map.CopySubshape(map.Select(s.FullTopologyIndex));
                var surface=face.GetFaceSurfaceSnapshot();if(surface.SurfaceType!=SurfaceGeometryType.Cylinder)return false;
                var point=face.EvaluateFace((surface.FirstUParameter+surface.LastUParameter)/2,(surface.FirstVParameter+surface.LastVParameter)/2).Point;
                return Math.Abs(Math.Sqrt(point.X*point.X+point.Y*point.Y)-1)<1e-8;
            });
            var occurrence=session.Snapshot.EnumerateOccurrences().Single();
            var author=await kernel.ResolveDrawingTopologyAsync(session.Snapshot,occurrence.Path,hole.OutputBodyId,selected.FullTopologyIndex,selected.Fingerprint,assets);
            // Start from the generated wall on the exact hole result, rather than pretending it was a face of the cylinder.
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(4,0,0)),"Side",hole.PartId));
            var side=session.Snapshot.Features.Values.Single(f=>f.Name=="Side");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[side.OutputBodyId,hole.OutputBodyId]));
            var last=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var traced=await kernel.TraceExactAsync(session.Snapshot,selected,last.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Resolved,traced.Status);Assert.Equal(1,traced.CompletedSteps);
            var body=session.Snapshot.Bodies[last.OutputBodyId];using var finalShape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var finalMap=RepairSnapshot.Create(finalShape);
            var reference=await kernel.ResolveDrawingTopologyAsync(session.Snapshot,session.Snapshot.EnumerateOccurrences().Single().Path,body.Id,traced.Target!.FullTopologyIndex,finalMap.Fingerprint,assets);
            Assert.Equal(DrawingAttachmentGeometry.CylinderFace,reference.Geometry);
            var size=await kernel.ResolveDrawingCylinderSizeAsync(session.Snapshot,reference,assets);
            Assert.True(size.Internal);Assert.Equal(2,size.DiameterMm,6);Assert.Equal(15,size.LengthMm,6);
            Assert.Equal(hole.Id,selected.FeatureId);Assert.Equal(last.Id,reference.FeatureId);
            var target=new DrawingSource(reference.Path,reference.BodyId,reference.FeatureId,reference.Revision,reference.Asset,occurrence.WorldTransform);
            var continued=await kernel.ResolveDrawingHistoryAsync(session.Snapshot,author,target,assets);
            Assert.Equal(author,continued.Author);Assert.Equal(reference,continued.Current);Assert.Equal(traced,continued.History);
            foreach(var forged in new[]{author with{LocalPoint=author.LocalPoint+Vector3d.UnitZ},author with{BoundaryEdgeIndex=99999},author with{EdgeParameter=author.EdgeParameter+.1}})
            {
                var rejected=await kernel.ResolveDrawingHistoryAsync(session.Snapshot,forged,target,assets);
                Assert.Equal(HistoryResolutionStatus.Stale,rejected.History.Status);Assert.Null(rejected.Current);
            }
            Assert.Equal(HistoryResolutionStatus.Stale,(await kernel.ResolveDrawingHistoryAsync(session.Snapshot,author,target with{WorldTransform=RigidTransform3d.Translate(1,0,0)},assets)).History.Status);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task SameKindTargetSwapsCannotForgeNativeHistoryEvenWithCorrectFingerprintsAndCoverage()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Native history forgery"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Features.Values.Single();
            var selection=Pick(session.Snapshot,source,assets).First(s=>s.Kind==HistoryShapeKind.Face);
            await session.ExecuteAsync(new AddHoleCommand(source.OutputBodyId,new(source.Result,RigidTransform3d.Translate(0,0,15),new(5,6),2,true,0),"Hole"));
            var target=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var history=target.TopologyHistory!;
            var faces=history.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face&&e.ResultKind==HistoryShapeKind.Face&&e.Evolution==TopologyEvolution.Unchanged).Take(2).ToArray();
            Assert.Equal(2,faces.Length);Assert.NotEqual(faces[0].ResultIndex,faces[1].ResultIndex);
            var swapped=history with{Entries=[..history.Entries.Select(e=>e==faces[0]?e with{ResultIndex=faces[1].ResultIndex}:e==faces[1]?e with{ResultIndex=faces[0].ResultIndex}:e)]};
            swapped.ValidateFor(target);
            var document=session.Snapshot with{Features=session.Snapshot.Features.SetItem(target.Id,target with{TopologyHistory=swapped})};
            int count=assets.Count;var result=await kernel.TraceExactAsync(document,selection,target.Id,assets);
            Assert.Equal(HistoryResolutionStatus.Unsupported,result.Status);Assert.Equal("HISTORY.NATIVE_REPLAY_MISMATCH",result.Diagnostic);Assert.Null(result.Target);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("document")][InlineData("revision")][InlineData("asset")][InlineData("fingerprint")]
    [InlineData("kind")][InlineData("adapter")][InlineData("source-stale")][InlineData("target-stale")]
    public async Task ExactSourceCannotBeReinterpretedAndStaleFeaturesStop(string mutation)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exact context"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,15,RigidTransform3d.Identity),"Cylinder"));var source=session.Snapshot.Features.Values.Single();
            var selection=Pick(session.Snapshot,source,assets).First(s=>s.Kind==HistoryShapeKind.Face);
            await session.ExecuteAsync(new AddHoleCommand(source.OutputBodyId,new(source.Result,RigidTransform3d.Translate(0,0,15),new(0,0),2,true,0),"Hole"));
            var target=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var document=session.Snapshot;
            var expected=HistoryResolutionStatus.Stale;
            switch(mutation)
            {
                case "document":selection=selection with{DocumentId=DocumentId.New()};expected=HistoryResolutionStatus.WrongContext;break;
                case "revision":selection=selection with{Revision=GeometryRevisionId.New()};break;
                case "asset":selection=selection with{Asset=target.Result.AssetId};break;
                case "fingerprint":selection=selection with{Fingerprint=new('0',64)};break;
                case "kind":selection=selection with{Kind=HistoryShapeKind.Edge};expected=HistoryResolutionStatus.WrongContext;break;
                case "adapter":selection=selection with{AdapterVersion="unverified"};expected=HistoryResolutionStatus.Unsupported;break;
                case "source-stale":document=document with{Features=document.Features.SetItem(source.Id,source with{IsStale=true})};break;
                case "target-stale":document=document with{Features=document.Features.SetItem(target.Id,target with{IsStale=true})};break;
            }
            int count=assets.Count;var result=await kernel.TraceExactAsync(document,selection,target.Id,assets);
            Assert.Equal(expected,result.Status);Assert.Null(result.Target);Assert.Equal(count,assets.Count);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.TraceExactAsync(document,selection,target.Id,assets,cancelled.Token));
        }
        Assert.Equal(0,assets.Count);
    }

    private static ExactTopologySelection[] Pick(DocumentSnapshot document,FeatureDefinition feature,IAssetStore assets)
    {
        using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
        return map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge).Select(t=>new ExactTopologySelection(document.Id,feature.Id,
            feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,t.Selection.Index,t.Kind==ShapeKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge,
            OcctGeometryKernel.HistoryAdapterVersion)).ToArray();
    }
}
