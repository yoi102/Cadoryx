using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

// Real pipe and unequal-loft consumers, beyond the existing
// single-line and congruent-section prism cases.
public sealed class SketchExpressionCurvedConsumerTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PolynomialSpines()=>from spline in new[]{false,true}
        from reverse in new[]{false,true} from tilted in new[]{false,true}
        from lower in new[]{.05,.1} from distance in new[]{-.03,.03}
        select new object[]{spline,reverse,tilted,lower,distance};

    [Theory,MemberData(nameof(PolynomialSpines))]
    public async Task CircleTubeOnExactOffsetPolynomialSpineRetainsFiniteDirectionAndIndependentArcLengthVolume(
        bool spline,bool reverse,bool tilted,double lower,double distance)
    {
        double upper=lower+.8;
        var source=Polynomial(spline,1);var spine=source.Offset(distance).Trim(lower,upper);
        if(reverse)spine=spine.Reverse();
        var first=spine.Evaluate(0);var normal=new Vector3d(first.First.X,first.First.Y,0).Normalized();
        var rotation=Quaterniond.FromAxisAngle(Vector3d.UnitZ.Cross(normal),Math.Acos(Vector3d.UnitZ.Dot(normal)));
        var world=World(tilted);var pose=world*new RigidTransform3d(new(first.Point.X,first.Point.Y,0),rotation);
        const double radius=.04;
        var section=CircleExpression(.03,.01);
        var profile=Profile(section);
        var recipe=new SweepRecipe(new(profile,pose),new([Boundary(spine)],world));
        // Antiderivative in the original author's parameter units. Traversal
        // reversal does not change positive arclength or the authored offset.
        double Primitive(double t)
        {double v=2-4*t;return -(v*Math.Sqrt(1+v*v)+Math.Asinh(v))/8+distance*Math.Atan(v);}
        double length=Primitive(upper)-Primitive(lower);
        double integratedLength=Integral(u=>
        {
            double t=lower+(upper-lower)*u,v=2-4*t,s=Math.Sqrt(1+v*v);
            return (upper-lower)*(s-4*distance/(s*s));
        });
        Assert.InRange(Math.Abs(length-integratedLength),0,1e-12);
        double expected=Math.PI*radius*radius*length;
        await VerifySolid(recipe,expected);
        Assert.True(spine.SameValues(recipe.Path.Curves.Single().Expression!));
        Assert.True(section.SameValues(recipe.Section.Profile.BoundaryCurves.Single().Expression!));
        Assert.Equal(reverse,spine.Reversed);
        output.WriteLine($"spline={spline};reverse={reverse};tilted={tilted};window={lower:R}..{upper:R};distance={distance:R};length={length:R};volume={expected:R}");
    }

    public static IEnumerable<object[]> CircularSpines()=>from family in new[]{"circle","bezier","spline"}
        from reverse in new[]{false,true} from tilted in new[]{false,true}
        from distance in new[]{-.1,.1} select new object[]{family,reverse,tilted,distance};

    [Theory,MemberData(nameof(CircularSpines))]
    public async Task CircleAndPolynomialOffsetSectionsOnCircularExpressionSpineMatchIndependentPappusVolume(
        string family,bool reverse,bool tilted,double distance)
    {
        var spine=CircleExpression(12,distance).Trim(0,.25);if(reverse)spine=spine.Reverse();
        var first=spine.Evaluate(0);var world=World(tilted);
        // localX stays radial in both directions. The polynomial lens is
        // symmetric about this axis's zero coordinate, so its radial moment is0.
        var rotation=reverse?Quaterniond.FromAxisAngle(new(1,1,1),Math.Tau/3):Quaterniond.FromEulerDegrees(-90,0,0);
        var pose=world*new RigidTransform3d(new(first.Point.X,first.Point.Y,0),rotation);
        var section=Section(family,1,reverse);var profile=Profile(section);
        var recipe=new SweepRecipe(new(profile,pose),new([Boundary(spine)],world));
        Assert.InRange((pose.Rotation.Rotate(Vector3d.UnitZ)-world.Rotation.Rotate(new(first.First.X,first.First.Y,0)).Normalized()).Length,0,1e-10);
        double expected=Area(family)*(12+distance)*(Math.PI/2);
        await VerifySolid(recipe,expected);
        Assert.True(spine.SameValues(recipe.Path.Curves.Single().Expression!));
        Assert.True(section.SameValues(recipe.Section.Profile.BoundaryCurves[0].Expression!));
        output.WriteLine($"family={family};reverse={reverse};tilted={tilted};distance={distance:R};volume={expected:R}");
    }

    public static IEnumerable<object[]> VaryingSections()=>from family in new[]{"circle","bezier","spline"}
        from factor in new[]{.8,1.3} from reverse in new[]{false,true} from placement in new[]{0,1}
        select new object[]{family,factor,reverse,placement};

    [Theory,MemberData(nameof(VaryingSections))]
    public async Task UnequalHomotheticExactOffsetLoftSectionsKeepDirectedCorrespondenceAndIndependentVolume(
        string family,double factor,bool reverse,int placement)
    {
        var first=Profile(Section(family,1,reverse));var last=Profile(Section(family,factor,reverse));
        Assert.False(SketchProfileReference.SameProfile(first,last));
        var world=World(placement==1);var delta=placement==1?new Vector3d(1,.4,5):new(0,0,5);
        var id=Guid.NewGuid();var recipe=new LoftRecipe([world,world*new RigidTransform3d(delta,Quaterniond.Identity)],
            [new(id,null,[first,last])],Ruled:true);
        double expected=5*Area(family)*(1+factor+factor*factor)/3;
        await VerifySolid(recipe,expected);
        Assert.Equal(id,recipe.Channels.Single().Id);
        Assert.True(Section(family,1,reverse).SameValues(recipe.Channels[0].Profiles[0].BoundaryCurves[0].Expression!));
        Assert.True(Section(family,factor,reverse).SameValues(recipe.Channels[0].Profiles[1].BoundaryCurves[0].Expression!));
        output.WriteLine($"family={family};factor={factor:R};reverse={reverse};placement={placement};volume={expected:R}");
    }

    private async Task VerifySolid(GeometryRecipe recipe,double expected)
    {
        recipe.Validate();var assets=new MemoryAssetStore();
        using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        {
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);
            Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);
            double error=Math.Abs(result.Geometry.VolumeMm3-expected);output.WriteLine($"actual={result.Geometry.VolumeMm3:R};expected={expected:R};error={error:R}");
            Assert.InRange(error,0,1e-7);
        }
        Assert.Equal(0,assets.Count);
    }
    private static RigidTransform3d World(bool tilted)=>tilted?new(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
    private static SketchCurveExpressionGeometry CircleExpression(double radius,double distance)
    {
        var ellipse=new SketchEllipseGeometry(new(0,0),new(radius,0),new(0,radius),0,Math.Tau);
        return SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse}).Offset(distance);
    }
    private static SketchCurveExpressionGeometry Polynomial(bool spline,double scale)
    {
        Point2d P(double x,double y)=>new((x-.5)*scale,y*scale);
        // Exact degree elevation: both families have x=u-.5,y=2u(1-u).
        var boundary=spline?new SketchBoundaryCurve(P(0,0),P(1,0))
            {SplineControls=[P(0,0),P(1d/3,2d/3),P(2d/3,2d/3),P(1,0)]}:
            new(P(0,0),P(1,0)){BezierControl=P(.5,1)};
        return SketchCurveExpressionGeometry.FromBoundary(boundary);
    }
    private static SketchCurveExpressionGeometry Section(string family,double scale,bool reverse)
    {
        var geometry=family=="circle"?CircleExpression(.5*scale,.03*scale):Polynomial(family=="spline",scale).Offset(.03*scale);
        return reverse?geometry.Reverse():geometry;
    }
    private static SketchBoundaryCurve Boundary(SketchCurveExpressionGeometry geometry)=>new(geometry.At(0),geometry.At(1)){Expression=geometry};
    private static SketchProfile Profile(SketchCurveExpressionGeometry geometry)
    {
        var a=geometry.At(0);var b=geometry.At(1);bool closed=double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-7;
        return new([]){BoundaryCurves=closed?[Boundary(geometry)]:[Boundary(geometry),new(b,a)]};
    }
    private static (Point2d P,Point2d D) OffsetPolynomial(double u)
    {
        double v=2-4*u,s=Math.Sqrt(1+v*v),s3=s*s*s;
        return(new(-.5+u+.03*v/s,2*u*(1-u)-.03/s),new(1-.12/s3,v-.12*v/s3));
    }
    private static double Area(string family)
    {
        if(family=="circle")return Math.PI*.53*.53;
        const double distance=.03;
        // Green's integral plus the endpoint chord has this closed form for
        // x=u-.5, y=2u(1-u). The positive right-normal offset shrinks this lens.
        double area=1d/3-distance*(3/(2*Math.Sqrt(5))+Math.Asinh(2)/4)
            +distance*distance*(Math.Atan(2)-2d/5);
        var a=OffsetPolynomial(0).P;var b=OffsetPolynomial(1).P;
        double integratedArea=Math.Abs(Integral(u=>{var(p,d)=OffsetPolynomial(u);return p.X*d.Y-p.Y*d.X;})+b.X*a.Y-b.Y*a.X)/2;
        Assert.InRange(Math.Abs(area-integratedArea),0,1e-12);
        return area;
    }
    private static double Integral(Func<double,double> function)
    {
        const int steps=8192;double sum=function(0)+function(1);
        for(int i=1;i<steps;i++)sum+=(i%2==0?2:4)*function(i/(double)steps);
        return sum/(3*steps);
    }
}
