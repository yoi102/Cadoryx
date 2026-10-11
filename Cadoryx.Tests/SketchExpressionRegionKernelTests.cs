using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionRegionKernelTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static ImmutableArray<ExactExpressionRegionSource> Rectangle(double x,double y,double width,double height,bool reverse,bool alternate)
    {
        Point2d[] points=[new(x,y),new(x+width,y),new(x+width,y+height),new(x,y+height)];if(reverse)Array.Reverse(points);
        return points.Select((p,i)=>
        {
            bool flipped=alternate&&i%2==1;var end=points[(i+1)%points.Length];
            var a=flipped?end:p;var b=flipped?p:end;double length=double.Hypot(b.X-a.X,b.Y-a.Y);
            var shift=new Point2d(-.05*(b.Y-a.Y)/length,.05*(b.X-a.X)/length);
            var g=SketchCurveExpressionGeometry.FromBoundary(new(new(a.X+shift.X,a.Y+shift.Y),new(b.X+shift.X,b.Y+shift.Y))).Offset(.03).Offset(.02);
            return new ExactExpressionRegionSource(SketchEntityId.New(),g,flipped);
        }).ToImmutableArray();
    }
    private static void SourcePrefixes(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> original,ExactExpressionRegionOffset result)
    {
        Assert.Equal(original.Length,result.Loops.Length);
        foreach(var loop in result.Loops)
        {
            Assert.InRange(loop.SourceLoopIndex,0,original.Length-1);var input=original[loop.SourceLoopIndex];
            foreach(var segment in loop.Segments)
            {
                Assert.Equal(segment.OriginalCurveIndices.Select(i=>input[i].Id),segment.SourceIds);
                if(segment.Kind==ExactExpressionRegionSegmentKind.Parallel)
                {
                    Assert.Single(segment.OriginalCurveIndices);Assert.InRange(segment.BasisDepth,1,2);
                    var basis=segment.Geometry;for(int i=0;i<segment.BasisDepth;i++)basis=basis.Basis!;
                    Assert.True(basis.SameValues(input[segment.OriginalCurveIndices[0]].Geometry));
                }
                else{Assert.Equal(2,segment.SourceIds.Length);Assert.Equal(0,segment.BasisDepth);Assert.Null(segment.Geometry.Basis);}
            }
            for(int i=0;i<loop.Segments.Length;i++)
            {
                var a=loop.Segments[i].Geometry.At(1);var b=loop.Segments[(i+1)%loop.Segments.Length].Geometry.At(0);
                Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,1e-7);
            }
        }
    }
    public static IEnumerable<object[]> Rectangles()=>from style in Enum.GetValues<ExactExpressionRegionJoin>()
        from reverse in new[]{false,true} from alternate in new[]{false,true} from distance in new[]{.25,-.25}
        select new object[]{style,reverse,alternate,distance};
    [Theory][MemberData(nameof(Rectangles))]
    public void ThreeJoinsKeepEveryOriginalSourceAcrossLoopAndIndividualAuthorReversals(ExactExpressionRegionJoin join,bool reverse,bool alternate,double distance)
    {
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[Rectangle(0,0,8,4,reverse,alternate)];
        var result=Kernel.OffsetExpressionRegions(source,distance,join);SourcePrefixes(source,result);
        double corner=distance<0||join==ExactExpressionRegionJoin.Miter?4:join==ExactExpressionRegionJoin.Round?Math.PI:2;
        Assert.InRange(Math.Abs(result.FilledArea-(32+24*distance+corner*distance*distance)),0,1e-8);
        var loop=Assert.Single(result.Loops);Assert.Equal(0,loop.Depth);Assert.Null(loop.ParentLoopIndex);Assert.Equal(reverse,loop.InputTraversalReversed);
        Assert.Equal(distance>0&&join!=ExactExpressionRegionJoin.Miter?8:4,loop.Segments.Length);
        foreach(var segment in loop.Segments.Where(s=>s.Kind==ExactExpressionRegionSegmentKind.Parallel))
        {
            var node=segment.Geometry;if(node.Kind==SketchCurveExpressionKind.Continuation)node=node.Basis!;
            var input=source[0][segment.OriginalCurveIndices[0]];
            Assert.Equal(distance*((input.Reversed^reverse)?-1:1),node.SignedOffset);
        }
    }
    private static ExactExpressionRegionSource Circle(double x,double radius,bool mirror)
    {
        var e=new SketchEllipseGeometry(new(x,0),new(radius,0),new(0,mirror?-radius:radius),.2,Math.Tau);
        return new(SketchEntityId.New(),SketchCurveExpressionGeometry.FromBoundary(new(e.At(0),e.At(1)){Ellipse=e}));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FourLevelCirclesAndADisjointRootPreserveExactInputParentIndicesAndAlternatingMaterialSigns(bool reorder)
    {
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[[Circle(0,12,false)],[Circle(0,9,true)],[Circle(0,6,false)],[Circle(0,3,true)],[Circle(40,2,true)]];
        if(reorder)source=[source[3],source[4],source[1],source[0],source[2]];
        var result=Kernel.OffsetExpressionRegions(source,.25,ExactExpressionRegionJoin.Round);SourcePrefixes(source,result);
        double expected=Math.PI*(12.25*12.25-8.75*8.75+6.25*6.25-2.75*2.75+2.25*2.25);
        Assert.InRange(Math.Abs(expected-result.FilledArea),0,1e-8);
        Assert.Equal(reorder?new[]{3,0,1,0,2}:new[]{0,1,2,3,0},result.Loops.Select(l=>l.Depth));
        Assert.Equal(reorder?new int?[]{4,null,3,null,2}:new int?[]{null,0,1,2,null},result.Loops.Select(l=>l.ParentLoopIndex));
        foreach(var loop in result.Loops)Assert.Equal(loop.Depth%2==0?.25:-.25,loop.OutwardLoopDistance);
    }
    public static IEnumerable<object[]> Ellipses()=>from style in Enum.GetValues<ExactExpressionRegionJoin>()
        from mirror in new[]{false,true} from distance in new[]{.1,-.1} from start in new[]{.3,-.3,3*Math.Tau+.3}
        select new object[]{style,mirror,distance,start};
    [Theory][MemberData(nameof(Ellipses))]
    public void AReflectedNoncircularEllipseKeepsItsOriginalAuthorParametersAndHasIndependentSteinerArea(ExactExpressionRegionJoin join,bool mirror,double distance,double start)
    {
        double angle=.38,c=Math.Cos(angle),s=Math.Sin(angle),sign=mirror?-1:1;
        var e=new SketchEllipseGeometry(new(3,-2),new(7*c,7*s),new(-3*s*sign,3*c*sign),start,Math.Tau);
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[[new(SketchEntityId.New(),SketchCurveExpressionGeometry.FromBoundary(new(e.At(0),e.At(1)){Ellipse=e}))]];
        var result=Kernel.OffsetExpressionRegions(source,distance,join);SourcePrefixes(source,result);
        const int n=16384;double step=Math.Tau/n,sum=0;
        double Integrand(double t)=>Math.Sqrt(49*Math.Sin(t)*Math.Sin(t)+9*Math.Cos(t)*Math.Cos(t));
        for(int i=0;i<=n;i++)sum+=(i==0||i==n?1:i%2==0?2:4)*Integrand(i*step);
        double perimeter=sum*step/3,area=21*Math.PI+distance*perimeter+distance*distance*Math.PI;
        Assert.InRange(Math.Abs(area-result.FilledArea),0,1e-8);
        var parallel=result.Loops.Single().Segments.Single();Assert.Equal(e.StartAngle,parallel.Geometry.FirstParameter);Assert.Equal(e.StartAngle+e.SweepAngle,parallel.Geometry.LastParameter);
    }
    [Theory][InlineData("default")][InlineData("empty")][InlineData("loop-count")][InlineData("curve-count")][InlineData("total-count")][InlineData("nesting-depth")][InlineData("null-source")]
    public void ExistingResourceAndOwnedValueBoundsArePreserved(string fault)
    {
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[Rectangle(0,0,8,4,false,false)];
        if(fault=="default")source=default;
        if(fault=="empty")source=[];
        if(fault=="loop-count")source=Enumerable.Repeat(source[0],65).ToImmutableArray();
        if(fault=="curve-count")source=[Enumerable.Repeat(source[0][0],257).ToImmutableArray()];
        if(fault=="total-count")source=Enumerable.Repeat(Enumerable.Repeat(source[0][0],256).ToImmutableArray(),17).ToImmutableArray();
        if(fault=="nesting-depth")source=Enumerable.Range(0,14).Select(i=>ImmutableArray.Create(Circle(0,20-i,false))).ToImmutableArray();
        if(fault=="null-source")source=[source[0].SetItem(0,null!)];
        Assert.Throws<CadValidationException>(()=>Kernel.OffsetExpressionRegions(source,.25,ExactExpressionRegionJoin.Miter));
    }
    [Theory][InlineData("duplicate")][InlineData("distance")][InlineData("join")][InlineData("touch")][InlineData("hole-collapse")][InlineData("source-depth")][InlineData("canceling-line-offset")]
    public void InvalidRegionSourcesOrUnprovenOffsetsReturnNoPartialOwnedResult(string fault)
    {
        ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> source=[Rectangle(0,0,8,4,false,false)];double distance=.25;var join=ExactExpressionRegionJoin.Miter;
        if(fault=="duplicate")source=[source[0].SetItem(1,source[0][1] with{Id=source[0][0].Id})];
        if(fault=="distance")distance=0;if(fault=="join")join=(ExactExpressionRegionJoin)99;
        if(fault=="touch")source=[source[0],Rectangle(8,0,2,2,false,false)];
        if(fault=="hole-collapse")source=[[Circle(0,3,false)],[Circle(0,.1,false)]];
        if(fault=="canceling-line-offset")source=[source[0].Select(s=>s with{Geometry=s.Geometry.Basis!.Basis!.Offset(.03).Offset(-.03)}).ToImmutableArray()];
        if(fault=="source-depth")
        {var loop=source[0].Select(s=>{var g=s.Geometry;for(int i=3;i<64;i++)g=g.Offset(0);return s with{Geometry=g};}).ToImmutableArray();source=[loop];}
        var before=source;Assert.Throws<CadValidationException>(()=>Kernel.OffsetExpressionRegions(source,distance,join));Assert.Equal(before,source);
    }
}
