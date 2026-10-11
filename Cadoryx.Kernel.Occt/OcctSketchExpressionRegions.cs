using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using AuthorKind=Cadoryx.Db.SketchCurveExpressionKind;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IExactSketchExpressionRegions
{
    public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,
        double outwardDistance,ExactExpressionRegionJoin join)=>ExactGuard(()=>
    {
        if(loops.IsDefault||loops.Length is <1 or >64||loops.Any(l=>l.IsDefault||l.Length is <1 or >256)||loops.Sum(l=>l.Length)>4096)
            throw new CadValidationException("Region offset exceeds the existing loop/curve resource bounds.");
        CadGuard.Finite(outwardDistance);if(Math.Abs(outwardDistance)<=1e-7||!Enum.IsDefined(join))
            throw new CadValidationException("Region offset needs a nonzero finite distance and explicit join.");
        var ids=new HashSet<SketchEntityId>();
        foreach(var source in loops.SelectMany(l=>l))
        {
            if(source is null||source.Geometry is null)throw new CadValidationException("A region source needs an owned author identity and exact geometry.");
            CadGuard.Id(source.Id);source.Geometry.Validate();if(!ids.Add(source.Id))throw new CadValidationException("A region source identity occurs in more than one input slot.");
        }
        var frames=loops.Select(loop=>loop.Select(s=>NativeExpressionCurve(PrepareExpression(s.Geometry))).ToArray()).ToArray();
        var input=loops.Select((loop,i)=>SketchExpressionChain2d.CreateOrdered(loop.Select((s,j)=>
            s.Reversed?frames[i][j].Curve.Reverse():frames[i][j].Curve).ToArray(),true)).ToArray();
        var classified=SketchExpressionRegion2d.Classify(input);
        if(classified.Depths.Any(d=>d>12))throw new CadValidationException("Region nesting depth exceeds twelve.");
        var reversed=input.Select((loop,i)=>Math.Sign(loop.Measure().SignedArea)!=Math.Sign(classified.Loops[i].Measure().SignedArea)).ToArray();
        for(int i=0;i<input.Length;i++)for(int j=0;j<input[i].Curves.Count;j++)
        {
            int original=reversed[i]?input[i].Curves.Count-1-j:j;var expected=input[i].Curves[original];if(reversed[i])expected=expected.Reverse();
            if(!SameNativeExpression(expected,classified.Loops[i].Curves[j]))throw new CadValidationException("Region classification changed more than its declared loop direction.");
        }
        var offset=OffsetClassifiedExpressionRegion(classified,outwardDistance,(SketchExpressionJoin)join);
        if(offset.Loops.Count!=loops.Length||offset.Region.Loops.Count!=loops.Length)throw new CadValidationException("Region offset changed source loop correspondence.");
        var copied=ImmutableArray.CreateBuilder<ExactExpressionRegionLoop>();double filled=0;
        for(int i=0;i<loops.Length;i++)
        {
            var loop=offset.Loops[i];var original=loops[i];
            if(loop.SourceLoopIndex!=i||loop.SourceDepth!=classified.Depths[i]||offset.Region.Depths[i]!=classified.Depths[i]||
                offset.Region.ParentIndices[i]!=classified.ParentIndices[i])throw new CadValidationException("Region offset changed the explicit source containment tree.");
            var segments=ImmutableArray.CreateBuilder<ExactExpressionRegionSegment>();
            foreach(var segment in loop.Offset.Segments)
            {
                if(!Enum.IsDefined(segment.Kind)||segment.SourceIndices.Any(j=>j<0||j>=original.Length))
                    throw new CadValidationException("Region offset contains unknown source segment correspondence.");
                var sourceIndices=segment.SourceIndices.Select(j=>reversed[i]?original.Length-1-j:j).ToImmutableArray();
                var sourceIds=sourceIndices.Select(j=>original[j].Id).ToImmutableArray();SketchCurveExpressionGeometry geometry;int depth=0;bool traversal=false;
                if(segment.Kind==SketchExpressionOffsetSegmentKind.Parallel)
                {
                    if(sourceIndices.Length!=1)throw new CadValidationException("A parallel segment needs exactly one source.");
                    int j=sourceIndices[0];var source=original[j];traversal=source.Reversed^reversed[i];
                    (geometry,depth)=CopyParallel(segment.Curve,classified.Loops[i].Curves[segment.SourceIndices[0]],source.Geometry,frames[i][j],traversal);
                }
                else
                {
                    if(sourceIndices.Length!=2||segment.SourceIndices[1]!=(segment.SourceIndices[0]+1)%original.Length)
                        throw new CadValidationException("A bridge requires two explicitly adjacent source slots.");
                    geometry=CopyBridge(segment.Curve,segment.Kind);
                }
                geometry=PrepareExpression(geometry);RequireCopiedRegionCurve(segment.Curve,geometry);
                segments.Add(new(geometry,(ExactExpressionRegionSegmentKind)segment.Kind,sourceIndices,sourceIds,depth,traversal));
            }
            if(segments.Count is <1 or >256)throw new CadValidationException("Region result exceeds the existing boundary curve count.");
            var copiedChain=SketchExpressionChain2d.CreateOrdered(segments.Select(s=>NativeExpressionCurve(PrepareExpression(s.Geometry)).Curve).ToArray(),true);
            using var copiedFace=SketchExpressionProfile2d.Create(copiedChain).CreateFace(SketchPlane.XY);
            if(!copiedFace.GetValidationReport(true,true).IsValid||!copiedFace.GetSelfInterferenceReport().IsClear)
                throw new CadValidationException("Copied region face has invalid geometry or self-interference.");
            double copiedArea=Math.Abs(copiedFace.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-12).Properties.Mass);
            double nativeArea=Math.Abs(loop.Offset.Chain.Measure().SignedArea);
            if(Math.Abs(copiedArea-nativeArea)>1e-8)throw new CadValidationException($"Copied region boundary differs from its native area: face={copiedArea:R}, contour={nativeArea:R}.");
            filled+=(loop.SourceDepth%2==0?1:-1)*copiedArea;
            var ownedSegments=segments.ToImmutable();
            var joints=CopyRegionJoints(loop.Offset.Joints,original,frames[i],reversed[i],ownedSegments);
            copied.Add(new(i,loop.SourceDepth,classified.ParentIndices[i]<0?null:classified.ParentIndices[i],reversed[i],loop.OutwardLoopDistance,ownedSegments){Joints=joints});
        }
        if(!double.IsFinite(filled)||filled<=1e-12||Math.Abs(filled-offset.FilledArea)>1e-8)
            throw new CadValidationException("Copied filled region area differs from the native result.");
        return new ExactExpressionRegionOffset(copied.ToImmutable(),outwardDistance,filled);
    });
    private static (SketchCurveExpressionGeometry Geometry,int Depth) CopyParallel(SketchCurveExpression2d result,SketchCurveExpression2d normalizedSource,
        SketchCurveExpressionGeometry original,NativeExpression frame,bool traversalReversed)
    {
        var parallel=result.Kind==OcctSharp.SketchCurveExpressionKind.Continuation?result.Basis!:result;
        if(parallel.Kind!=OcctSharp.SketchCurveExpressionKind.Offset||parallel.Basis is null||!SameNativeExpression(parallel.Basis,normalizedSource))
            throw new CadValidationException("Parallel output lost the complete declared source prefix.");
        double a=RegionAuthorParameter(original,frame,result.FirstParameter),b=RegionAuthorParameter(original,frame,result.LastParameter);double first=Math.Min(a,b),last=Math.Max(a,b);
        bool direction=result.Reversed^(frame.ParameterSign<0);var offset=original.Offset(parallel.SignedOffset*(traversalReversed?-1:1));
        if(result.Kind==OcctSharp.SketchCurveExpressionKind.Continuation)
            return(new(AuthorKind.Continuation,null,offset,0,first,last,direction),2);
        return(offset with{FirstParameter=first,LastParameter=last,Reversed=direction},1);
    }
    private static double RegionAuthorParameter(SketchCurveExpressionGeometry original,NativeExpression frame,double value)
        =>value==frame.Curve.FirstParameter?(frame.ParameterSign>0?original.FirstParameter:original.LastParameter):
            value==frame.Curve.LastParameter?(frame.ParameterSign>0?original.LastParameter:original.FirstParameter):(value-frame.ParameterShift)/frame.ParameterSign;

    private static ImmutableArray<ExactExpressionRegionJoint> CopyRegionJoints(IReadOnlyList<SketchExpressionOffsetJoint2d> native,
        ImmutableArray<ExactExpressionRegionSource> original,NativeExpression[] frames,bool reversed,
        ImmutableArray<ExactExpressionRegionSegment> segments)
    {
        if(native.Count!=(original.Length==1?0:original.Length))
            throw new CadValidationException("Region connection witnesses do not cover the complete original corner set.");
        var copied=ImmutableArray.CreateBuilder<ExactExpressionRegionJoint>(native.Count);
        for(int i=0;i<native.Count;i++)
        {
            var joint=native[i];
            if(joint.PreviousSourceIndex!=i||joint.NextSourceIndex!=(i+1)%original.Length)
                throw new CadValidationException("Region connection witness changed its directed source adjacency.");
            int previous=reversed?original.Length-1-joint.PreviousSourceIndex:joint.PreviousSourceIndex;
            int next=reversed?original.Length-1-joint.NextSourceIndex:joint.NextSourceIndex;
            var a=original[previous];var b=original[next];
            double u=RegionAuthorParameter(a.Geometry,frames[previous],joint.PreviousParameter);
            double v=RegionAuthorParameter(b.Geometry,frames[next],joint.NextParameter);
            CadGuard.Finite(u,v);
            bool ra=a.Reversed^reversed,rb=b.Reversed^reversed;
            var kind=joint.Kind switch
            {
                SketchExpressionOffsetJointKind.SourceEndpoints=>ExactExpressionRegionJointKind.SourceEndpoints,
                SketchExpressionOffsetJointKind.NativeIntersection=>ExactExpressionRegionJointKind.NativeIntersection,
                SketchExpressionOffsetJointKind.RoundBridge=>ExactExpressionRegionJointKind.RoundBridge,
                SketchExpressionOffsetJointKind.BevelBridge=>ExactExpressionRegionJointKind.BevelBridge,
                _=>throw new CadValidationException("Unknown creating-operation region connection branch.")
            };
            if(kind!=ExactExpressionRegionJointKind.NativeIntersection&&
                (u!=(a.Geometry.Reversed^ra?a.Geometry.FirstParameter:a.Geometry.LastParameter)||
                 v!=(b.Geometry.Reversed^rb?b.Geometry.LastParameter:b.Geometry.FirstParameter)))
                throw new CadValidationException("Natural or bridged connection lost its declared directed source endpoints.");
            RequireBound(previous,u,false);RequireBound(next,v,true);
            copied.Add(new(previous,next,a.Id,b.Id,kind,u,v,ra,rb));
        }
        return copied.ToImmutable();

        void RequireBound(int slot,double parameter,bool start)
        {
            var parallel=segments.Where(s=>s.Kind==ExactExpressionRegionSegmentKind.Parallel&&s.OriginalCurveIndices.SequenceEqual(new[]{slot})).ToArray();
            if(parallel.Length!=1)throw new CadValidationException("A connection needs one complete declared parallel output per adjacent source.");
            var geometry=parallel[0].Geometry;
            double bound=start?(geometry.Reversed?geometry.LastParameter:geometry.FirstParameter):(geometry.Reversed?geometry.FirstParameter:geometry.LastParameter);
            if(Math.Abs(parameter-bound)>1e-8*Math.Max(1,Math.Abs(parameter)))
                throw new CadValidationException("Connection witness differs from its copied parallel output bound.");
        }
    }
    private static SketchCurveExpressionGeometry CopyBridge(SketchCurveExpression2d result,SketchExpressionOffsetSegmentKind kind)
    {
        if(result.Kind!=OcctSharp.SketchCurveExpressionKind.Primitive||result.Primitive is not {} primitive)
            throw new CadValidationException("A connection must retain its explicit primitive definition.");
        Point2d P(SketchPoint2d point)=>new(point.X,point.Y);
        if(kind==SketchExpressionOffsetSegmentKind.BevelBridge&&primitive.Kind==SketchCurveKind.Segment)
            return SketchCurveExpressionGeometry.FromBoundary(new(P(primitive.Evaluate(0).Point),P(primitive.Evaluate(1).Point)));
        if(kind!=SketchExpressionOffsetSegmentKind.RoundBridge||primitive.Kind!=SketchCurveKind.Circle||primitive.Poles.Count!=1)
            throw new CadValidationException("Unknown exact connection primitive.");
        double c=Math.Cos(primitive.AxisAngle),s=Math.Sin(primitive.AxisAngle),radius=primitive.MajorRadius;
        var ellipse=new SketchEllipseGeometry(P(primitive.Poles[0]),new(radius*c,radius*s),new(-radius*s,radius*c),
            primitive.Reversed?primitive.LastParameter:primitive.FirstParameter,(primitive.Reversed?-1:1)*(primitive.LastParameter-primitive.FirstParameter));
        return SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse});
    }
    private void RequireCopiedRegionCurve(SketchCurveExpression2d native,SketchCurveExpressionGeometry copied)
    {
        foreach(double t in new[]{0d,.37,1})
        {
            var actual=EvaluateExpression(copied,t);var expected=native.Evaluate(t).Point;
            if(double.Hypot(actual.Point.X-expected.X,actual.Point.Y-expected.Y)>1e-9)throw new CadValidationException("Copied region segment changed its exact finite geometry.");
        }
    }
    private static bool SameNativeExpression(SketchCurveExpression2d a,SketchCurveExpression2d b)
    {
        for(int i=0;i<64;i++)
        {
            if(a.Kind!=b.Kind||a.FirstParameter!=b.FirstParameter||a.LastParameter!=b.LastParameter||a.Reversed!=b.Reversed||a.SignedOffset!=b.SignedOffset)return false;
            if(a.Primitive is {} p)
            {
                if(b.Primitive is not {} q)return false;
                return p.Kind==q.Kind&&p.FirstParameter==q.FirstParameter&&p.LastParameter==q.LastParameter&&p.Reversed==q.Reversed&&p.Degree==q.Degree&&p.Periodic==q.Periodic&&
                    p.MajorRadius==q.MajorRadius&&p.MinorRadius==q.MinorRadius&&p.AxisAngle==q.AxisAngle&&p.Poles.SequenceEqual(q.Poles)&&p.Weights.SequenceEqual(q.Weights)&&
                    p.Knots.SequenceEqual(q.Knots)&&p.Multiplicities.SequenceEqual(q.Multiplicities);
            }
            if(a.Basis is null||b.Basis is null)return false;a=a.Basis;b=b.Basis;
        }
        return false;
    }
}
