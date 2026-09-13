using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
namespace Cadoryx.Tests;
public sealed class LocalHistoryInteropTests
{
    [Fact] public void BooleanSummaryProvesSplittingButDoesNotProvidePerFaceLocators()
    {
        using var box=ShapeFactory.CreateBox(10,20,30);
        using var tool=ShapeFactory.CreateBox(2,22,32);
        using var transform=GpTrsf.Create(4,-1,-1,0,0,1,0);
        using var placed=tool.Transformed(transform);
        using var cut=box.CutWithHistory(placed,ShapeKind.Face);
        Assert.True(cut.Shape.IsValid);
        Assert.Equal(4800,Math.Abs(cut.Shape.InspectProperties(InspectionPropertyKind.Volume).Mass),5);
        Assert.True(cut.History.Left.ModifiedResultCount>cut.History.Left.ModifiedSourceCount);
        using var grouped=FeatureModeling.Boolean(FeatureBooleanOperation.Cut,[box],[placed],new FeatureModelingOptions{NonDestructive=true});
        Assert.True(grouped.RequireShape().IsValid);
        Assert.NotEmpty(grouped.History);
        // SourceIndex is an operand index, not a source face or full topology slot.
        Assert.All(grouped.History,h=>Assert.InRange(h.SourceIndex,0,1));
    }
    [Theory][InlineData(false)][InlineData(true)] public void PackageProvidesSourceAndResultTopologyIndices(bool chamfer)
    {
        using var shape=ShapeFactory.CreateBox(10,20,30);using var source=RepairSnapshot.Create(shape);
        var box=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        var edge=source.Topology.Single(t=>
        {if(t.Kind!=ShapeKind.Edge)return false;using var s=source.CopySubshape(t.Selection);return BoxTopology.Matches(s,box,TopologyKind.Edge,BoxBoundary.YMin,BoxBoundary.ZMax);});
        var face=source.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var s=source.CopySubshape(t.Selection);return BoxTopology.Matches(s,box,TopologyKind.Face,BoxBoundary.YMin);});
        using var result=chamfer?ContourChamferRecipe.Create(source,[new(edge.Selection,face.Selection,ChamferDimensions.Symmetric,2)]).Build(source):ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(edge.Selection,2)]).Build(source);
        Assert.True(result.RequireShape().IsValid);
        Assert.Contains(result.History,h=>h.Kind==LocalFeatureHistoryKind.Unchanged&&h.Source is not null&&h.ResultTopologyIndex is not null);
        var assets=new MemoryAssetStore();
        using var stored=OcctGeometryBridge.StoreShape(result.RequireShape(),assets);
        using var reopened=OcctGeometryBridge.ReadShape(stored.Geometry,assets);
        using var before=RepairSnapshot.Create(result.RequireShape());using var after=RepairSnapshot.Create(reopened);
        Assert.Equal(before.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)),after.Topology.Select(t=>(t.Kind,t.Orientation,t.ParentIndex)));
        foreach(var h in result.History.Where(h=>h.ResultTopologyIndex is not null))
        {
            int i=h.ResultTopologyIndex!.Value;
            using var a=before.CopySubshape(before.Select(i));using var b=after.CopySubshape(after.Select(i));
            string Normalized(Shape s){using var g=OcctGeometryBridge.StoreShape(s,assets);using var r=OcctGeometryBridge.ReadShape(g.Geometry,assets);using var p=RepairSnapshot.Create(r);return p.Fingerprint;}
            Assert.Equal(Normalized(a),Normalized(b));
            Assert.Equal(h.Shape!.GetBoundingBox(),a.GetBoundingBox());
        }
    }
}
