using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using Xunit.Abstractions;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class CurvedExchangeBoundsProbeTests(ITestOutputHelper output)
{
    [Theory][InlineData(0,0,0)][InlineData(23,41,17)][InlineData(90,0,0)][InlineData(-12,67,83)]
    public async Task MeasuredBoundsMatchEightIndependentRotatedBoxCorners(double x,double y,double z)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bounds corners"),assets,kernel,new InlineSessionDispatcher()))
        {
            var pose=new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(x,y,z));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,pose),"Box"));var before=session.Snapshot;
            var observed=await kernel.ObserveExchangeAsync(before,assets);var corners=(from a in new[]{0d,2d} from b in new[]{0d,3d} from c in new[]{0d,4d} select pose.Apply(new(a,b,c))).ToArray();
            var expected=new Bounds3d(new(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Min(p=>p.Z)),new(corners.Max(p=>p.X),corners.Max(p=>p.Y),corners.Max(p=>p.Z)));
            Assert.InRange((expected.Min-observed.BoundsMm.Min).Length,0,1e-7);Assert.InRange((expected.Max-observed.BoundsMm.Max).Length,0,1e-7);Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("circle-sweep")][InlineData("bezier-sweep")][InlineData("spline-sweep")]
    public async Task NativePlaneDistanceWitnessesDistinguishCurveBoundsFromGeneratedGeometry(string kind)
    {
        string file=Path.Combine(Path.GetTempPath(),"CadoryxBoundsWitness-"+Guid.NewGuid().ToString("N")+".step");
        await using var f=await CurvedConsumerTestScene.Create(kind,false,true,true);
        try
        {
            await f.Kernel.ExportAsync(f.Session.Snapshot,f.Assets,file);
            using var imported=await f.Kernel.ImportAsync(file,f.Assets);
            using var source=OcctGeometryBridge.ReadShape(f.Session.Snapshot.Bodies.Values.Single().Geometry,f.Assets);
            using var reopened=OcctGeometryBridge.ReadShape(imported.Snapshot.Bodies.Values.Single().Geometry,f.Assets);
            var a=Support(source);var b=Support(reopened);double delta=a.Zip(b).Max(p=>Math.Abs(p.First-p.Second));
            output.WriteLine($"{kind}: support source=[{string.Join(',',a.Select(v=>v.ToString("R")))}]; reopened=[{string.Join(',',b.Select(v=>v.ToString("R")))}]; delta={delta:R}");
            Assert.InRange(delta,0,1e-5);
        }
        finally{if(File.Exists(file))File.Delete(file);}
    }
    private static double[] Support(Shape shape)
    {
        var box=shape.GetBoundingBox();double[] min=[box.Minimum.X,box.Minimum.Y,box.Minimum.Z],max=[box.Maximum.X,box.Maximum.Y,box.Maximum.Z];
        double margin=Math.Max(1,Math.Sqrt(Enumerable.Range(0,3).Sum(i=>Math.Pow(max[i]-min[i],2))));var values=new double[6];
        for(int axis=0;axis<3;axis++)for(int side=0;side<2;side++)
        {
            int u=(axis+1)%3,v=(axis+2)%3;double level=side==0?min[axis]-margin:max[axis]+margin;
            GpPoint Corner(bool highU,bool highV)
            {double[] p=new double[3];p[axis]=level;p[u]=highU?max[u]+margin:min[u]-margin;p[v]=highV?max[v]+margin:min[v]-margin;return new(p[0],p[1],p[2]);}
            using var wire=ShapeFactory.CreatePolygonWire([Corner(false,false),Corner(true,false),Corner(true,true),Corner(false,true)],true);
            using var plane=ShapeFactory.CreatePlanarFace(wire);var distance=shape.DistanceTo(plane);
            Assert.True(distance.SolutionCount>0);double[] witness=[distance.PointOnFirst.X,distance.PointOnFirst.Y,distance.PointOnFirst.Z];
            Assert.InRange(Math.Abs(Math.Abs(level-witness[axis])-distance.Distance),0,1e-7);
            values[axis+side*3]=witness[axis];
        }
        return values;
    }
}
