using System.Numerics;
using Cadoryx.Db;

namespace Cadoryx.Rendering;

/// <summary>Interpolates copied camera states while keeping a valid orthonormal view frame.</summary>
public static class CadCameraAnimation
{
    public static CadCamera Interpolate(CadCamera from,CadCamera to,double progress)
    {
        if(!double.IsFinite(progress))throw new ArgumentOutOfRangeException(nameof(progress));
        if(progress<=0)return from;
        if(progress>=1)return to;

        var orientation=Quaternion.Normalize(Quaternion.Slerp(Frame(from),Frame(to),(float)progress));
        var back=Vector3.Transform(Vector3.UnitZ,orientation);
        var up=Vector3.Transform(Vector3.UnitY,orientation);
        var target=Lerp(from.Target,to.Target,progress);
        double distance=Mix((from.Eye-from.Target).Length,(to.Eye-to.Target).Length,progress);
        return new(target+new Vector3d(back.X,back.Y,back.Z)*distance,target,
            new(up.X,up.Y,up.Z),Mix(from.Aspect,to.Aspect,progress),Mix(from.Scale,to.Scale,progress),
            Mix(from.FieldOfViewY,to.FieldOfViewY,progress),Mix(from.NearPlane,to.NearPlane,progress),
            Mix(from.FarPlane,to.FarPlane,progress),from.Perspective,from.AutoFitDepth);
    }

    private static Quaternion Frame(CadCamera camera)
    {
        var back=(camera.Eye-camera.Target).Normalized();
        var right=camera.Up.Cross(back).Normalized();
        var up=back.Cross(right).Normalized();
        var matrix=new Matrix4x4(
            (float)right.X,(float)right.Y,(float)right.Z,0,
            (float)up.X,(float)up.Y,(float)up.Z,0,
            (float)back.X,(float)back.Y,(float)back.Z,0,
            0,0,0,1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    private static Vector3d Lerp(Vector3d a,Vector3d b,double t)=>a+(b-a)*t;
    private static double Mix(double a,double b,double t)=>a+(b-a)*t;
}
