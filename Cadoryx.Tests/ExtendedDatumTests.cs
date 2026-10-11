using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class ExtendedDatumTests
{
    private static CadDatumPoint Point(DefinitionId part,string name,Vector3d value)=>new(Guid.NewGuid(),part,name,DatumPointKind.Independent,value);
    private static CadDatumPlane Plane(DefinitionId part,string name,DocumentWorkPlaneKind basis,double offset)=>new(Guid.NewGuid(),part,name,DatumPlaneKind.Offset,DatumPlaneSource.Base(basis),Value:offset);
    private static DatumBook Book(IEnumerable<CadDatumPlane>? planes=null,IEnumerable<CadDatumAxis>? axes=null,IEnumerable<CadDatumPoint>? points=null)=>
        DatumBook.Empty with{Planes=(planes??[]).ToImmutableDictionary(p=>p.Id),Axes=(axes??[]).ToImmutableDictionary(a=>a.Id),Points=(points??[]).ToImmutableDictionary(p=>p.Id)};
    private static ExactTopologySelection Pick(DocumentSnapshot doc,FeatureDefinition feature,IAssetStore assets,Func<Shape,bool> wanted,HistoryShapeKind kind=HistoryShapeKind.Face)
    {
        using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
        var selected=map.Topology.Single(t=>
        {if(t.Kind!=(kind==HistoryShapeKind.Face?ShapeKind.Face:ShapeKind.Edge))return false;using var sub=map.CopySubshape(t.Selection);return wanted(sub);});
        return new(doc.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,selected.Selection.Index,kind,OcctGeometryKernel.HistoryAdapterVersion);
    }
    private static bool Top(Shape shape)
    {var s=shape.GetFaceSurfaceSnapshot();if(s.SurfaceType!=SurfaceGeometryType.Plane)return false;var p=shape.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2);return p.Normal.Z>.99;}

    [Fact] public async Task CrossTypeIntersectionsOffsetMidpointAndReverseAxesHaveExactLocalValues()
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();
        await using(var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var xy=Plane(part,"XY3",DocumentWorkPlaneKind.XY,3);var yz=Plane(part,"YZ5",DocumentWorkPlaneKind.YZ,5);var xz=Plane(part,"XZ7",DocumentWorkPlaneKind.XZ,7);
            var intersection=new CadDatumAxis(Guid.NewGuid(),part,"XY/YZ",DatumAxisKind.PlaneIntersection,xy.Id,yz.Id,Reverse:true,Origin:new(9,11,13),Offset:2);
            var corner=new CadDatumPoint(Guid.NewGuid(),part,"Corner",DatumPointKind.ThreePlanes,First:xy.Id,Second:yz.Id,Third:xz.Id);
            var projected=new CadDatumPoint(Guid.NewGuid(),part,"On axis",DatumPointKind.AxisPlane,First:intersection.Id,Second:xz.Id);
            var shifted=new CadDatumPoint(Guid.NewGuid(),part,"Shift",DatumPointKind.Offset,new(2,4,6),corner.Id);
            var mid=new CadDatumPoint(Guid.NewGuid(),part,"Mid",DatumPointKind.Midpoint,First:corner.Id,Second:shifted.Id);
            var two=new CadDatumAxis(Guid.NewGuid(),part,"Two points",DatumAxisKind.TwoPoints,corner.Id,shifted.Id);
            await session.ExecuteAsync(new SetDatumsCommand(Book([xy,yz,xz],[intersection,two],[corner,projected,shifted,mid])));
            var b=session.Snapshot.Datums;Assert.True((b.Points[corner.Id].Position-new Vector3d(5,7,3)).Length<1e-9);Assert.True((b.Points[projected.Id].Position-new Vector3d(5,7,3)).Length<1e-9);
            Assert.True((b.Points[mid.Id].Position-new Vector3d(6,9,6)).Length<1e-9);Assert.True((b.Axes[two.Id].Frame.Rotation.Rotate(Vector3d.UnitZ)-new Vector3d(2,4,6).Normalized()).Length<1e-10);
            Assert.True((b.Axes[intersection.Id].Frame.Translation-new Vector3d(5,9,3)).Length<1e-9);Assert.True(b.Axes[intersection.Id].Frame.Rotation.Rotate(Vector3d.UnitZ).Y<-.99);
            session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task SingularConstructionFailsAtomicallyAndDoesNotAllocateGeometry(int mode)
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var a=Plane(part,"A",DocumentWorkPlaneKind.XY,0);var b=Plane(part,"B",DocumentWorkPlaneKind.XY,10);var p=Point(part,"P",new(1,2,3));var q=Point(part,"Q",p.Value);
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",mode==0?DatumAxisKind.TwoPoints:DatumAxisKind.PlaneIntersection,mode==0?p.Id:a.Id,mode==0?q.Id:b.Id);
        var book=Book([a,b],[axis],[p,q]);
        if(mode==2){var c=Plane(part,"C",DocumentWorkPlaneKind.XY,20);book=Book([a,b,c],points:[new(Guid.NewGuid(),part,"Bad",DatumPointKind.ThreePlanes,First:a.Id,Second:b.Id,Third:c.Id)]);}
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(book)));Assert.Same(before,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task PointsAndAxisDriveCircularArraySketchAndDrawingInOneUndoStep()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            var p=Point(part,"Axis base",Vector3d.Zero);var q=Point(part,"Axis end",new(0,0,10));var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.TwoPoints,p.Id,q.Id);
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Normal",DatumPlaneKind.Offset,DatumPlaneSource.Normal(axis.Id,p.Id));
            await session.ExecuteAsync(new SetDatumsCommand(Book([plane],[axis],[p,q])));axis=session.Snapshot.Datums.Axes[axis.Id];
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Translate(10,0,0)),"Source",part));var source=session.Snapshot.Bodies.Values.Single();
            var recipe=new RepetitionRecipe(source.Geometry,RepetitionKind.Circular,axis.Frame,4,0,[Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()],FullCircle:true,Datum:axis.Id,DatumRevision:axis.Revision);
            await session.ExecuteAsync(new AddRepetitionCommand(source.Id,recipe,"Ring"));var ring=session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);
            var sketch=CadSketch.Create(part,"Datum sketch",session.Snapshot.Datums.Planes[plane.Id].Frame);await session.ExecuteAsync(new UpsertSketchCommand(sketch,new Cadoryx.Sketching.ManagedSketchConstraintSolver(),datum:plane.Id));
            var sheet=TechnicalDrawingSheet.A4Landscape("Datum drawing");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(100,100),1));
            var before=session.Snapshot;var driver=new CadParameter(Guid.NewGuid(),"shift",ParameterDimension.Length,"6 mm");
            await session.ExecuteAsync(new SetParametersCommand(new([driver],[new(Guid.NewGuid(),driver.Id,ParameterTarget.Datum(p.Id,"X")),new(Guid.NewGuid(),driver.Id,ParameterTarget.Datum(q.Id,"X"))])));
            var after=session.Snapshot;Assert.Equal(6,after.Datums.Axes[axis.Id].Frame.Translation.X,8);Assert.Equal(6,after.Sketches[sketch.Id].Plane.Translation.X,8);
            Assert.Equal(96,after.Features[ring.Id].Result.VolumeMm3,5);Assert.NotEqual(before.Features[ring.Id].Result.AssetId,after.Features[ring.Id].Result.AssetId);
            Assert.All(after.DrawingSheets.Values.SelectMany(s=>s.Views),v=>{Assert.Null(v.StaleReason);Assert.Equal(after.Features[ring.Id].Result.Revision,v.Sources.Single(s=>s.Feature==ring.Id).Revision);});after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var path=files.PathFor("datums.cadoryx");await session.SaveAsync(new CadDocumentStorage(datumResolver:kernel),path);
            using var loaded=await new CadDocumentStorage(datumResolver:kernel).LoadAsync(path,assets);loaded.Snapshot.Validate();Assert.Equal(2,loaded.Snapshot.Datums.Points.Count);Assert.Equal(after.Datums.Axes[axis.Id].Frame,loaded.Snapshot.Datums.Axes[axis.Id].Frame);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CylinderAndConeExtractDirectedAxisOnRotatedTranslatedConsumedSource(bool cone)
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            using var primitive=cone?ShapeFactory.CreateCone(5,2,12):ShapeFactory.CreateCylinder(5,12);using var pose=GpTrsf.Create(7,11,13,0,1,0,Math.PI/3);using var moved=primitive.Transformed(pose);using var stored=OcctGeometryBridge.StoreShape(moved,assets);
            await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Analytic",part));var f=session.Snapshot.Features.Values.Single();
            var selected=Pick(session.Snapshot,f,assets,s=>s.GetFaceSurfaceSnapshot().SurfaceType==(cone?SurfaceGeometryType.Cone:SurfaceGeometryType.Cylinder));
            var e=await kernel.ResolveDatumGeometryAsync(session.Snapshot,selected,assets);Assert.Equal(cone?ExactDatumKind.Cone:ExactDatumKind.Cylinder,e.Kind);
            var expected=new Vector3d(Math.Sin(Math.PI/3),0,Math.Cos(Math.PI/3));Assert.True((e.Direction-expected).Length<1e-9);Assert.True((e.Point-new Vector3d(7,11,13)).Cross(expected).Length<1e-8);
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Native axis",DatumAxisKind.Analytic,Exact:e,Origin:new(7,11,13));await session.ExecuteAsync(new SetDatumsCommand(Book(axes:[axis])));
            var current=session.Snapshot.Datums.Axes[axis.Id];Assert.True((current.Frame.Translation-new Vector3d(7,11,13)).Length<1e-8);
            await session.ExecuteAsync(new AddRepetitionCommand(f.OutputBodyId,new(f.Result,RepetitionKind.Linear,RigidTransform3d.Identity,2,30,[Guid.NewGuid(),Guid.NewGuid()]),"Consumed"));
            Assert.False(session.Snapshot.Bodies.ContainsKey(f.OutputBodyId));var again=await kernel.ResolveDatumGeometryAsync(session.Snapshot,selected,assets);Assert.Equal(e,again);
            await kernel.ConfirmDatumGeometryAsync(e,assets);await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmDatumGeometryAsync(e with{Point=e.Point+new Vector3d(1,0,0)},assets));
            session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ExactPlaneUsesAuthoredOriginAndUDirectionAndChangedSourceFreezesArrayUntilReselected()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Base",part));var f=session.Snapshot.Features.Values.Single();var selected=Pick(session.Snapshot,f,assets,Top);var e=await kernel.ResolveDatumGeometryAsync(session.Snapshot,selected,assets);
            var authored=new RigidTransform3d(new(2,3,99),Quaterniond.FromAxisAngle(Vector3d.UnitZ,.37));var plane=new CadDatumPlane(Guid.NewGuid(),part,"Exact top",DatumPlaneKind.Offset,DatumPlaneSource.Face(e,authored));
            await session.ExecuteAsync(new SetDatumsCommand(Book([plane])));plane=session.Snapshot.Datums.Planes[plane.Id];Assert.Equal(new Vector3d(2,3,15),plane.Frame.Translation);Assert.True(DatumPlanes.SameFrame(plane.Frame,authored with{Translation=new(2,3,15)}));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(20,0,0)),"Source",part));var body=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Source");
            await session.ExecuteAsync(new AddRepetitionCommand(body.Id,new(body.Geometry,RepetitionKind.Linear,plane.Frame,2,5,[Guid.NewGuid(),Guid.NewGuid()],Datum:plane.Id,DatumRevision:plane.Revision),"Array"));var array=session.Snapshot.Features.Values.Single(g=>g.Recipe is RepetitionRecipe);
            await session.ExecuteAsync(new RecomputeCommand(f.Id,new BoxRecipe(10,12,18,RigidTransform3d.Identity)));var frozen=session.Snapshot;frozen.Validate();Assert.True(frozen.Datums.Planes[plane.Id].IsStale);Assert.True(frozen.Features[array.Id].IsStale);Assert.Equal(array.Result,frozen.Features[array.Id].Result);
            Assert.Throws<CadValidationException>(()=>(frozen with{Datums=frozen.Datums with{Planes=frozen.Datums.Planes.SetItem(plane.Id,frozen.Datums.Planes[plane.Id] with{Frame=RigidTransform3d.Translate(99,99,99)})}}).Validate());
            var path=files.PathFor("frozen.cadoryx");await session.SaveAsync(new CadDocumentStorage(datumResolver:kernel),path);using(var loaded=await new CadDocumentStorage(datumResolver:kernel).LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.True(loaded.Snapshot.Datums.Planes[plane.Id].IsStale);}
            f=session.Snapshot.Features[f.Id];var newSelection=Pick(session.Snapshot,f,assets,Top);var nextEvidence=await kernel.ResolveDatumGeometryAsync(session.Snapshot,newSelection,assets);
            await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(plane.Id,plane with{Primary=DatumPlaneSource.Face(nextEvidence,authored)})}));
            Assert.False(session.Snapshot.Datums.Planes[plane.Id].IsStale);Assert.False(session.Snapshot.Features[array.Id].IsStale);Assert.Equal(18,session.Snapshot.Datums.Planes[plane.Id].Frame.Translation.Z,8);session.Snapshot.Validate();
            var detach=session.Snapshot.Datums.Planes[plane.Id];await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(plane.Id,detach with{Primary=DatumPlaneSource.Independent(detach.Frame),Value=0})}));
            await session.ExecuteAsync(new RecomputeCommand(f.Id,new BoxRecipe(10,12,22,RigidTransform3d.Identity)));Assert.Equal(18,session.Snapshot.Datums.Planes[plane.Id].Frame.Translation.Z,8);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task MixedAxisPointPlaneCycleAndDuplicateNamesAreRejectedBeforeNativeAllocation()
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var p=Point(part,"Point",Vector3d.Zero);var q=Point(part,"Q",new(0,0,1));var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",DatumAxisKind.TwoPoints,p.Id,q.Id);var plane=new CadDatumPlane(Guid.NewGuid(),part,"Plane",DatumPlaneKind.Offset,DatumPlaneSource.Normal(axis.Id));
        p=p with{Kind=DatumPointKind.AxisPlane,First=axis.Id,Second=plane.Id};var before=session.Snapshot;
        Assert.Contains("cycle",(await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(Book([plane],[axis],[p,q]))))).Message);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(Book([plane],points:[q with{Name="PLANE"}]))));Assert.Same(before,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ExtendedPayloadMasqueradeAndTamperedAnalyticCacheAreRejectedWithoutLeaks()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Base",part));var f=session.Snapshot.Features.Values.Single();var e=await kernel.ResolveDatumGeometryAsync(session.Snapshot,Pick(session.Snapshot,f,assets,Top),assets);
            var p=new CadDatumPlane(Guid.NewGuid(),part,"Exact",DatumPlaneKind.Offset,DatumPlaneSource.Face(e,RigidTransform3d.Identity));await session.ExecuteAsync(new SetDatumsCommand(Book([p])));
            var storage=new CadDocumentStorage(datumResolver:kernel);var path=files.PathFor("tampered.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="datums"?s with{SchemaVersion=1}:s).ToImmutableArray()});await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
            await session.SaveAsync(storage,path);FormatEvolutionTests.RewriteSection(path,"datums",bytes=>
            {var b=MessagePackSerializer.Deserialize<PackDatumBook>(bytes);var plane=b.Planes.Single();var evidence=plane.Primary.Exact!;return MessagePackSerializer.Serialize(b with{Planes=[plane with{Primary=plane.Primary with{Exact=evidence with{Point=evidence.Point with{X=evidence.Point.X+1}}}}]});});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
            await session.SaveAsync(storage,path);await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void NativeIdentityKindAndHiddenCompanionsCannotBeSubstituted()
    {
        var(doc,part)=SketchCommandTests.Document();var point=Point(part,"P",Vector3d.Zero);Assert.Throws<CadValidationException>(()=>(point with{Third=Guid.NewGuid()}).Validate());
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",DatumAxisKind.Basis);Assert.Throws<CadValidationException>(()=>(axis with{First=Guid.NewGuid()}).Validate());
        var independent=DatumPlaneSource.Independent(RigidTransform3d.Identity);Assert.Throws<CadValidationException>(()=>(independent with{Feature=FeatureId.New()}).Validate());
        Assert.Throws<CadValidationException>(()=>DatumConstructions.Frame(Vector3d.Zero,Vector3d.UnitZ,Vector3d.UnitZ));
    }
    [Fact] public async Task PointNormalPlaneActuallyLocatesHoleAndChangesItsNativeResult()
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            var point=Point(part,"Hole entry",new(5,5,20));var axis=new CadDatumAxis(Guid.NewGuid(),part,"Hole normal",DatumAxisKind.Basis);
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Hole frame",DatumPlaneKind.Offset,DatumPlaneSource.Normal(axis.Id,point.Id));
            await session.ExecuteAsync(new SetDatumsCommand(Book([plane],[axis],[point])));plane=session.Snapshot.Datums.Planes[plane.Id];
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,20,RigidTransform3d.Identity),"Block",part));var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,plane.Frame,new(0,0),2,Datum:plane.Id,DatumRevision:plane.Revision),"Hole"));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
            var before=session.Snapshot;var parameter=new CadParameter(Guid.NewGuid(),"x",ParameterDimension.Length,"12 mm");
            await session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Datum(point.Id,"X"))])));
            var after=session.Snapshot;Assert.Equal(8000-20*Math.PI,after.Features[feature.Id].Result.VolumeMm3,4);Assert.Equal(12,((HoleRecipe)after.Features[feature.Id].Recipe).Frame.Translation.X,8);
            Assert.NotEqual(before.Features[feature.Id].Result.AssetId,after.Features[feature.Id].Result.AssetId);after.Validate();await session.UndoAsync();Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task AnalyticCircleCenterAxisAndNativeCacheRoundtripRetainIndependentValues()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,12,RigidTransform3d.Translate(7,11,13)),"Cylinder",part));var f=session.Snapshot.Features.Values.Single();
            bool TopCircle(Shape s){var c=s.GetEdgeCurveSnapshot();return c.CurveType==CurveGeometryType.Circle&&s.EvaluateEdge((c.FirstParameter+c.LastParameter)/2).Point.Z>24;}
            var selection=Pick(session.Snapshot,f,assets,TopCircle,HistoryShapeKind.Edge);var evidence=await kernel.ResolveDatumGeometryAsync(session.Snapshot,selection,assets);
            var center=new CadDatumPoint(Guid.NewGuid(),part,"Center",DatumPointKind.CircleCenter,Exact:evidence);var axis=new CadDatumAxis(Guid.NewGuid(),part,"Circle axis",DatumAxisKind.Analytic,Exact:evidence,Reverse:true);
            await session.ExecuteAsync(new SetDatumsCommand(Book(axes:[axis],points:[center])));Assert.True((session.Snapshot.Datums.Points[center.Id].Position-new Vector3d(7,11,25)).Length<1e-8);Assert.Equal(5,evidence.Radius,8);
            var path=files.PathFor("circle.cadoryx");var storage=new CadDocumentStorage(datumResolver:kernel);await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);Assert.Equal(evidence,loaded.Snapshot.Datums.Points[center.Id].Exact);loaded.Snapshot.Validate();
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDatumGeometryAsync(session.Snapshot,selection with{Fingerprint=new string('a',64)},assets));
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDatumGeometryAsync(session.Snapshot,selection with{Kind=HistoryShapeKind.Face},assets));
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDatumGeometryAsync(session.Snapshot,selection with{DocumentId=DocumentId.New()},assets));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FinalShellAndDraftPlanarFacesAreValidExactDatumSources(bool draft)
    {
        var(doc,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Block",part));var source=session.Snapshot.Features.Values.Single();
            var face=Pick(session.Snapshot,source,assets,draft?s=>{var g=s.GetFaceSurfaceSnapshot();if(g.SurfaceType!=SurfaceGeometryType.Plane)return false;return s.EvaluateFace((g.FirstUParameter+g.LastUParameter)/2,(g.FirstVParameter+g.LastVParameter)/2).Normal.X<-.99;}:Top);
            GeometryRecipe recipe=draft?new DraftRecipe(source.Result,[new(Guid.NewGuid(),face,.05,RigidTransform3d.Identity,Vector3d.UnitZ)]):new ShellRecipe(source.Result,[face],1);
            await session.ExecuteAsync(new AddFaceFeatureCommand(source.OutputBodyId,recipe,"Final"));var final=session.Snapshot.Features.Values.Single(f=>f.Id!=source.Id);
            bool Bottom(Shape s){var g=s.GetFaceSurfaceSnapshot();return g.SurfaceType==SurfaceGeometryType.Plane&&s.EvaluateFace((g.FirstUParameter+g.LastUParameter)/2,(g.FirstVParameter+g.LastVParameter)/2).Normal.Z<-.99;}
            var picked=Pick(session.Snapshot,final,assets,Bottom);var evidence=await kernel.ResolveDatumGeometryAsync(session.Snapshot,picked,assets);
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Final face",DatumPlaneKind.Offset,DatumPlaneSource.Face(evidence,new(new(2,3,8),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI))));
            await session.ExecuteAsync(new SetDatumsCommand(Book([plane])));Assert.True((session.Snapshot.Datums.Planes[plane.Id].Frame.Translation-new Vector3d(2,3,0)).Length<1e-8);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
