using Cadoryx.Db;
using Cadoryx.Rendering;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M8SceneFitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSceneCameraFramesEveryBoundsCornerWithoutKeepingOldDistance(bool perspective)
    {
        var source=new CadCamera(new(0,-1000,0),Vector3d.Zero,new(0,0,1),1.5,1000,45,0.01,10000,perspective,true);
        var bounds=new Bounds3d(new(-100,-50,-10),new(100,50,10));
        var target=SceneEnvelope.FitVisible(source,bounds);
        Assert.Equal(Vector3d.Zero,target.Target);
        Assert.True(target.Scale<source.Scale);
        if(perspective)Assert.True((target.Eye-target.Target).Length<(source.Eye-source.Target).Length);
        var back=(target.Eye-target.Target).Normalized();var right=target.Up.Cross(back).Normalized();
        var up=back.Cross(right);double tan=Math.Tan(target.FieldOfViewY*Math.PI/360);
        foreach(double x in new[]{bounds.Min.X,bounds.Max.X})
        foreach(double y in new[]{bounds.Min.Y,bounds.Max.Y})
        foreach(double z in new[]{bounds.Min.Z,bounds.Max.Z})
        {
            var point=new Vector3d(x,y,z);var offset=point-target.Target;
            double depth=(target.Eye-target.Target).Length-offset.Dot(back);
            double halfHeight=perspective?depth*tan:target.Scale/2;
            Assert.True(halfHeight>0);
            Assert.True(Math.Abs(offset.Dot(up))*1.14<halfHeight);
            Assert.True(Math.Abs(offset.Dot(right))*1.14<halfHeight*target.Aspect);
        }
    }

    [Fact]
    public void PlacedCornersAvoidSecondInflationFromWorldAlignedBounds()
    {
        var geometry=new GeometryAssetRef(new(new string('a',64)),GeometryRevisionId.New(),BodyKind.Solid,
            new(new(-100,-1,-1),new(100,1,1)),400);
        var placement=new RigidTransform3d(Vector3d.Zero,Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/4));
        var item=new SceneItem(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),geometry,placement,0xffffffff);
        var camera=new CadCamera(new(1000,1000,0),Vector3d.Zero,Vector3d.UnitZ,1,1000,45,0.01,10000,false,true);
        var placed=SceneEnvelope.FitVisible(camera,new[]{item});
        var inflated=SceneEnvelope.FitVisible(camera,SceneEnvelope.Measure([item])!.Value);
        Assert.True(placed.Scale<inflated.Scale*0.8);
    }
}
