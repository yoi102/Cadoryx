using Cadoryx.Db;
using Cadoryx.Rendering.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class OccurrenceGizmoMathTests
{
    [Fact]
    public void DecomposePreservesRotationTranslationAndUniformScale()
    {
        var rotation=Quaterniond.FromAxisAngle(new Vector3d(0,0,1),Math.PI/2);
        var (rigid,scale)=OccurrenceGizmoMath.Decompose([
            0,-2,0,12,
            2,0,0,-4,
            0,0,2,7]);
        Assert.Equal(2,scale,6);
        Assert.Equal(new Vector3d(12,-4,7),rigid.Translation);
        Assert.True((rigid.Rotation.Rotate(new Vector3d(1,0,0))-rotation.Rotate(new Vector3d(1,0,0))).Length<1e-10);
        Assert.Throws<CadValidationException>(()=>OccurrenceGizmoMath.Decompose([
            1,0.5,0,0,0,1,0,0,0,0,1,0]));
    }

    [Fact]
    public void ScalePrimitiveKeepsRotatedBoxAndCylinderCentersFixed()
    {
        var placement=new RigidTransform3d(new(9,-3,5),
            Quaterniond.FromAxisAngle(new Vector3d(0,1,0),Math.PI/3));
        var box=new BoxRecipe(4,6,8,placement);
        var scaledBox=Assert.IsType<BoxRecipe>(OccurrenceGizmoMath.ScalePrimitive(box,1.5));
        Assert.Equal(6,scaledBox.X);Assert.Equal(9,scaledBox.Y);Assert.Equal(12,scaledBox.Z);
        var beforeBox=placement.Apply(new(2,3,4));
        var afterBox=scaledBox.Placement.Apply(new(3,4.5,6));
        Assert.True((afterBox-beforeBox).Length<1e-10);

        var cylinder=new CylinderRecipe(3,10,placement);
        var scaledCylinder=Assert.IsType<CylinderRecipe>(OccurrenceGizmoMath.ScalePrimitive(cylinder,0.5));
        Assert.Equal(1.5,scaledCylinder.Radius);Assert.Equal(5,scaledCylinder.Height);
        Assert.True((scaledCylinder.Placement.Apply(new(0,0,2.5))-
            placement.Apply(new(0,0,5))).Length<1e-10);
    }
}
