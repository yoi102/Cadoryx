using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

// Staged outside the current997 source freeze. Not compiled or executed yet.
[Collection("IsolatedNativeDeadlines")]
public sealed class SerializedOccurrenceConsumerTests
{
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task ExactDatumAndSketchSourcesRetainCompleteTableAfterCloseAndFreshReopen(bool stale,bool deferred)
    {
        using var files=new TestFiles();string path=files.PathFor("nested-sources.cadoryx");
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        GeometryAssetRef captured;Guid planeId,axisId,pointId;SketchId sketchId;FeatureId sourceId;
        using(var cylinder=ShapeFactory.CreateCylinder(5,12))
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(cylinder,assets))
        {
            captured=stored.Geometry;var (initial,part)=Source(captured);
            await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
            {
                sourceId=Assert.Single(initial.Features.Keys);
                var face=Pick(initial,assets,HistoryShapeKind.Face,s=>
                {
                    var g=s.GetFaceSurfaceSnapshot();if(g.SurfaceType!=SurfaceGeometryType.Plane)return false;
                    return s.EvaluateFace((g.FirstUParameter+g.LastUParameter)/2,(g.FirstVParameter+g.LastVParameter)/2).Normal.Z>.99;
                });
                var lateral=Pick(initial,assets,HistoryShapeKind.Face,s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);
                var circle=Pick(initial,assets,HistoryShapeKind.Edge,s=>
                {
                    var g=s.GetEdgeCurveSnapshot();return g.CurveType==CurveGeometryType.Circle&&s.EvaluateEdge((g.FirstParameter+g.LastParameter)/2).Point.Z>11.99;
                });
                var faceEvidence=await kernel.ResolveDatumGeometryAsync(initial,face,assets);
                var axisEvidence=await kernel.ResolveDatumGeometryAsync(initial,lateral,assets);
                var circleEvidence=await kernel.ResolveDatumGeometryAsync(initial,circle,assets);
                var plane=new CadDatumPlane(Guid.NewGuid(),part,"Captured face",DatumPlaneKind.Offset,
                    DatumPlaneSource.Face(faceEvidence,RigidTransform3d.Translate(0,0,12)));
                var axis=new CadDatumAxis(Guid.NewGuid(),part,"Captured axis",DatumAxisKind.Analytic,Exact:axisEvidence);
                var point=new CadDatumPoint(Guid.NewGuid(),part,"Captured center",DatumPointKind.CircleCenter,Exact:circleEvidence);
                planeId=plane.Id;axisId=axis.Id;pointId=point.Id;
                await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with
                {
                    Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(plane.Id,plane),
                    Axes=ImmutableDictionary<Guid,CadDatumAxis>.Empty.Add(axis.Id,axis),
                    Points=ImmutableDictionary<Guid,CadDatumPoint>.Empty.Add(point.Id,point)
                }));
                var projection=await kernel.ProjectEdgeAsync(session.Snapshot,part,circle,RigidTransform3d.Identity,assets);
                var draft=new SketchDraft(CadSketch.Create(part,"Captured projection",RigidTransform3d.Identity));
                draft.ProjectNative(projection);sketchId=draft.Value.Id;
                await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
                if(stale)
                {
                    var before=session.Snapshot;
                    // The captured source is an imported feature. Change its actual
                    // imported geometry while retaining that feature kind and identity.
                    using var replacement=await kernel.EvaluateAsync(new CylinderRecipe(7,12,RigidTransform3d.Identity),assets);
                    await session.ExecuteAsync(new RecomputeCommand(sourceId,new ImportedRecipe(replacement.Geometry)));
                    var after=session.Snapshot;
                    Assert.True(after.Datums.Planes[planeId].IsStale);Assert.True(after.Datums.Axes[axisId].IsStale);
                    Assert.True(after.Datums.Points[pointId].IsStale);
                    Assert.False(SketchNativeProjectionGeometry.IsCurrent(after,after.Sketches[sketchId],after.Sketches[sketchId].NativeProjections[0]));
                    Assert.Contains(captured.SerializedOccurrences!.TableAssetId,after.ReferencedAssets());
                    await session.UndoAsync();Assert.Same(before,session.Snapshot);
                    await session.RedoAsync();Assert.Same(after,session.Snapshot);
                }
                await session.SaveAsync(new CadDocumentStorage(datumResolver:kernel,edgeProjector:kernel),path);
            }
        }
        Assert.Equal(0,assets.Count);
        using var fresh=new DiskAssetStore(files.PathFor("fresh"));
        using(var loaded=await new CadDocumentStorage(deferGeometryAssets:deferred,datumResolver:kernel,edgeProjector:kernel).LoadAsync(path,fresh))
        {
            var snapshot=loaded.Snapshot;snapshot.Validate();
            var sources=new[]
            {
                snapshot.Datums.Planes[planeId].Primary.Exact!.Source,
                snapshot.Datums.Axes[axisId].Exact!.Source,
                snapshot.Datums.Points[pointId].Exact!.Source,
                Assert.Single(snapshot.Sketches[sketchId].NativeProjections).Source
            };
            Assert.All(sources,g=>{Assert.Equal(captured,g);Assert.NotNull(g.SerializedOccurrences);Assert.Null(g.SerializedReferences);});
            foreach(var source in sources.Skip(1))Assert.Same(sources[0],source);
            if(!stale)Assert.Same(snapshot.Features[sourceId].Result,sources[0]);
            else Assert.NotEqual(snapshot.Features[sourceId].Result.AssetId,sources[0].AssetId);
            Assert.Equal(stale,snapshot.Datums.Planes[planeId].IsStale);
            Assert.Equal(stale,snapshot.Datums.Axes[axisId].IsStale);
            Assert.Equal(stale,snapshot.Datums.Points[pointId].IsStale);
            await kernel.ConfirmDatumGeometryAsync(snapshot.Datums.Planes[planeId].Primary.Exact!,fresh);
            await kernel.ConfirmDatumGeometryAsync(snapshot.Datums.Axes[axisId].Exact!,fresh);
            await kernel.ConfirmDatumGeometryAsync(snapshot.Datums.Points[pointId].Exact!,fresh);
            var reopenedSketch=snapshot.Sketches[sketchId];var link=Assert.Single(reopenedSketch.NativeProjections);
            var ellipse=Assert.Single(reopenedSketch.Ellipses).Geometry;
            await kernel.ConfirmEdgeProjectionAsync(new(reopenedSketch.PartId,link.Selection,link.Source,link.TargetPlane,
                new(ellipse.At(0),ellipse.At(1),Ellipse:ellipse)),fresh);
            using var graph=OcctGeometryBridge.ReadOccurrenceShape(sources[0],fresh);
            Assert.True(graph.Report.ProvesCompleteDecodedOccurrencePaths);
            Assert.False(graph.Report.NativeSourceCaptureProven);Assert.Null(graph.Report.SourceDomainId);
            Assert.False(graph.Report.ProvesComposedReferenceIdentity);
            using var shape=OcctGeometryBridge.ReadShape(sources[0],fresh);
            Assert.InRange(Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass-captured.VolumeMm3),0,1e-7);
        }
        Assert.Equal(0,fresh.Count);
    }

    [Fact]
    public async Task ActualModeledThreadWorkerAcceptsCapturedSourceAndMatchesDirectGeometryWithoutClaimingIdentity()
    {
        var assets=new MemoryAssetStore();
        using(var root=ShapeFactory.CreateBox(40,40,12))
        using(var source=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets))
        {
            var recipe=Thread(source.Geometry);
            using(var direct=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
            using(var isolated=await new IsolatedMetricThreadExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe")).EvaluateAsync(recipe,assets,CancellationToken.None))
            {
                Assert.NotNull(isolated.TopologyHistory);isolated.TopologyHistory.Validate();
                Assert.Null(isolated.Geometry.SerializedOccurrences);
                Assert.InRange(Math.Abs(direct.Geometry.VolumeMm3-isolated.Geometry.VolumeMm3),0,1e-7);
                double groove=Math.Tau*4.25*((1d/16+3/Math.Sqrt(3))*(9-6.25)-2/(3*Math.Sqrt(3))*(27-15.625));
                Assert.InRange(Math.Abs(isolated.Geometry.VolumeMm3-(1600*12-62.5*Math.PI-groove)),0,.002);
                using var result=OcctGeometryBridge.ReadShape(isolated.Geometry,assets);Assert.True(result.IsValid);
                Assert.InRange(Math.Abs(Math.Abs(result.InspectProperties(InspectionPropertyKind.Volume).Mass)-isolated.Geometry.VolumeMm3),0,1e-7);
            }
            using var current=OcctGeometryBridge.ReadOccurrenceShape(source.Geometry,assets);
            Assert.True(current.Report.ProvesCompleteDecodedOccurrencePaths);Assert.False(current.Report.NativeSourceCaptureProven);
            Assert.Equal(2,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("missing")][InlineData("duplicate")][InlineData("extra")][InlineData("bytes")]
    public async Task ActualThreadWorkerRejectsIncorrectSupplementaryTableBeforeNativeModeling(string fault)
    {
        var assets=new MemoryAssetStore();
        using(var box=ShapeFactory.CreateBox(40,40,12))
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(box,assets))
        {
            string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"Cadoryx","ThreadJobs"));
            string directory=Path.GetFullPath(Path.Combine(root,"thread-"+Guid.NewGuid().ToString("N")));
            Assert.Equal(root,Path.GetDirectoryName(directory));Directory.CreateDirectory(directory);
            try
            {
                var table=stored.Geometry.SerializedOccurrences!.TableAssetId;
                foreach(var id in stored.Geometry.BoundaryAssets())
                {
                    using var lease=assets.Acquire(id);var bytes=lease.Content.ToArray();
                    if(fault=="bytes"&&id==table)bytes[0]^=1;
                    await File.WriteAllBytesAsync(Path.Combine(directory,id.Sha256+".brep"),bytes);
                }
                AssetId[] references=fault switch
                {
                    "missing"=>[],"duplicate"=>[table,table],
                    "extra"=>[table,new AssetId(new string('0',64))],_=>[table]
                };
                await File.WriteAllTextAsync(Path.Combine(directory,"request.json"),JsonSerializer.Serialize(new
                {
                    Contract=1,Recipe=(GeometryRecipe)Thread(stored.Geometry),Assets=new[]{stored.Geometry.AssetId},
                    Current=(GeometryAssetRef?)null,Selected=(DrawingTopologyReference?)null,ReferenceAssets=references
                }));
                var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"))
                    {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                start.ArgumentList.Add("--native-thread-worker");start.ArgumentList.Add(directory);
                using var process=Process.Start(start)!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try{await process.WaitForExitAsync(deadline.Token);}
                finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
                Assert.Equal(1,process.ExitCode);Assert.False(File.Exists(Path.Combine(directory,"native-started.txt")));
                using var response=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"response.json")));
                Assert.Contains(fault=="bytes"?"input hash mismatch":"reference assets differ",response.RootElement.GetProperty("Error").GetString());
                Assert.Equal(0,response.RootElement.GetProperty("Diagnostics").GetArrayLength());Assert.Equal(2,assets.Count);
            }
            finally{Assert.Equal(root,Path.GetDirectoryName(Path.GetFullPath(directory)));Directory.Delete(directory,recursive:true);}
            Assert.False(Directory.Exists(directory));
        }
        Assert.Equal(0,assets.Count);
    }

    private static HoleRecipe Thread(GeometryAssetRef source)=>new(source,RigidTransform3d.Translate(0,0,12),new(12,20),5,false,10,
        Thread:MetricThreadHoles.Create(6,ThreadHand.Right,false,4.25,ThreadHoleRepresentation.Modeled));
    private static (DocumentSnapshot Snapshot,DefinitionId Part) Source(GeometryAssetRef geometry)
    {
        var (snapshot,part)=SketchCommandTests.Document();var feature=FeatureId.New();var body=BodyId.New();
        var definition=(PartDefinition)snapshot.Definitions[part];var layer=snapshot.Layers.Keys.First();
        var result=snapshot with
        {
            Definitions=snapshot.Definitions.SetItem(part,definition with{Bodies=definition.Bodies.Add(body),Features=definition.Features.Add(feature)}),
            Bodies=snapshot.Bodies.Add(body,new(body,part,"Captured source",geometry,feature,layer,new())),
            Features=snapshot.Features.Add(feature,new(feature,part,"Captured source",new ImportedRecipe(geometry),[],body,geometry))
        };
        result.Validate();return(result,part);
    }
    private static ExactTopologySelection Pick(DocumentSnapshot snapshot,IAssetStore assets,HistoryShapeKind kind,Func<Shape,bool> predicate)
    {
        var feature=Assert.Single(snapshot.Features.Values);using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);
        using var map=RepairSnapshot.Create(shape);
        var item=Assert.Single(map.Topology,t=>
        {
            if(t.Kind!=(kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge))return false;
            using var member=map.CopySubshape(t.Selection);return predicate(member);
        });
        return new(snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,item.Selection.Index,kind,OcctGeometryKernel.HistoryAdapterVersion);
    }
}
