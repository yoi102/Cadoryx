using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed partial class FiniteSurfaceExtrusionTests
{
    private static SketchProfile Square=>new([new(3,3),new(7,3),new(7,7),new(3,7)]);
    [Theory][InlineData("plane")][InlineData("sphere")][InlineData("cylinder")]
    public async Task ExactFiniteCapsMatchIndependentAnalyticVolumesAndUndo(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Finite stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind);
            var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            var before=session.Snapshot;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until finite surface",target.PartId,target.Id));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            double expected=kind switch
            {
                "sphere"=>500+Math.PI*4*13-2*Math.PI/3*(216-Math.Pow(32,1.5)),
                "cylinder"=>500+4*(52-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
                _=>612
            };
            Assert.InRange(Math.Abs(expected-feature.Result.VolumeMm3),0,1e-5);
            Assert.Null(feature.TopologyHistory);Assert.Equal(2,session.Snapshot.Bodies.Count);
            session.Snapshot.Validate();var after=session.Snapshot;
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task NativeSuccessOnUndersizedPlaneCannotBeAcceptedAsFiniteTermination()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Small stop"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"small");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            var before=session.Snapshot;long generation=session.Generation;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Reject",target.PartId,target.Id)));
            Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactHoleAndRecursiveIslandDomainsAreRetained(bool island)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Material domain"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            var profile=SketchProfile.FromCircle(new(5,5),2) with{Holes=[new(new(5,5),1)]};
            if(island)profile=profile with{Islands=[new([],new(new(5,5),.5))]};
            await session.ExecuteAsync(new AddExtrusionCommand(recipe with{Profile=profile},"Holes and islands",target.PartId,target.Id));
            var result=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe).Result;
            Assert.InRange(Math.Abs(500+(island?3.25:3)*Math.PI*7-result.VolumeMm3),0,1e-5);
        }
        Assert.Equal(0,assets.Count);
    }
    internal static async Task<ExtrudeRecipe> Seed(CadDocumentSession session,MemoryAssetStore assets,string kind,RigidTransform3d? placement=null)
    {
        var outer=placement??RigidTransform3d.Identity;
        bool reverse=kind=="reverse",cut=kind=="cut";
        double startZ=reverse?-5:cut?20:5;
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,cut?20:5,outer*(reverse?RigidTransform3d.Translate(0,0,-5):RigidTransform3d.Identity)),"Target"));
        var target=session.Snapshot.Bodies.Values.Single();
        var support=Pick(session.Snapshot,target.Producer!.Value,assets,f=>
        {
            using var inverse=OcctGeometryBridge.ToNative(outer.Inverse());using var local=f.Transformed(inverse);var b=local.GetBoundingBox();
            return reverse?b.Maximum.Z<-4.999:b.Minimum.Z>startZ-.001;
        });
        var plane=new SketchPlane(new(5,5,reverse?-12:12),new(1,0,0),new(0,1,0));
        using var stop=kind switch
        {
            "sphere"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,
                new(new(5,5,18),new(1,0,0),new(0,1,0)),new(0,Math.Tau,-Math.PI/2,0),6),
            "cylinder"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,
                new(new(5,0,18),new(1,0,0),new(0,0,-1)),new(0,Math.PI,0,10),6),
            "small"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,plane,new(-.5,.5,-.5,.5)),
            _=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,plane,new(-5,5,-5,5))
        };
        using var transform=OcctGeometryBridge.ToNative(outer);using var moved=stop.Transformed(transform);
        using var stored=OcctGeometryBridge.StoreShape(moved,assets);
        await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Finite stop",target.PartId));
        var limit=session.Snapshot.Features.Values.Single(f=>f.Name=="Finite stop");
        var selection=Pick(session.Snapshot,limit.Id,assets,_=>true);
        return new(kind=="sphere"?SketchProfile.FromCircle(new(5,5),2):Square,0,outer*RigidTransform3d.Translate(0,0,startZ),
            new(reverse||cut?ExtrusionSide.Negative:ExtrusionSide.Positive,Operation:cut?ExtrusionOperation.Cut:ExtrusionOperation.Fuse,Source:target.Geometry,Termination:ExtrusionTermination.FiniteSurface,
                LimitFace:selection,LimitGeometry:limit.Result,SupportFace:support,SurfaceBranch:ExtrusionSurfaceBranch.Near));
    }
    internal static ExactTopologySelection Pick(DocumentSnapshot doc,FeatureId id,IAssetStore assets,Func<Shape,bool> predicate)
    {
        var feature=doc.Features[id];using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
        var faces=shape.GetFaces();
        try
        {
            var face=faces.Single(predicate);int index=RepairSnapshot.FindTopologyIndex(shape,face);
            return new(doc.Id,id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
        }
        finally{foreach(var face in faces)face.Dispose();}
    }
}
