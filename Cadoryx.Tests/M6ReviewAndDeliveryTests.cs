using System.Text.Json;
using Cadoryx.Cli;
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

public sealed class M6ReviewAndDeliveryTests
{
    [Fact] public async Task ExactMeasurementsUseWorldPlacementsAndMinimumSurfaceDistance()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,20,30,RigidTransform3d.Identity),assets);
        var a=Instance(box.Geometry,RigidTransform3d.Translate(100,0,0));
        var b=Instance(box.Geometry,RigidTransform3d.Translate(125,0,0));
        var result=await kernel.InspectAsync([a,b],assets);
        Assert.Equal(2200,result.Bodies[0].AreaMm2,7);Assert.Equal(6000,result.Bodies[0].VolumeMm3!.Value,7);
        Assert.Equal(new Vector3d(105,10,15),Round(result.Bodies[0].VolumeCentroidMm!.Value));
        Assert.Equal(15,result.Distance!.DistanceMm,7);
        Assert.Equal(15,(result.Distance.PointOnFirstMm-result.Distance.PointOnSecondMm).Length,7);
        Assert.Equal(6,result.Bodies[0].Faces);Assert.Equal(12,result.Bodies[0].Edges);
    }
    [Fact] public async Task InspectionHonorsRotatedInstancesAndZeroDistanceDoesNotClaimPenetration()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,20,30,RigidTransform3d.Identity),assets);
        var a=Instance(box.Geometry,new(new(100,50,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2)));
        var result=await kernel.InspectAsync([a,a with{BodyId=BodyId.New()}],assets);
        Assert.Equal(new Vector3d(90,55,15),Round(result.Bodies[0].VolumeCentroidMm!.Value));
        Assert.Equal(0,result.Distance!.DistanceMm,7);
        Assert.Equal(6000,result.Bodies[1].VolumeMm3!.Value,7);
    }
    [Fact] public async Task SurfaceOnlyInspectionDoesNotInventVolume()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var edge=OcctSharp.ShapeFactory.CreatePolygonWire([new(0,0,0),new(10,0,0),new(10,20,0),new(0,20,0)],true);
        using var face=OcctSharp.ShapeFactory.CreatePlanarFace(edge);using var geometry=OcctGeometryBridge.StoreShape(face,assets);
        var result=await kernel.InspectAsync([Instance(geometry.Geometry,RigidTransform3d.Identity)],assets);
        Assert.Equal(200,result.Bodies[0].AreaMm2,7);Assert.Null(result.Bodies[0].VolumeMm3);Assert.Null(result.Distance);
    }
    [Fact] public async Task ExactInspectionRejectsOversizedRequestsAndCancellation()
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.InspectAsync([],assets));
        using var geometry=await kernel.EvaluateAsync(new BoxRecipe(1,1,1,RigidTransform3d.Identity),assets);
        var instance=Instance(geometry.Geometry,RigidTransform3d.Identity);
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.InspectAsync(Enumerable.Repeat(instance,257).ToArray(),assets));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.InspectAsync([instance],assets,cancel.Token));
    }
    [Fact] public async Task ReviewMeasurementInvalidatesAfterSelectionOrDocumentChanges()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"A"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var item=Assert.Single(vm.Scene.Items);var target=new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision);
            vm.Selection.Replace([target]);await vm.Review.MeasureAsync();Assert.NotNull(vm.Review.Result);Assert.NotEmpty(vm.Review.Rows);
            vm.Selection.Replace([]);Assert.Null(vm.Review.Result);Assert.Empty(vm.Review.Rows);
            vm.Selection.Replace([target]);await vm.Review.MeasureAsync();
            await session.ExecuteAsync(DocumentEdits.RenameBody(target.BodyId,"B"));Assert.Null(vm.Review.Result);
            Assert.Throws<CadValidationException>(()=>InspectionSelection.Resolve(session.Snapshot,[target with{GeometryRevision=GeometryRevisionId.New()}]));
        }
        finally{vm.Detach();}
    }
    [Fact] public void DiskLeasesDoNotReadUntilConsumedAndReleaseDeduplicatedPayloads()
    {
        using var files=new TestFiles();using var store=new DiskAssetStore(files.PathFor("cache"));
        var first=store.Stage([1,2,3]);var second=store.Stage([1,2,3]);var id=first.Id;
        Assert.Equal(1,store.Count);Assert.Equal(3,store.SizeBytes);Assert.Equal(0,store.ReadCount);
        using(var hold=new DocumentAssetLease(DocumentSnapshot.Create("Disk") with{RetainedAssets=[id]},store))
        {Assert.Equal(3,hold.PayloadSizes()[id]);Assert.Equal(0,store.ReadCount);}
        Assert.Equal(new byte[]{1,2,3},second.Content.ToArray());Assert.Equal(1,store.ReadCount);
        _=second.Content;Assert.Equal(1,store.ReadCount);
        first.Dispose();Assert.Equal(1,store.Count);second.Dispose();second.Dispose();Assert.Equal(0,store.Count);
        Assert.Throws<KeyNotFoundException>(()=>store.Acquire(id));Assert.Throws<ObjectDisposedException>(()=>second.Content);
    }
    [Fact] public void DiskStoreChecksTamperedPayloadAndKeepsLiveLeaseAfterStoreDisposal()
    {
        using var files=new TestFiles();var store=new DiskAssetStore(files.PathFor("cache"));
        var lease=store.Stage([1,2,3]);var path=Directory.GetFiles(store.DirectoryPath,"*.bin").Single();
        File.WriteAllBytes(path,[4,5,6]);Assert.Throws<InvalidDataException>(()=>lease.Content);
        File.WriteAllBytes(path,[1,2,3]);store.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>store.Acquire(lease.Id));
        Assert.Equal(new byte[]{1,2,3},lease.Content.ToArray());lease.Dispose();Assert.False(Directory.Exists(store.DirectoryPath));
    }
    [Fact] public async Task DiskStoreSupportsNativeHistorySaveReloadAndZeroFinalLeases()
    {
        using var files=new TestFiles();using var assets=new DiskAssetStore(files.PathFor("cache"));var kernel=new OcctGeometryKernel();
        var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Disk"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"A"));
            await session.SaveAsync(storage,files.PathFor("a.cadoryx"));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,2,3,RigidTransform3d.Identity),"B"));
            await session.UndoAsync();Assert.Single(session.Snapshot.Bodies);await session.RedoAsync();Assert.Equal(2,session.Snapshot.Bodies.Count);
            using var loaded=await storage.LoadAsync(files.PathFor("a.cadoryx"),assets);
            Assert.Equal(6000,Assert.Single(loaded.Snapshot.Bodies.Values).Geometry.VolumeMm3,6);
        }
        Assert.Equal(0,assets.Count);Assert.Empty(Directory.GetFiles(assets.DirectoryPath,"*.bin"));
    }
    [Fact] public async Task DiskStoreConcurrentConsumersLeaveNoFiles()
    {
        using var files=new TestFiles();using var store=new DiskAssetStore(files.PathFor("cache"));using var seed=store.Stage([3,2,1]);
        await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(()=>{using var lease=store.Acquire(seed.Id);Assert.Equal(3,lease.Content.Length);}))); 
        seed.Dispose();Assert.Equal(0,store.Count);
    }
    [Theory] [InlineData(SectionAxis.X)] [InlineData(SectionAxis.Y)] [InlineData(SectionAxis.Z)]
    public void SectionPlanesDescribeRequestedHalfSpaceAndSlab(SectionAxis axis)
    {
        var section=new SectionView(true,axis,10);var plane=Assert.Single(section.Planes());
        Assert.Equal(0,plane.Normal.Dot(plane.Normal*10)+plane.D,9);
        var reversed=Assert.Single((section with{Reverse=true}).Planes());Assert.Equal(plane.Normal*-1,reversed.Normal);
        var slab=(section with{SlabThicknessMm=4}).Planes();Assert.Equal(2,slab.Count);
        foreach(var p in slab)Assert.True(p.Normal.Dot(plane.Normal*10)+p.D>0);
        Assert.Contains(slab,p=>p.Normal.Dot(plane.Normal*13)+p.D<0);
    }
    [Fact] public void InvalidSectionLeavesAppliedStateUntouched()
    {
        Assert.Throws<CadValidationException>(()=>new SectionView(true,OffsetMm:double.NaN).Planes());
        Assert.Throws<CadValidationException>(()=>new SectionView(true,SlabThicknessMm:0).Planes());
        Assert.Empty(new SectionView().Planes());
    }
    [Fact] public async Task VisibilityFiltersExactOccurrenceAndDoesNotChangeExportSceneOrDocumentState()
    {
        var store=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,20,30,RigidTransform3d.Identity),store);
        var a=Instance(box.Geometry,RigidTransform3d.Identity);var b=Instance(box.Geometry,RigidTransform3d.Translate(100,0,0));
        var scene=new CadScene(a.Path.DocumentId,DocumentStateId.New(),[new(a.Path,a.BodyId,a.Geometry,a.WorldTransform,0xffffffff),new(b.Path,a.BodyId,b.Geometry,b.WorldTransform,0xffffffff)]);
        var review=new ReviewVisibility();review.Isolate([new(a.Path,a.BodyId)]);Assert.Single(review.Apply(scene).Items);Assert.Equal(2,scene.Items.Length);
        review.Hide([new(a.Path,a.BodyId)]);Assert.Empty(review.Apply(scene).Items);review.Reset();Assert.True(scene.Items.SequenceEqual(review.Apply(scene).Items));
    }
    [Fact] public void FocusUsesAllTransformedCornersAndHandlesNarrowViewport()
    {
        var geometry=new GeometryAssetRef(new(new string('a',64)),GeometryRevisionId.New(),BodyKind.Solid,new(new(0,0,0),new(10,20,30)),6000);
        var transform=new RigidTransform3d(new(100,50,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));
        var item=Instance(geometry,transform);
        var bounds=SceneEnvelope.Measure([new(item.Path,item.BodyId,geometry,transform,0xffffffff)])!.Value;
        Assert.Equal(new Vector3d(80,50,0),Round(bounds.Min));Assert.Equal(new Vector3d(100,60,30),Round(bounds.Max));
        var camera=new CadCamera(new(0,0,100),Vector3d.Zero,new(0,1,0),.5,100,45,.1,1000,false,true);
        var focused=SceneEnvelope.Fit(camera,bounds);Assert.Equal(new Vector3d(90,55,15),Round(focused.Target));
        Assert.True(focused.Scale>(bounds.Max-bounds.Min).Length);
    }
    [Theory]
    [InlineData("inspect","--bogus")]
    [InlineData("benchmark","--iterations","0")]
    [InlineData("inspect","--exact","--exact")]
    [InlineData("inspect","--linear","1")]
    public async Task CliRejectsInvalidArguments(params string[] parts)
    {
        var args=new[]{parts[0],"a.step"}.Concat(parts.Skip(1)).ToArray();
        Assert.Equal(2,await CadCommandLine.RunAsync(args,new StringWriter(),new StringWriter()));
    }
    [Theory] [InlineData("step")] [InlineData("iges")] [InlineData("stl")]
    public async Task CliConvertsThreeExchangeFormatsAndProtectsExistingOutput(string extension)
    {
        using var files=new TestFiles();var input=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","v2-box.cadoryx");
        var path=files.PathFor("converted."+extension);var error=new StringWriter();
        Assert.Equal(0,await CadCommandLine.RunAsync(["convert",input,path],new StringWriter(),error));Assert.True(new FileInfo(path).Length>0);
        Assert.Equal(0,await CadCommandLine.RunAsync(["inspect",path,"--exact","--output",files.PathFor("import.json")],new StringWriter(),error));
        if(extension=="stl")
        {
            using var imported=JsonDocument.Parse(File.ReadAllText(files.PathFor("import.json")));
            Assert.Contains(imported.RootElement.GetProperty("diagnostics").EnumerateArray(),d=>d.GetProperty("code").GetString()=="StlUnitless");
        }
        var bytes=File.ReadAllBytes(path);
        Assert.Equal(1,await CadCommandLine.RunAsync(["convert",input,path],new StringWriter(),error));Assert.Equal(bytes,File.ReadAllBytes(path));
        Assert.Equal(2,await CadCommandLine.RunAsync(["convert",input,input,"--overwrite"],new StringWriter(),error));
    }
    [Fact] public async Task CliExactReportAndBenchmarkHaveReproducibleIdentityAndHonestMetrics()
    {
        using var files=new TestFiles();var input=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","v2-box.cadoryx");
        var report=files.PathFor("inspection.json");var error=new StringWriter();
        Assert.Equal(0,await CadCommandLine.RunAsync(["inspect",input,"--exact","--output",report],new StringWriter(),error));
        using(var json=JsonDocument.Parse(File.ReadAllText(report)))Assert.True(json.RootElement.GetProperty("exact").GetProperty("bodies").GetArrayLength()>0);
        var benchmark=files.PathFor("benchmark.json");
        Assert.Equal(0,await CadCommandLine.RunAsync(["benchmark",input,"--output",benchmark,"--iterations","2"],new StringWriter(),error));
        using var results=JsonDocument.Parse(File.ReadAllText(benchmark));var root=results.RootElement;
        Assert.Equal(64,root.GetProperty("sourceSha256").GetString()!.Length);Assert.Equal(2,root.GetProperty("samples").GetArrayLength());
        Assert.Contains("No Viewer",root.GetProperty("limitations").GetString());Assert.Equal("disk",root.GetProperty("assetStore").GetString());
        Assert.True(root.GetProperty("samples")[0].GetProperty("firstBrepReadMs").GetDouble()>0);
    }
    [Fact] public async Task CliCancellationDoesNotCreateOutput()
    {
        using var files=new TestFiles();var path=files.PathFor("cancelled.json");using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Equal(130,await CadCommandLine.RunAsync(["inspect","a.step","--output",path],new StringWriter(),new StringWriter(),cancel.Token));
        Assert.False(File.Exists(path));
    }
    private static GeometryInstance Instance(GeometryAssetRef geometry,RigidTransform3d transform)=>new(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),geometry,transform);
    private static Vector3d Round(Vector3d p)=>new(Math.Round(p.X,7),Math.Round(p.Y,7),Math.Round(p.Z,7));
}
