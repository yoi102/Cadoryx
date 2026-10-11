using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class ExtendedDatumBoundaryTests
{
    [Theory][InlineData(128,true)][InlineData(129,false)]
    public async Task DatumDepthBoundaryIsIndependentOfDictionaryOrder(int depth,bool allowed)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();
        var points=Enumerable.Range(0,depth).Select(i=>new CadDatumPoint(Guid.NewGuid(),part,"P"+i,DatumPointKind.Independent)).ToArray();
        for(int i=1;i<points.Length;i++)points[i]=points[i] with{Kind=DatumPointKind.Offset,First=points[i-1].Id,Value=new(1,0,0)};
        await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var book=DatumBook.Empty with{Points=points.Reverse().ToImmutableDictionary(p=>p.Id)};
            if(allowed){await session.ExecuteAsync(new SetDatumsCommand(book));Assert.Equal(depth-1,session.Snapshot.Datums.Points[points[^1].Id].Position.X,8);session.Snapshot.Validate();}
            else{await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(book)));Assert.Same(initial,session.Snapshot);}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(.001)][InlineData(1)][InlineData(10000)]
    public async Task ThreePlaneAndAxisPlaneIntersectionsRemainExactAtDifferentCoordinateScales(double scale)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();
        var rotation=Quaterniond.FromEulerDegrees(17,29,41);var center=new Vector3d(3,5,7)*scale;
        CadDatumPlane Plane(string name,Quaterniond local)=>new(Guid.NewGuid(),part,name,DatumPlaneKind.Offset,
            DatumPlaneSource.Independent(new(center,rotation*local)));
        var xy=Plane("XY",Quaterniond.Identity);var yz=Plane("YZ",Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2));var xz=Plane("XZ",Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2));
        var a=new CadDatumAxis(Guid.NewGuid(),part,"Line",DatumAxisKind.PlaneIntersection,xy.Id,yz.Id,Origin:center+rotation.Rotate(new(0,3*scale,0)));
        var p=new CadDatumPoint(Guid.NewGuid(),part,"Three",DatumPointKind.ThreePlanes,First:xy.Id,Second:yz.Id,Third:xz.Id);
        var q=new CadDatumPoint(Guid.NewGuid(),part,"AxisPlane",DatumPointKind.AxisPlane,First:a.Id,Second:xz.Id);
        await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=new[]{xy,yz,xz}.ToImmutableDictionary(p=>p.Id),Axes=ImmutableDictionary<Guid,CadDatumAxis>.Empty.Add(a.Id,a),Points=new[]{p,q}.ToImmutableDictionary(p=>p.Id)}));
            foreach(var point in session.Snapshot.Datums.Points.Values)Assert.InRange((point.Position-center).Length,0,Math.Max(1e-9,scale*1e-9));session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NearParallelPlanesAndLinePlaneRefuseIllConditionedIntersectionAtomically(bool point)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();
        var a=new CadDatumPlane(Guid.NewGuid(),part,"A",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY));
        var b=new CadDatumPlane(Guid.NewGuid(),part,"B",DatumPlaneKind.Offset,DatumPlaneSource.Independent(new(new(0,0,1),Quaterniond.FromAxisAngle(new(1,0,0),1e-10))));
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",point?DatumAxisKind.Independent:DatumAxisKind.PlaneIntersection,point?null:a.Id,point?null:b.Id,Direction:point?new Vector3d(1,0,1e-10):Vector3d.Zero);
        var book=DatumBook.Empty with{Planes=new[]{a,b}.ToImmutableDictionary(p=>p.Id),Axes=ImmutableDictionary<Guid,CadDatumAxis>.Empty.Add(axis.Id,axis)};
        if(point){var p=new CadDatumPoint(Guid.NewGuid(),part,"Point",DatumPointKind.AxisPlane,First:axis.Id,Second:a.Id);book=book with{Points=book.Points.Add(p.Id,p)};}
        await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(book)));Assert.Same(initial,session.Snapshot);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LateNativePickAfterGenerationChangeOrCloseCannotPublishAndCloseRetainsBaselineLease(bool close)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();var kernel=new GatedDatumKernel(native);
        var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,5,RigidTransform3d.Identity),"Cylinder",part));var feature=session.Snapshot.Features.Values.Single();
        ExactTopologySelection selection;
        using(var shape=OcctGeometryBridge.ReadShape(feature.Result,assets))using(var map=RepairSnapshot.Create(shape))
        {
            var item=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
            selection=new(session.Snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
        }
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());var editor=new DatumEditorViewModel(vm,kernel);Task? disposal=null;
        try
        {
            editor.ConstructionTab=1;editor.AxisName="Late axis";editor.NativeSource=editor.NativeSources.Single();var pick=editor.PickExactAsync(selection);
            await kernel.Picked.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.True(editor.IsWorking);
            if(close)
            {
                disposal=editor.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);await session.DisposeAsync();Assert.True(assets.Count>0);
            }
            else await session.ExecuteAsync(ResourceCommands.AddPart(DefinitionId.New(),"External change"));
            kernel.Release.TrySetResult();await pick;if(disposal is not null)await disposal;
            Assert.False(editor.CanConfirm);Assert.Empty(editor.Axes);Assert.Empty(session.Snapshot.Datums.Axes);
            if(close)Assert.Equal(0,assets.Count);else Assert.True(editor.IsStale);
        }
        finally{kernel.Release.TrySetResult();await editor.DisposeAsync();vm.Detach();await session.DisposeAsync();}
        Assert.Equal(0,assets.Count);
    }
    private sealed class GatedDatumKernel(OcctGeometryKernel native):IGeometryKernel,IDatumGeometryResolver
    {
        internal TaskCompletionSource Picked {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;
        public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public Task ConfirmDatumGeometryAsync(ExactDatumGeometry e,IAssetStore assets,CancellationToken token=default)=>native.ConfirmDatumGeometryAsync(e,assets,token);
        public async Task<ExactDatumGeometry> ResolveDatumGeometryAsync(DocumentSnapshot doc,ExactTopologySelection s,IAssetStore assets,CancellationToken token=default)
        {
            // Signal once the input lease is acquired, before waiting for the shared native queue.
            // Deliberately ignore cancellation to model a native result arriving after editor close.
            var pending=native.ResolveDatumGeometryAsync(doc,s,assets,CancellationToken.None);
            Picked.TrySetResult();await Release.Task;return await pending;
        }
    }
}
