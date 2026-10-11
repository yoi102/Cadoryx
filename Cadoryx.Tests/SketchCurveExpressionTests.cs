using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using AuthorKind=Cadoryx.Db.SketchCurveExpressionKind;

namespace Cadoryx.Tests;

public sealed class SketchCurveExpressionTests
{
    public static IEnumerable<object[]> Curves()=>from kind in Enumerable.Range(0,8) from reverse in new[]{false,true} from distance in new[]{.075,-.075} select new object[]{kind,reverse,distance};
    private static SketchBoundaryCurve Boundary(int kind)
    {
        if(kind==0)return new(new(0,0),new(8,4));
        if(kind==1)return new(new(4,0),new(-4,0),new(0,4));
        if(kind is 2 or 3)
        {
            var g=new SketchEllipseGeometry(new(2,-3),new(7,1.5),kind==2?new(-.8,2.6):new(.8,-2.6),.2,1.8);
            return new(g.At(0),g.At(1)){Ellipse=g};
        }
        var freeform=SketchFreeformGeometryTests.Geometry(kind switch{4=>0,5=>2,_=>3});
        if(kind==7)freeform=freeform with{FirstParameter=3.6,LastParameter=4.8};
        return new(freeform.At(0),freeform.At(1)){Freeform=freeform};
    }
    private static void Near(Point2d expected,Point2d actual,double tolerance=1e-9)=>
        Assert.InRange(double.Hypot(expected.X-actual.X,expected.Y-actual.Y),0,tolerance);
    [Theory][MemberData(nameof(Curves))]
    public void OwnedExpressionMatchesNativeDerivativesBrepAndPortableValues(int kind,bool reverse,double distance)
    {
        var original=SketchCurveExpressionGeometry.FromBoundary(Boundary(kind));
        if(reverse)original=original.Reverse();
        var source=original.Offset(distance).Reverse().Offset(-.025).Trim(.1,.9);
        var kernel=new OcctGeometryKernel();Assert.Same(source,kernel.PrepareExpression(source));
        var json=SketchCurveExpressionStorage.EncodeJson(source);var packed=SketchCurveExpressionStorage.EncodeMessagePack(source);
        var copies=new[]{SketchCurveExpressionStorage.DecodeJson(json),SketchCurveExpressionStorage.DecodeMessagePack(packed)};
        Assert.All(copies,copy=>Assert.True(source.SameValues(copy)));
        foreach(double t in new[]{0d,.13,.37,.61,.99,1})
        {
            var actual=source.Evaluate(t);var native=kernel.EvaluateExpression(source,t);
            Near(native.Point,actual.Point);Near(native.First,actual.First,1e-8);Near(native.Second,actual.Second,1e-8);Near(native.Third,actual.Third,1e-8);
            foreach(var copy in copies){var value=copy.Evaluate(t);Near(actual.Point,value.Point);Near(actual.Third,value.Third,1e-8);}
            Near(source.At(1-t),source.Reverse().At(t));
        }
        var assets=new MemoryAssetStore();
        foreach(var placement in new[]{RigidTransform3d.Identity,new RigidTransform3d(new(100,-200,30),Quaterniond.FromEulerDegrees(90,0,0))})
        {
            using(var result=kernel.CreateExpressionEdge(source,placement,assets))
            using(var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets))
            {
                Assert.True(shape.IsValid);Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.EdgeCount);
                Assert.Equal(CurveGeometryType.OffsetCurve,shape.GetEdgeCurveSnapshot().CurveType);
                var x=placement.Rotation.Rotate(new(1,0,0));var y=placement.Rotation.Rotate(new(0,1,0));var o=placement.Translation;
                var plane=new SketchPlane(new(o.X,o.Y,o.Z),new(x.X,x.Y,x.Z),new(y.X,y.Y,y.Z));
                var copied=SketchNativeExpressionSnapshot2d.Read(shape,plane);
                foreach(double t in new[]{0d,.37,1})
                {
                    var point=source.At(t);var world=placement.Apply(new(point.X,point.Y,0));
                    double u=copied.FirstParameter+(copied.LastParameter-copied.FirstParameter)*(copied.Reversed?1-t:t);
                    var actual=shape.EvaluateEdge(u).Point;
                    Assert.InRange(actual.DistanceTo(new(world.X,world.Y,world.Z)),0,1e-9);
                    var native=copied.Evaluate(t).Point;Near(point,new(native.X,native.Y));
                }
                Assert.Null(result.TopologyHistory); // Author node correspondence cannot masquerade as native history.
            }
            Assert.Equal(0,assets.Count);
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ParabolaNormalAndFirstThreeDerivativesMatchIndependentClosedForm(bool reverse)
    {
        var primitive=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(1,1)){BezierControl=new(.5,0)});
        if(reverse)primitive=primitive.Reverse();
        var curve=primitive.Offset(.075).Reverse().Offset(-.025).Trim(.1,.9);
        double d=reverse?-.1:.1,span=curve.ParameterSpan*(curve.Reversed?-1:1);
        foreach(double t in new[]{0d,.13,.37,.61,.99,1})
        {
            double u=curve.FirstParameter+curve.ParameterSpan*(curve.Reversed?1-t:t),q=Math.Sqrt(1+4*u*u);
            var expected=new SketchExpressionEvaluation(new(u+2*d*u/q,u*u-d/q),
                new((1+2*d/Math.Pow(q,3))*span,(2*u+4*d*u/Math.Pow(q,3))*span),
                new(-24*d*u/Math.Pow(q,5)*span*span,(2+4*d*(1-8*u*u)/Math.Pow(q,5))*span*span),
                new(d*(-24+384*u*u)/Math.Pow(q,7)*span*span*span,d*(-144*u+384*u*u*u)/Math.Pow(q,7)*span*span*span));
            var actual=curve.Evaluate(t);Near(expected.Point,actual.Point);Near(expected.First,actual.First);Near(expected.Second,actual.Second);Near(expected.Third,actual.Third);
            var native=new OcctGeometryKernel().EvaluateExpression(curve,t);Near(expected.Third,native.Third,1e-8);
        }
    }
    [Fact]
    public void CircleOffsetRegionHasIndependentAreaAndNormalizedDerivatives()
    {
        var ellipse=new SketchEllipseGeometry(new(2,-3),new(4,0),new(0,4),.2,Math.Tau);
        var curve=SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse}).Offset(.5).Reverse().Offset(.25);
        const double radius=4.25;var kernel=new OcctGeometryKernel();
        var inspection=kernel.InspectExpressionRegion([curve]);Assert.InRange(Math.Abs(inspection.Area-Math.PI*radius*radius),0,1e-8);
        foreach(double t in new[]{0d,.13,.37,.61,1})
        {
            double u=curve.LastParameter-curve.ParameterSpan*t,scale=-curve.ParameterSpan;
            var expected=new SketchExpressionEvaluation(new(2+radius*Math.Cos(u),-3+radius*Math.Sin(u)),
                new(-radius*Math.Sin(u)*scale,radius*Math.Cos(u)*scale),new(-radius*Math.Cos(u)*scale*scale,-radius*Math.Sin(u)*scale*scale),
                new(radius*Math.Sin(u)*scale*scale*scale,-radius*Math.Cos(u)*scale*scale*scale));
            var actual=curve.Evaluate(t);Near(expected.Point,actual.Point);Near(expected.First,actual.First);Near(expected.Second,actual.Second);Near(expected.Third,actual.Third);
        }
    }
    [Theory][InlineData(4)][InlineData(5)]
    public void ContinuationKeepsOriginalRationalBasisAndNativeParameterUnits(int kind)
    {
        var original=SketchCurveExpressionGeometry.FromBoundary(Boundary(kind));
        var curve=original.ContinueNativeRange(-.05,1.05).Offset(.025).Trim(.1,.9).Reverse();var kernel=new OcctGeometryKernel();
        Assert.Same(curve,kernel.PrepareExpression(curve));
        Assert.Same(original,curve.Basis!.Basis);
        foreach(double t in new[]{0d,.13,.37,.61,.99,1})
        {var expected=kernel.EvaluateExpression(curve,t);var actual=curve.Evaluate(t);Near(expected.Point,actual.Point);Near(expected.Third,actual.Third,1e-8);}
    }
    [Fact]
    public void LocalPeriodicPieceDoesNotBypassTheOriginalWholePeriodCertificate()
    {
        var g=new SketchFreeformGeometry(SketchFreeformKind.BSpline,[new(0,0),new(0,0),new(0,0),new(0,0),new(8,0),new(0,6)],[],
            [0,1,2,3,4,5,6],[1,1,1,1,1,1,1],3,true,3.2,3.8);
        g.Validate();var original=SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Freeform=g});
        var curve=original.Offset(.01);var kernel=new OcctGeometryKernel();
        Assert.False(kernel.InspectExpressionRegularity(curve).IsCertified);
        Assert.Throws<CadValidationException>(()=>kernel.PrepareExpression(curve));
    }
    [Fact]
    public void OffsetContinuityRequiresOneAdditionalPrimitiveDerivativeAtEveryStage()
    {
        var g=SketchFreeformGeometryTests.Geometry(2);var primitive=SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Freeform=g});
        double t=(.35-g.FirstParameter)/(g.LastParameter-g.FirstParameter);
        primitive.RequireDerivativeContinuity(t,3);
        var offset=primitive.Offset(.01);offset.RequireDerivativeContinuity(t,2);
        var error=Assert.Throws<CadValidationException>(()=>offset.RequireDerivativeContinuity(t,3));
        Assert.Equal("SlidingContactDerivativeAmbiguous",error.Data["SketchContact.Message"]);
        var second=offset.Offset(.01);Assert.Throws<CadValidationException>(()=>second.RequireDerivativeContinuity(t,2));
    }
    [Fact]
    public void ProjectionFiniteIntersectionsAndOverlapUseExactExpressions()
    {
        var kernel=new OcctGeometryKernel();var curve=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(1,1)){BezierControl=new(.5,0)}).Offset(.1);
        var at=curve.At(.37);var projections=kernel.ProjectExpression(curve,at);Assert.InRange(projections[0].Distance,0,1e-9);Assert.InRange(Math.Abs(projections[0].Parameter-.37),0,1e-9);
        var line=SketchCurveExpressionGeometry.FromBoundary(new(new(.5,-2),new(.5,3)));
        var hit=Assert.Single(kernel.IntersectExpressions(curve,line));Near(curve.At(hit.FirstParameter),line.At(hit.SecondParameter));Assert.False(hit.IsTangent);
        Assert.Throws<CadValidationException>(()=>kernel.IntersectExpressions(curve,curve.Trim(.2,.8)));
    }
    [Theory][InlineData("kind")][InlineData("basis")][InlineData("interval")][InlineData("primitive")][InlineData("distance")][InlineData("schema")][InlineData("extra")]
    public void PortableDecoderRejectsMalformedAuthorGraphsAtomically(string change)
    {
        var original=SketchCurveExpressionGeometry.FromBoundary(Boundary(4)).Offset(.075);
        var json=JsonNode.Parse(SketchCurveExpressionStorage.EncodeJson(original))!;var node=json["nodes"]![1]!;
        switch(change)
        {
            case "kind":node["kind"]=99;break;case "basis":node["basisIndex"]=1;break;
            case "interval":node["lastParameter"]=node["firstParameter"]!.DeepClone();break;
            case "primitive":json["nodes"]![0]!["primitive"]=null;break;case "distance":node["signedOffset"]="NaN";break;
            case "schema":json["schemaVersion"]=2;break;default:node["nativePointer"]=123;break;
        }
        Assert.Throws<InvalidDataException>(()=>SketchCurveExpressionStorage.DecodeJson(JsonSerializer.SerializeToUtf8Bytes(json)));
        Assert.True(original.SameValues(SketchCurveExpressionStorage.DecodeMessagePack(SketchCurveExpressionStorage.EncodeMessagePack(original))));
    }
    [Fact]
    public void MessagePackRejectsForwardFieldsAndTrailingBytesBeforePublishingValues()
    {
        var expression=SketchCurveExpressionGeometry.FromBoundary(Boundary(4)).Offset(.1);
        var bytes=SketchCurveExpressionStorage.EncodeMessagePack(expression);
        Assert.Throws<InvalidDataException>(()=>SketchCurveExpressionStorage.DecodeMessagePack(bytes.Concat(new byte[]{0xc0}).ToArray()));
        var reader=new MessagePackReader(bytes);reader.ReadArrayHeader();int version=reader.ReadInt32(),count=reader.ReadArrayHeader();
        var buffer=new ArrayBufferWriter<byte>();var writer=new MessagePackWriter(buffer);writer.WriteArrayHeader(2);writer.Write(version);writer.WriteArrayHeader(count);
        for(int i=0;i<count;i++){Assert.Equal(7,reader.ReadArrayHeader());writer.WriteArrayHeader(8);for(int field=0;field<7;field++)writer.WriteRaw(reader.ReadRaw());writer.WriteNil();}
        writer.Flush();Assert.Throws<InvalidDataException>(()=>SketchCurveExpressionStorage.DecodeMessagePack(buffer.WrittenMemory));
    }
    [Fact]
    public void All64OwnedNodesSurviveFlatStorageAndThe65thNodeIsRejected()
    {
        var curve=SketchCurveExpressionGeometry.FromBoundary(Boundary(4));
        for(int i=1;i<SketchCurveExpressionGeometry.MaximumDepth;i++)curve=curve.Offset(.0001);
        Assert.True(curve.SameValues(SketchCurveExpressionStorage.DecodeJson(SketchCurveExpressionStorage.EncodeJson(curve))));
        Assert.True(curve.SameValues(SketchCurveExpressionStorage.DecodeMessagePack(SketchCurveExpressionStorage.EncodeMessagePack(curve))));
        Assert.Throws<CadValidationException>(()=>curve.Offset(.0001));
    }
    [Fact]
    public void AFinalDistanceCancellationCannotHideAnIntermediateFoldOrPublishAnAsset()
    {
        var g=new SketchEllipseGeometry(new(0,0),new(4,0),new(0,4),0,Math.Tau);
        var primitive=SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Ellipse=g});
        var folded=primitive.Offset(-5).Offset(5);var before=SketchCurveExpressionStorage.EncodeMessagePack(folded);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        var check=kernel.InspectExpressionRegularity(folded);Assert.Equal(ExactExpressionRegularityState.Folded,check.State);Assert.Equal(1,check.ExpressionNodeIndex);
        Assert.Throws<CadValidationException>(()=>kernel.CreateExpressionEdge(folded,RigidTransform3d.Identity,assets));
        Assert.Equal(0,assets.Count);Assert.Equal(before,SketchCurveExpressionStorage.EncodeMessagePack(folded));
    }
    [Fact]
    public void CuspNormalFailsAndStorageNeverTreatsAValueAsAGeometricCertificate()
    {
        var g=new SketchFreeformGeometry(SketchFreeformKind.Bezier,[new(0,0),new(0,0),new(.5,.3),new(1,0)],[],[],[],3,false,0,1);
        var primitive=SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Freeform=g});
        var cusp=primitive.Offset(.1);var restored=SketchCurveExpressionStorage.DecodeJson(SketchCurveExpressionStorage.EncodeJson(cusp));
        Assert.True(cusp.SameValues(restored));Assert.Throws<CadValidationException>(()=>restored.Evaluate(0));
        Assert.False(new OcctGeometryKernel().InspectExpressionRegularity(restored).IsCertified);
    }
}
