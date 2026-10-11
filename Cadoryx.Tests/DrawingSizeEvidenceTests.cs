using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class DrawingSizeEvidenceTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurrentCompleteSizeWallOwnsOriginalRingsAndCannotBeForgedOrReuseChangedSlots(bool internalWall)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Size proof"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(internalWall?new BoxRecipe(12,10,15,RigidTransform3d.Identity):new CylinderRecipe(1,15,RigidTransform3d.Identity),"Source"));
            if(internalWall)
            {var original=session.Snapshot.Bodies.Values.Single();await session.ExecuteAsync(new AddHoleCommand(original.Id,new(original.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,true,0),"Hole"));}
            var doc=session.Snapshot;var body=doc.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
            var item=map.Topology.Single(t=>
            {if(t.Kind!=ShapeKind.Face)return false;using var sub=map.CopySubshape(t.Selection);return sub.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
            var source=await kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,body.Id,item.Selection.Index,map.Fingerprint,assets);
            var proof=await kernel.ResolveDrawingCylinderSizeAsync(doc,source,assets);Assert.Equal(2,proof.DiameterMm,6);Assert.Equal(15,proof.LengthMm,6);
            Assert.Equal(internalWall,proof.Internal);Assert.Equal(3,proof.ProvenFaceIndices.Length);Assert.True(proof.RingEdgeIndices.Length>=2);
            Assert.True(proof.SameProof(proof with{ProvenFaceIndices=[..proof.ProvenFaceIndices],RingEdgeIndices=[..proof.RingEdgeIndices]}));
            Assert.False(proof.SameProof(proof with{DiameterMm=3}));Assert.False(proof.SameProof(proof with{RingEdgeIndices=[123,456]}));
            using var after=RepairSnapshot.Create(shape);Assert.Equal(map.Fingerprint,after.Fingerprint);
            var before=session.Snapshot;
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDrawingCylinderSizeAsync(doc,source with{LocalPoint=source.LocalPoint+Vector3d.UnitZ},assets));
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDrawingCylinderSizeAsync(doc,source with{Fingerprint=new('0',64)},assets));Assert.Same(before,session.Snapshot);
            var feature=doc.Features[body.Producer!.Value];GeometryRecipe recipe=internalWall?feature.Recipe is HoleRecipe hole?hole with{Diameter=3}:throw new InvalidOperationException():new CylinderRecipe(2,15,RigidTransform3d.Identity);
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe));
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveDrawingCylinderSizeAsync(session.Snapshot,source,assets));
        }
        Assert.Equal(0,assets.Count);
    }
}
