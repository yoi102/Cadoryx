using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData("edge")][InlineData("vertex")][InlineData("face")][InlineData("duplicate")][InlineData("multiple")]
    public async Task FaceEditNeverDropsExtraSourceTopologyWhenClaimingSingleSolid(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var box=ShapeFactory.CreateBox(10,12,15))using(var extra=ShapeFactory.CreateBox(2,3,4))using(var edge=ShapeFactory.CreateEdge(new GpPoint(30,30,30),new GpPoint(31,31,31)))
        {
            var faces=extra.GetFaces();var vertices=edge.GetSubShapes(ShapeKind.Vertex);
            try
            {
                using var compound=ShapeFactory.CreateCompound([box,kind switch{"edge"=>edge,"vertex"=>vertices[0],"face"=>faces[0],"duplicate"=>box,_=>extra}]);
                using var stored=OcctGeometryBridge.StoreShape(compound,assets);using var source=OcctGeometryBridge.ReadShape(stored.Geometry,assets);using var map=RepairSnapshot.Create(source);
                var slot=map.Topology.First(t=>t.Kind==ShapeKind.Face);var geometry=stored.Geometry with{Kind=BodyKind.Solid};
                var face=new ExactTopologySelection(DocumentId.New(),FeatureId.New(),geometry.Revision,geometry.AssetId,map.Fingerprint,slot.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);int count=assets.Count;
                foreach(GeometryRecipe recipe in new GeometryRecipe[]{new PlanarPushPullRecipe(geometry,face,2,Vector3d.UnitZ),new DefeatureRecipe(geometry,[face])})
                {await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(recipe,assets));Assert.Equal(count,assets.Count);}
                using var after=RepairSnapshot.Create(source);Assert.Equal(map.Fingerprint,after.Fingerprint);
            }
            finally{foreach(var face in faces)face.Dispose();foreach(var vertex in vertices)vertex.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(2)][InlineData(-2)]
    public async Task ReversedBottomFaceUsesSolidOutwardNormalForSignedDirection(double distance)
    {
        await using var f=await Fixture.Create();var face=f.SelectMany(s=>Math.Abs(s.GetBoundingBox().Maximum.Z)<1e-5).Single();
        var normal=await f.Kernel.InspectPlanarFaceAsync(f.Session.Snapshot,face,f.Assets);Assert.Equal(Vector3d.UnitZ*-1,normal);
        await f.Apply(new PlanarPushPullRecipe(f.Source.Result,face,distance,normal));Assert.Equal(1800+120*distance,f.Edited.Result.VolumeMm3,4);Assert.Equal(-distance,f.Edited.Result.Bounds.Min.Z,5);
    }
}
