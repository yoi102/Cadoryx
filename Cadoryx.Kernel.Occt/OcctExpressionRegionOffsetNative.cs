using OcctSharp;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private sealed record RegionNativeSegment(SketchCurveExpression2d Curve,SketchExpressionOffsetSegmentKind Kind,IReadOnlyList<int> SourceIndices);
    private sealed record RegionNativeBoundary(SketchExpressionChain2d Chain,IReadOnlyList<RegionNativeSegment> Segments,
        IReadOnlyList<SketchExpressionOffsetJoint2d> Joints);
    private sealed record RegionNativeLoop(int SourceLoopIndex,int SourceDepth,double OutwardLoopDistance,RegionNativeBoundary Offset);
    private sealed record RegionNativeOffset(SketchExpressionRegion2d Region,IReadOnlyList<RegionNativeLoop> Loops,double FilledArea);

    // A single closed support has no corner to trim. Keeping its exact domain
    // avoids a redundant full-range Trim(0,1), which can move a reflected
    // endpoint outside the SDK's strict finite basis by one floating-point ulp.
    // Multi-curve corners retain the SDK's six windows and 20:1 miter bound.
    private static RegionNativeOffset OffsetClassifiedExpressionRegion(SketchExpressionRegion2d source,double distance,SketchExpressionJoin join)
    {
        RequireRegionSeparation(source.Loops,"SourceBoundary");
        var loops=new RegionNativeLoop[source.Loops.Count];
        for(int i=0;i<loops.Length;i++)
        {
            var original=source.Loops[i];double outward=source.Depths[i]%2==0?distance:-distance;
            try
            {
                RegionNativeBoundary boundary;
                if(original.Curves.Count==1)
                {
                    var curve=original.Curves[0];var shifted=curve.Offset(Math.Sign(original.Measure().SignedArea)*outward);
                    var regularity=SketchCurveRegularity2d.Inspect(shifted);
                    if(!regularity.IsCertified)throw new ArgumentException("Single closed offset regularity is not certified: "+regularity.Diagnostic);
                    RejectRegionCircularCollapse(curve,shifted);
                    var chain=SketchExpressionChain2d.CreateOrdered([shifted],true,1e-7);
                    double before=original.Measure().SignedArea,after=chain.Measure().SignedArea;
                    if(before*after<=0||(Math.Abs(after)-Math.Abs(before))*outward<=0)
                        throw new ArgumentException("Single closed offset crosses an area collapse or lacks monotonic area.");
                    boundary=new(chain,[new(shifted,SketchExpressionOffsetSegmentKind.Parallel,new[]{0})],[]);
                }
                else
                {
                    var offset=SketchExpressionOffset2d.CreateClosed(original,outward,join,20,1e-7);
                    boundary=new(offset.Chain,offset.Segments.Select(s=>new RegionNativeSegment(s.Curve,s.Kind,s.SourceIndices.ToArray())).ToArray(),offset.Joints);
                }
                loops[i]=new(i,source.Depths[i],outward,boundary);
            }
            catch(Exception error) when(error is ArgumentException or OcctException or InvalidOperationException)
            {throw RegionOffsetRejected("CurveOffset",i,-1,error.Message);}
        }
        var candidate=loops.Select(l=>l.Offset.Chain).ToArray();
        if(candidate.Sum(l=>l.Curves.Count)>4096)throw RegionOffsetRejected("ResultBoundary",-1,-1,"Joined region exceeds the unchanged 4096-curve budget.");
        RequireRegionSeparation(candidate,"ResultBoundary");
        SketchExpressionRegion2d result;
        try{result=SketchExpressionRegion2d.Classify(candidate,1e-7);}
        catch(Exception error) when(error is ArgumentException or OcctException or InvalidOperationException)
        {throw RegionOffsetRejected("ResultBoundary",-1,-1,error.Message);}
        for(int i=0;i<loops.Length;i++)
            if(result.Depths[i]!=source.Depths[i]||result.ParentIndices[i]!=source.ParentIndices[i])
                throw RegionOffsetRejected("ChangedContainment",i,result.ParentIndices[i],"Offset changes the explicit source containment tree; reselect the whole region.");
        double Filled(SketchExpressionRegion2d region)
        {
            double sum=0,compensation=0;
            foreach(var loop in region.Loops)
            {double value=loop.Measure().SignedArea-compensation,next=sum+value;compensation=(next-sum)-value;sum=next;}
            return sum;
        }
        double beforeArea=Filled(source),area=Filled(result);
        if(!double.IsFinite(area)||area<=1e-14||(area-beforeArea)*distance<=0)
            throw RegionOffsetRejected("AreaCollapse",-1,-1,"Filled material has no positive monotonic area after the offset.");
        double faceArea=0;
        for(int i=0;i<result.Profiles.Count;i++)
        {
            try
            {
                using var face=result.Profiles[i].CreateFace(SketchPlane.XY);
                if(!face.GetValidationReport(true,true).IsValid||!face.GetSelfInterferenceReport().IsClear)
                    throw new ArgumentException("The actual filled native component has invalid geometry or self-interference.");
                faceArea+=Math.Abs(face.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-12).Properties.Mass);
            }
            catch(Exception error) when(error is ArgumentException or OcctException or InvalidOperationException)
            {throw RegionOffsetRejected("NativeFace",i,-1,error.Message);}
        }
        if(!double.IsFinite(faceArea)||Math.Abs(faceArea-area)>1e-8)
            throw RegionOffsetRejected("NativeFace",-1,-1,"Filled native component area differs from its exact contour integral.");
        return new(result,loops,area);
    }
    private static void RejectRegionCircularCollapse(SketchCurveExpression2d original,SketchCurveExpression2d shifted)
    {
        var leaf=original;while(leaf.Basis is {} basis)leaf=basis;
        if(leaf.Primitive!.Kind!=SketchCurveKind.Circle)return;
        double Radius(SketchCurveExpression2d curve)
        {
            using var edge=curve.ToEdge(SketchPlane.XY);var snapshot=SketchNativeExpressionSnapshot2d.Read(edge,SketchPlane.XY);
            var primitive=snapshot.Nodes[0];double orientation=primitive.XDirection.X*primitive.YDirection.Y-primitive.XDirection.Y*primitive.YDirection.X;
            return primitive.MajorRadius+snapshot.Nodes.Where(n=>n.Kind==SketchNativeCurveKind.Offset).Sum(n=>n.SignedOffset)*orientation;
        }
        double before=Radius(original),after=Radius(shifted);
        if(Math.Abs(after)<=1e-7||before*after<=0)throw new ArgumentException("Circular offset crosses a zero-radius collapse.");
    }
    private static void RequireRegionSeparation(IReadOnlyList<SketchExpressionChain2d> loops,string phase)
    {
        var wires=new List<Shape>();
        try
        {
            foreach(var loop in loops)wires.Add(loop.BuildWire(SketchPlane.XY));
            for(int i=0;i<wires.Count;i++)for(int j=i+1;j<wires.Count;j++)
            {
                double separation=wires[i].DistanceTo(wires[j]).Distance;
                if(!double.IsFinite(separation)||separation<=1e-7)
                    throw RegionOffsetRejected(phase,i,j,"Region boundaries touch, intersect or lack native separation at the unchanged tolerance.");
            }
        }
        finally{foreach(var wire in wires)wire.Dispose();}
    }
    private static CadValidationException RegionOffsetRejected(string phase,int first,int second,string message)
    {
        var error=new CadValidationException("Exact region offset rejected: "+message);
        error.Data["RegionOffset.Failure"]=phase;error.Data["RegionOffset.FirstLoopIndex"]=first;error.Data["RegionOffset.SecondLoopIndex"]=second;
        return error;
    }
}
