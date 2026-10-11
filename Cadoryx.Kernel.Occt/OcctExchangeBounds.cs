using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static Bounds3d ExchangeGeometryBounds(Shape shape,BodyKind kind,CancellationToken token)
    {
        var box=shape.GetBoundingBox();double[] min=[box.Minimum.X,box.Minimum.Y,box.Minimum.Z],max=[box.Maximum.X,box.Maximum.Y,box.Maximum.Z];
        var coarse=new Bounds3d(new(min[0],min[1],min[2]),new(max[0],max[1],max[2]));coarse.Validate();
        // Native optimal boxes can still include the rectangular parameter domain
        // outside a trimmed curved face. Exchange can reparameterize that surface,
        // changing the conservative box without changing the material geometry.
        // Mesh coordinates retain their native box; BRep supports use six finite
        // outboard planes spanning an expanded native-box projection of the shape.
        if(kind==BodyKind.Mesh)return coarse;
        double margin=Math.Max(1,(coarse.Max-coarse.Min).Length);CadGuard.Finite(margin);
        var supports=new double[6];
        for(int axis=0;axis<3;axis++)for(int side=0;side<2;side++)
        {
            token.ThrowIfCancellationRequested();int u=(axis+1)%3,v=(axis+2)%3;
            double level=side==0?min[axis]-margin:max[axis]+margin;CadGuard.Finite(level);
            GpPoint Corner(bool highU,bool highV)
            {
                double[] p=new double[3];p[axis]=level;p[u]=highU?max[u]+margin:min[u]-margin;p[v]=highV?max[v]+margin:min[v]-margin;
                CadGuard.Finite(p);return new(p[0],p[1],p[2]);
            }
            using var wire=ShapeFactory.CreatePolygonWire([Corner(false,false),Corner(true,false),Corner(true,true),Corner(false,true)],true);
            using var plane=ShapeFactory.CreatePlanarFace(wire);var distance=shape.DistanceTo(plane);
            double[] witness=[distance.PointOnFirst.X,distance.PointOnFirst.Y,distance.PointOnFirst.Z];CadGuard.Finite(witness);CadGuard.Finite(distance.Distance);
            double tolerance=Math.Max(1e-7,distance.Distance*1e-12);
            if(distance.SolutionCount<1||distance.Distance<=0||
                Math.Abs(Math.Abs(level-witness[axis])-distance.Distance)>tolerance||
                (side==0?witness[axis]<=level:witness[axis]>=level)||
                new[]{u,v}.Any(i=>witness[i]<=min[i]-margin+tolerance||witness[i]>=max[i]+margin-tolerance))
                throw new CadValidationException($"Exchange bounds lack a consistent native outboard-plane distance witness: axis={axis}, side={side}, " +
                    $"plane={level:R}, distance={distance.Distance:R}, coordinate={witness[axis]:R}, residual={Math.Abs(Math.Abs(level-witness[axis])-distance.Distance):R}, " +
                    $"witness=({witness[0]:R},{witness[1]:R},{witness[2]:R}), box=({min[0]:R},{min[1]:R},{min[2]:R})-({max[0]:R},{max[1]:R},{max[2]:R}), solutions={distance.SolutionCount}.");
            supports[axis+side*3]=witness[axis];
        }
        var result=new Bounds3d(new(supports[0],supports[1],supports[2]),new(supports[3],supports[4],supports[5]));result.Validate();
        token.ThrowIfCancellationRequested();return result;
    }
}
