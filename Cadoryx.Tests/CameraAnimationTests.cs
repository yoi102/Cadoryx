using Cadoryx.Db;
using Cadoryx.Rendering;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CameraAnimationTests
{
    private static CadCamera Camera(Vector3d eye,Vector3d up)=>
        new(eye,new(5,6,7),up,1.5,120,45,0.1,10000,false,true);

    [Fact]
    public void OppositeViewsKeepValidFrameAndExactEndpoints()
    {
        var from=Camera(new(5,-94,7),new(0,0,1));
        var to=Camera(new(5,106,7),new(0,0,1));
        Assert.Same(from,CadCameraAnimation.Interpolate(from,to,0));
        Assert.Same(to,CadCameraAnimation.Interpolate(from,to,1));
        foreach(double t in new[]{0.1,0.25,0.5,0.75,0.9})
        {
            var frame=CadCameraAnimation.Interpolate(from,to,t);
            Assert.Equal(from.Target,frame.Target);
            Assert.Equal(100,(frame.Eye-frame.Target).Length,4);
            Assert.InRange(frame.Up.Length,0.9999,1.0001);
            Assert.True((frame.Eye-frame.Target).Cross(frame.Up).Length>99);
            Assert.NotEqual(from.Eye,frame.Eye);
            Assert.NotEqual(to.Eye,frame.Eye);
        }
    }

    [Fact]
    public void RetargetedCameraInterpolatesFromItsCurrentState()
    {
        var from=Camera(new(5,-94,7),new(0,0,1));
        var top=Camera(new(5,6,107),new(0,1,0));
        var right=Camera(new(105,6,7),new(0,0,1));
        var middle=CadCameraAnimation.Interpolate(from,top,0.4);
        Assert.Same(middle,CadCameraAnimation.Interpolate(middle,right,0));
        var next=CadCameraAnimation.Interpolate(middle,right,0.1);
        Assert.True((next.Eye-middle.Eye).Length<30);
        Assert.Equal(middle.Target,next.Target);
        Assert.Throws<ArgumentOutOfRangeException>(()=>CadCameraAnimation.Interpolate(from,top,double.NaN));
    }
}
