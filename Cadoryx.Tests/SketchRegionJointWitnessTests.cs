using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionJointWitnessTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Capsules()=>from mirror in new[]{false,true}
        from reverse in new[]{false,true} from alternate in new[]{false,true}
        from phase in new[]{0d,6*Math.Tau} from distance in new[]{.1,-.1}
        select new object[]{mirror,reverse,alternate,phase,distance};

    [Theory][MemberData(nameof(Capsules))]
    public void NaturalEndpointWitnessesRestoreOriginalSlotsAndReflectedNativeUnitsWithIndependentArea(
        bool mirror,bool reverse,bool alternate,double phase,double distance)
    {
        const double angle=.38;
        Point2d Vector(double x,double y){if(mirror)x=-x;return new(x*Math.Cos(angle)-y*Math.Sin(angle),x*Math.Sin(angle)+y*Math.Cos(angle));}
        Point2d Point(double x,double y){var v=Vector(x,y);return new(v.X+3,v.Y-2);}
        SketchCurveExpressionGeometry Line(double x1,double y1,double x2,double y2)=>SketchCurveExpressionGeometry.FromBoundary(new(Point(x1,y1),Point(x2,y2)));
        SketchCurveExpressionGeometry Arc(double x,double start)
        {
            var ellipse=new SketchEllipseGeometry(Point(x,0),Vector(2,0),Vector(0,2),start+phase,Math.PI);
            return SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse});
        }
        var curves=new[]{Line(0,-2,8,-2),Arc(8,-Math.PI/2),Line(8,2,0,2),Arc(0,Math.PI/2)};
        var source=curves.Select((g,i)=>new ExactExpressionRegionSource(SketchEntityId.New(),alternate&&i%2==1?g.Reverse():g,alternate&&i%2==1)).ToImmutableArray();
        if(reverse)source=source.Reverse().Select(s=>s with{Reversed=!s.Reversed}).ToImmutableArray();
        var result=Kernel.OffsetExpressionRegions([source],distance,ExactExpressionRegionJoin.Miter);
        var loop=Assert.Single(result.Loops);Assert.Equal(mirror^reverse,loop.InputTraversalReversed);
        RequireMapping(source,loop);
        Assert.Equal(4,loop.Joints.Length);Assert.All(loop.Joints,j=>Assert.Equal(ExactExpressionRegionJointKind.SourceEndpoints,j.Kind));
        foreach(var joint in loop.Joints)
        {
            var a=source[joint.PreviousSourceSlot].Geometry;var b=source[joint.NextSourceSlot].Geometry;
            Assert.Equal(a.Reversed^joint.PreviousTraversalReversed?a.FirstParameter:a.LastParameter,joint.PreviousParameter);
            Assert.Equal(b.Reversed^joint.NextTraversalReversed?b.LastParameter:b.FirstParameter,joint.NextParameter);
        }
        double expected=32+4*Math.PI+distance*(16+4*Math.PI)+distance*distance*Math.PI;
        Assert.InRange(Math.Abs(result.FilledArea-expected),0,1e-8);
    }

    public static IEnumerable<object[]> Rectangles()=>from policy in Enum.GetValues<ExactExpressionRegionJoin>()
        from reverse in new[]{false,true} from alternate in new[]{false,true} from distance in new[]{.2,-.2}
        select new object[]{policy,reverse,alternate,distance};
    [Theory][MemberData(nameof(Rectangles))]
    public void ActualIntersectionAndBridgeKindsUseOriginalSourceSlotsRatherThanExpandedSegmentIndices(
        ExactExpressionRegionJoin join,bool reverse,bool alternate,double distance)
    {
        var source=Rectangle(0,0,8,4,reverse,alternate);var result=Kernel.OffsetExpressionRegions([source],distance,join);
        var loop=Assert.Single(result.Loops);RequireMapping(source,loop);
        var expected=distance<0||join==ExactExpressionRegionJoin.Miter?ExactExpressionRegionJointKind.NativeIntersection:
            join==ExactExpressionRegionJoin.Round?ExactExpressionRegionJointKind.RoundBridge:ExactExpressionRegionJointKind.BevelBridge;
        Assert.All(loop.Joints,j=>Assert.Equal(expected,j.Kind));
        Assert.Equal(expected==ExactExpressionRegionJointKind.NativeIntersection?4:8,loop.Segments.Length);
        double area=32+24*distance+distance*distance*(distance<0||join==ExactExpressionRegionJoin.Miter?4:join==ExactExpressionRegionJoin.Round?Math.PI:2);
        Assert.InRange(Math.Abs(result.FilledArea-area),0,1e-8);
    }

    [Theory][InlineData(ExactExpressionRegionJoin.Miter,false)][InlineData(ExactExpressionRegionJoin.Round,false)]
    [InlineData(ExactExpressionRegionJoin.Bevel,false)][InlineData(ExactExpressionRegionJoin.Miter,true)]
    [InlineData(ExactExpressionRegionJoin.Round,true)][InlineData(ExactExpressionRegionJoin.Bevel,true)]
    public void HoleWindingAndOutwardSignRemainSeparateFromOriginalInputAdjacency(ExactExpressionRegionJoin join,bool reorder)
    {
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[Rectangle(0,0,16,12,true,true),Rectangle(4,4,8,4,false,true)];
        if(reorder)source=source.Reverse().ToImmutableArray();
        var result=Kernel.OffsetExpressionRegions(source,.2,join);
        foreach(var loop in result.Loops)
        {
            RequireMapping(source[loop.SourceLoopIndex],loop);
            Assert.Equal(loop.Depth==0?.2:-.2,loop.OutwardLoopDistance);
            var kind=loop.Depth==1||join==ExactExpressionRegionJoin.Miter?ExactExpressionRegionJointKind.NativeIntersection:
                join==ExactExpressionRegionJoin.Round?ExactExpressionRegionJointKind.RoundBridge:ExactExpressionRegionJointKind.BevelBridge;
            Assert.All(loop.Joints,j=>Assert.Equal(kind,j.Kind));
        }
    }

    [Theory][InlineData(false,.2)][InlineData(true,.2)][InlineData(false,-.2)][InlineData(true,-.2)]
    public void SingleClosedPeriodicSupportHasNoInventedCornerConnection(bool reflected,double distance)
    {
        var ellipse=new SketchEllipseGeometry(new(3,-2),new(4,0),new(0,reflected?-4:4),.3,Math.Tau);
        var source=new ExactExpressionRegionSource(SketchEntityId.New(),SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse}));
        var result=Kernel.OffsetExpressionRegions([[source]],distance,ExactExpressionRegionJoin.Round);
        var loop=Assert.Single(result.Loops);Assert.False(loop.Joints.IsDefault);Assert.Empty(loop.Joints);Assert.Single(loop.Segments);
        Assert.InRange(Math.Abs(result.FilledArea-Math.PI*(4+distance)*(4+distance)),0,1e-8);
    }

    private static ImmutableArray<ExactExpressionRegionSource> Rectangle(double x,double y,double width,double height,bool reverse,bool alternate)
    {
        Point2d[] points=[new(x,y),new(x+width,y),new(x+width,y+height),new(x,y+height)];
        var source=Enumerable.Range(0,4).Select(i=>
        {
            var g=SketchCurveExpressionGeometry.FromBoundary(new(points[i],points[(i+1)%4]));bool flip=alternate&&i%2==1;
            return new ExactExpressionRegionSource(SketchEntityId.New(),flip?g.Reverse():g,flip);
        }).ToImmutableArray();
        return reverse?source.Reverse().Select(s=>s with{Reversed=!s.Reversed}).ToImmutableArray():source;
    }
    private static void RequireMapping(ImmutableArray<ExactExpressionRegionSource> source,ExactExpressionRegionLoop loop)
    {
        Assert.Equal(source.Length,loop.Joints.Length);
        Assert.Equal(Enumerable.Range(0,source.Length),loop.Joints.Select(j=>j.PreviousSourceSlot).Order());
        foreach(var joint in loop.Joints)
        {
            int step=loop.InputTraversalReversed?-1:1;
            Assert.Equal((joint.PreviousSourceSlot+step+source.Length)%source.Length,joint.NextSourceSlot);
            Assert.Equal(source[joint.PreviousSourceSlot].Id,joint.PreviousSource);Assert.Equal(source[joint.NextSourceSlot].Id,joint.NextSource);
            Assert.Equal(source[joint.PreviousSourceSlot].Reversed^loop.InputTraversalReversed,joint.PreviousTraversalReversed);
            Assert.Equal(source[joint.NextSourceSlot].Reversed^loop.InputTraversalReversed,joint.NextTraversalReversed);
            var previous=Assert.Single(loop.Segments.Where(s=>s.Kind==ExactExpressionRegionSegmentKind.Parallel&&s.SourceIds.Contains(joint.PreviousSource)));
            var next=Assert.Single(loop.Segments.Where(s=>s.Kind==ExactExpressionRegionSegmentKind.Parallel&&s.SourceIds.Contains(joint.NextSource)));
            Assert.InRange(Math.Abs(joint.PreviousParameter-(previous.Geometry.Reversed?previous.Geometry.FirstParameter:previous.Geometry.LastParameter)),0,1e-8);
            Assert.InRange(Math.Abs(joint.NextParameter-(next.Geometry.Reversed?next.Geometry.LastParameter:next.Geometry.FirstParameter)),0,1e-8);
        }
    }
}
