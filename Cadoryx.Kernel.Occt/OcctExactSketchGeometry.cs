using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IExactSketchGeometry
{
    private static SketchCurve2d NativeExactCurve(ExactSketchCurve curve)
    {
        curve.Validate();
        if(curve.Expression is not null)throw new CadValidationException("An exact expression must use its expression consumer.");
        if(curve.Freeform is {} freeform)return OcctFreeformSketchGeometry.Create(freeform);
        if(curve.Ellipse is {} ellipse)
        {
            var p=ellipse.Principal();return SketchCurve2d.EllipticArc(new(ellipse.Center.X,ellipse.Center.Y),
                p.MajorRadius,p.MinorRadius,p.StartAngle,p.SweepAngle,p.AxisAngle);
        }
        if(curve.Spline is {} spline)return SketchCurve2d.BSpline(
            spline.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),spline.Knots,spline.Multiplicities,3);
        if(curve.Bezier is {} bezier)return SketchCurve2d.Bezier([new(bezier.Start.X,bezier.Start.Y),
            new(bezier.Control.X,bezier.Control.Y),new(bezier.End.X,bezier.End.Y)]);
        if(curve.Arc is {} arc)return SketchCurve2d.CircularArc(new(arc.Center.X,arc.Center.Y),arc.Radius,arc.StartAngle,arc.SweepAngle);
        return SketchCurve2d.Segment(new(curve.Start.X,curve.Start.Y),new(curve.End.X,curve.End.Y));
    }
    private static ExactSketchEvaluation CopiedEvaluation(SketchCurve2d curve,double parameter)
    {
        if(!double.IsFinite(parameter)||parameter<0||parameter>1)throw new CadValidationException("Curve parameter must lie in [0,1].");
        var value=curve.Evaluate(parameter);double domain=curve.LastParameter-curve.FirstParameter;
        var derivative=new Point2d(value.FirstDerivative.X*domain,value.FirstDerivative.Y*domain);
        CadGuard.Finite(value.Point.X,value.Point.Y,derivative.X,derivative.Y);
        return new(new(value.Point.X,value.Point.Y),derivative);
    }
    public ExactSketchEvaluation Evaluate(ExactSketchCurve curve,double parameter)=>ExactGuard(()=>
    {
        curve.Validate();if(curve.Expression is {} expression){var value=EvaluateExpression(expression,parameter);return new ExactSketchEvaluation(value.Point,value.First);}
        return CopiedEvaluation(NativeExactCurve(curve),parameter);
    });
    public ImmutableArray<ExactSketchProjection> Project(ExactSketchCurve curve,Point2d point)=>ExactGuard(()=>
    {
        CadGuard.Finite(point.X,point.Y);
        curve.Validate();if(curve.Expression is {} expression)return ProjectExpression(expression,point);
        var native=NativeExactCurve(curve);
        var candidates=native.Project(new(point.X,point.Y)).Select(p=>
            new ExactSketchProjection(new(p.Point.X,p.Point.Y),p.NormalizedParameter,p.Distance)).ToList();
        foreach(double parameter in new[]{0d,1d})
        {
            var at=CopiedEvaluation(native,parameter).Point;
            if(!candidates.Any(p=>Math.Abs(p.Parameter-parameter)<=1e-12))
                candidates.Add(new(at,parameter,double.Hypot(at.X-point.X,at.Y-point.Y)));
        }
        foreach(var p in candidates)CadGuard.Finite(p.Point.X,p.Point.Y,p.Parameter,p.Distance);
        return candidates.OrderBy(p=>p.Distance).ThenBy(p=>p.Parameter).ToImmutableArray();
    });
    public ImmutableArray<ExactSketchIntersection> Intersect(ExactSketchCurve first,ExactSketchCurve second)=>ExactGuard(()=>
    {
        first.Validate();second.Validate();
        if(first.Expression is not null||second.Expression is not null)return IntersectExpressions(OwnedExpression(first),OwnedExpression(second));
        var a=NativeExactCurve(first);var b=NativeExactCurve(second);
        var inspection=SketchModeling.InspectIntersections(a,b);
        if(inspection.HasOverlappingIntervals)
            throw SketchCurveFailure.Create("CurveOverlappingIntervals","Coincident curve intervals do not identify a unique crossing. Select distinct finite boundaries.");
        return inspection.Endpoints.Select(hit=>
        {
            var da=CopiedEvaluation(a,hit.FirstNormalizedParameter).NormalizedDerivative;
            var db=CopiedEvaluation(b,hit.SecondNormalizedParameter).NormalizedDerivative;
            double size=double.Hypot(da.X,da.Y)*double.Hypot(db.X,db.Y);
            bool tangent=size<=1e-20||Math.Abs(da.X*db.Y-da.Y*db.X)<=1e-9*size;
            return new ExactSketchIntersection(new(hit.Point.X,hit.Point.Y),hit.FirstNormalizedParameter,hit.SecondNormalizedParameter,tangent);
        }).ToImmutableArray();
    });
    public ExactRegionInspection InspectRegion(ImmutableArray<ExactSketchCurve> directedBoundary)=>ExactGuard(()=>
    {
        if(directedBoundary.IsDefault||directedBoundary.Length is <1 or >256)
            throw new CadValidationException("A region needs 1 to 256 explicitly ordered finite edges.");
        for(int i=0;i<directedBoundary.Length;i++)
        {
            var a=directedBoundary[i].End;var b=directedBoundary[(i+1)%directedBoundary.Length].Start;
            if(double.Hypot(a.X-b.X,a.Y-b.Y)>1e-7)throw new CadValidationException("Exact region boundaries must form the authored closed chain.");
        }
        if(directedBoundary.Any(c=>c.Expression is not null))return InspectExpressionRegion(directedBoundary.Select(OwnedExpression).ToImmutableArray());
        var curves=directedBoundary.Select(NativeExactCurve).ToArray();
        var chain=SketchCurveChain2d.Create(curves,true);
        if(chain.Curves.Count!=curves.Length||!chain.Curves.Select((curve,i)=>ReferenceEquals(curve,curves[i])).All(same=>same))
            throw new CadValidationException("Native preparation tried to reorder or reverse an authored boundary.");
        using var face=SketchProfile2d.Create(chain).CreateFace(SketchPlane.XY);
        if(!face.GetValidationReport(true,true).IsValid||!face.GetSelfInterferenceReport().IsClear)
            throw new CadValidationException("The exact region has invalid geometry or self-interference.");
        double area=Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass);
        if(!double.IsFinite(area)||area<=1e-12)throw new CadValidationException("The exact region has no positive area.");
        return new ExactRegionInspection(area,face.GetTopologySummary().UniqueCounts.EdgeCount);
    });
    private static T ExactGuard<T>(Func<T> operation)
    {
        try{return operation();}
        catch(CadValidationException){throw;}
        catch(Exception error) when(error is OcctException or ArgumentException or InvalidOperationException)
        {throw new CadValidationException("Exact curve operation rejected: "+error.Message);}
    }
}
