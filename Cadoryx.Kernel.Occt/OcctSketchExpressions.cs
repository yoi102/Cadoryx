using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using AuthorKind=Cadoryx.Db.SketchCurveExpressionKind;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IExactSketchExpressionGeometry
{
    private sealed record NativeExpression(SketchCurveExpression2d Curve,double ParameterSign,double ParameterShift,
        double AuthorFirst,double AuthorLast)
    {
        internal double Map(double parameter)=>parameter==AuthorFirst?(ParameterSign>0?Curve.FirstParameter:Curve.LastParameter):
            parameter==AuthorLast?(ParameterSign>0?Curve.LastParameter:Curve.FirstParameter):ParameterSign*parameter+ParameterShift;
    }
    private static NativeExpression NativeExpressionCurve(SketchCurveExpressionGeometry expression)
    {
        expression.Validate();return Build(expression);
        static NativeExpression Build(SketchCurveExpressionGeometry node)
        {
            SketchCurveExpression2d native;double sign,shift;double first,last;
            if(node.Primitive is {} primitive)
            {
                var curve=NativeExactCurve(ExactSketchCurve.FromBoundary(primitive));
                native=SketchCurveExpression2d.FromCurve(curve);sign=1;shift=0;
                if(primitive.Ellipse is {} ellipse)
                {
                    sign=Math.Sign(ellipse.Cosine.X*ellipse.Sine.Y-ellipse.Cosine.Y*ellipse.Sine.X);
                    shift=(ellipse with{StartAngle=0}).Principal().StartAngle;
                }
                // Exact full author endpoints use the factory's corresponding bounds. This
                // avoids re-evaluating a reflected conic endpoint with a different sum order.
                double authorFirst,authorLast;
                if(primitive.Freeform is {} freeform){authorFirst=freeform.FirstParameter;authorLast=freeform.LastParameter;}
                else if(primitive.Ellipse is {} conic){authorFirst=Math.Min(conic.StartAngle,conic.StartAngle+conic.SweepAngle);authorLast=Math.Max(conic.StartAngle,conic.StartAngle+conic.SweepAngle);}
                else if(primitive.Middle is {} middle){var arc=SketchArcGeometry.Through(primitive.Start,middle,primitive.End);authorFirst=Math.Min(arc.StartAngle,arc.StartAngle+arc.SweepAngle);authorLast=Math.Max(arc.StartAngle,arc.StartAngle+arc.SweepAngle);}
                else if(primitive.BezierControl is null&&primitive.SplineControls.IsDefaultOrEmpty)
                {
                    // The author uses Hypot for physical line length; the native segment
                    // factory may round that length differently. Bind its exact endpoints
                    // to the author endpoints, as for conics, without clamping a trim.
                    authorFirst=0;authorLast=double.Hypot(primitive.End.X-primitive.Start.X,primitive.End.Y-primitive.Start.Y);
                }
                else {authorFirst=curve.FirstParameter;authorLast=curve.LastParameter;}
                var mapping=new NativeExpression(native,sign,shift,authorFirst,authorLast);
                first=mapping.Map(node.FirstParameter);last=mapping.Map(node.LastParameter);
            }
            else
            {
                var basis=Build(node.Basis!);sign=basis.ParameterSign;shift=basis.ParameterShift;
                first=basis.Map(node.FirstParameter);last=basis.Map(node.LastParameter);
                native=node.Kind==AuthorKind.Offset?basis.Curve.Offset(node.SignedOffset):
                    basis.Curve.ContinueNativeRange(Math.Min(Math.Min(first,last),basis.Curve.FirstParameter),Math.Max(Math.Max(first,last),basis.Curve.LastParameter));
            }
            double lower=Math.Min(first,last),upper=Math.Max(first,last);
            bool reversed=node.Reversed^(sign<0);
            if(lower!=native.FirstParameter||upper!=native.LastParameter)
            {
                // Put the retained exact endpoint at normalized zero before
                // trimming. SDK19's opposite endpoint arithmetic can otherwise
                // round outside an offset's strict finite basis. This changes
                // traversal during preparation only; the authored bounds stay exact.
                bool trimReversed=upper==native.LastParameter&&lower!=native.FirstParameter;
                if(native.Reversed!=trimReversed)native=native.Reverse();
                double width=native.LastParameter-native.FirstParameter;
                double a=native.Reversed?(native.LastParameter-upper)/width:(lower-native.FirstParameter)/width;
                double b=native.Reversed?(native.LastParameter-lower)/width:(upper-native.FirstParameter)/width;
                native=native.Trim(a,b);
            }
            if(native.Reversed!=reversed)native=native.Reverse();
            return new(native,sign,shift,node.FirstParameter,node.LastParameter);
        }
    }
    public ExactExpressionRegularity InspectExpressionRegularity(SketchCurveExpressionGeometry expression)=>ExactGuard(()=>
    {
        expression.Validate();
        var authors=new List<SketchCurveExpressionGeometry>();for(var node=expression;node is not null;node=node.Basis)authors.Add(node);authors.Reverse();
        if(authors[0].Primitive?.Freeform is {Periodic:true} periodic)
        {
            // The finite native edge may use exact periodic segmentation. Certify on the
            // complete ORIGINAL author period, retaining SDK19's conservative scope and
            // reporting original author node indices even when continuation nodes are skipped.
            var primitive=SketchCurve2d.BSpline(periodic.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),periodic.Knots,
                periodic.Multiplicities,periodic.Degree,true,periodic.Weights.IsEmpty?null:periodic.Weights);
            if(authors[0].Reversed)primitive=primitive.Reverse();
            var prototype=SketchCurveExpression2d.FromCurve(primitive);var indices=new List<int>{0};
            for(int i=1;i<authors.Count;i++)
            {
                if(authors[i].Kind==AuthorKind.Offset){prototype=prototype.Offset(authors[i].SignedOffset);indices.Add(i);}
                if(prototype.Reversed!=authors[i].Reversed)prototype=prototype.Reverse();
            }
            var original=SketchCurveRegularity2d.Inspect(prototype);
            return new ExactExpressionRegularity((ExactExpressionRegularityState)original.State,original.FirstParameter,original.LastParameter,
                original.ExpressionNodeIndex<0?original.ExpressionNodeIndex:indices[original.ExpressionNodeIndex],original.IntervalsChecked,original.MinimumTangentialScale,original.Diagnostic);
        }
        var native=NativeExpressionCurve(expression);var result=SketchCurveRegularity2d.Inspect(native.Curve);
        double a=(result.FirstParameter-native.ParameterShift)/native.ParameterSign;
        double b=(result.LastParameter-native.ParameterShift)/native.ParameterSign;
        return new ExactExpressionRegularity((ExactExpressionRegularityState)result.State,Math.Min(a,b),Math.Max(a,b),
            result.ExpressionNodeIndex,result.IntervalsChecked,result.MinimumTangentialScale,result.Diagnostic);
    });
    public SketchCurveExpressionGeometry PrepareExpression(SketchCurveExpressionGeometry expression)
    {
        var inspection=InspectExpressionRegularity(expression);
        if(!inspection.IsCertified)
        {
            var error=new CadValidationException("Exact expression regularity was not certified: "+inspection.Diagnostic);
            error.Data["Expression.NodeIndex"]=inspection.ExpressionNodeIndex;
            error.Data["Expression.FirstParameter"]=inspection.FirstParameter;error.Data["Expression.LastParameter"]=inspection.LastParameter;
            error.Data["Expression.Regularity"]=inspection.State.ToString();throw error;
        }
        return expression;
    }
    public SketchExpressionEvaluation EvaluateExpression(SketchCurveExpressionGeometry expression,double parameter)=>ExactGuard(()=>
    {
        var native=NativeExpressionCurve(expression).Curve;var value=native.Evaluate(parameter);double span=expression.ParameterSpan;
        Point2d Derivative(int order)
        {
            var d=native.Derivative(parameter,order);double factor=Math.Pow(span,order);
            var result=new Point2d(d.X*factor,d.Y*factor);CadGuard.Finite(result.X,result.Y);return result;
        }
        return new SketchExpressionEvaluation(new(value.Point.X,value.Point.Y),Derivative(1),Derivative(2),Derivative(3));
    });
    public ImmutableArray<ExactSketchProjection> ProjectExpression(SketchCurveExpressionGeometry expression,Point2d point)=>ExactGuard(()=>
    {
        CadGuard.Finite(point.X,point.Y);var native=NativeExpressionCurve(expression).Curve;
        var candidates=native.Project(new(point.X,point.Y)).Select(p=>new ExactSketchProjection(new(p.Point.X,p.Point.Y),p.NormalizedParameter,p.Distance)).ToList();
        foreach(double t in new[]{0d,1d})
        {
            var value=native.Evaluate(t).Point;
            if(!candidates.Any(p=>Math.Abs(p.Parameter-t)<=1e-12))candidates.Add(new(new(value.X,value.Y),t,value.DistanceTo(new(point.X,point.Y))));
        }
        return candidates.OrderBy(p=>p.Distance).ThenBy(p=>p.Parameter).ToImmutableArray();
    });
    public ImmutableArray<ExactSketchIntersection> IntersectExpressions(SketchCurveExpressionGeometry first,SketchCurveExpressionGeometry second)=>ExactGuard(()=>
    {
        var a=NativeExpressionCurve(first).Curve;var b=NativeExpressionCurve(second).Curve;var hits=a.InspectIntersections(b);
        if(hits.HasOverlappingIntervals)throw SketchCurveFailure.Create("CurveOverlappingIntervals","Coincident expression intervals do not identify a unique crossing.");
        return hits.Endpoints.Select(hit=>
        {
            var da=a.Derivative(hit.FirstNormalizedParameter,1);var db=b.Derivative(hit.SecondNormalizedParameter,1);
            double size=da.Magnitude*db.Magnitude;
            bool tangent=size<=1e-20||Math.Abs(da.X*db.Y-da.Y*db.X)<=1e-9*size;
            return new ExactSketchIntersection(new(hit.Point.X,hit.Point.Y),hit.FirstNormalizedParameter,hit.SecondNormalizedParameter,tangent);
        }).ToImmutableArray();
    });
    public ExactRegionInspection InspectExpressionRegion(ImmutableArray<SketchCurveExpressionGeometry> directedBoundary)=>ExactGuard(()=>
    {
        if(directedBoundary.IsDefault||directedBoundary.Length is <1 or >256)throw new CadValidationException("An exact region needs 1 to 256 ordered finite expressions.");
        var expressions=directedBoundary.Select(c=>NativeExpressionCurve(PrepareExpression(c)).Curve).ToArray();
        var chain=SketchExpressionChain2d.CreateOrdered(expressions,true);
        using var face=SketchExpressionProfile2d.Create(chain).CreateFace(SketchPlane.XY);
        if(!face.GetValidationReport(true,true).IsValid||!face.GetSelfInterferenceReport().IsClear)
            throw new CadValidationException("The exact expression region has invalid geometry or self-interference.");
        double area=Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass);
        if(!double.IsFinite(area)||area<=1e-12)throw new CadValidationException("The exact expression region has no positive area.");
        return new ExactRegionInspection(area,face.GetTopologySummary().UniqueCounts.EdgeCount);
    });
    public GeometryResult CreateExpressionEdge(SketchCurveExpressionGeometry expression,RigidTransform3d plane,IAssetStore assets)=>ExactGuard(()=>
    {
        plane.Validate();var x=plane.Rotation.Rotate(new(1,0,0));var y=plane.Rotation.Rotate(new(0,1,0));var p=plane.Translation;
        var nativePlane=new SketchPlane(new(p.X,p.Y,p.Z),new(x.X,x.Y,x.Z),new(y.X,y.Y,y.Z));
        using var edge=NativeExpressionCurve(PrepareExpression(expression)).Curve.ToEdge(nativePlane);
        return OcctGeometryBridge.StoreShape(edge,assets);
    });
}
