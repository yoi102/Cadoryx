using Cadoryx.Db;
using System.Collections.Immutable;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:ISketchEdgeProjector
{
    public async Task<ExactSketchEdgeProjection> ProjectEdgeAsync(DocumentSnapshot document,DefinitionId part,ExactTopologySelection selection,
        RigidTransform3d targetPlane,IAssetStore assets,CancellationToken token=default)
    {
        selection.Validate();targetPlane.Validate();
        if(selection.Kind!=HistoryShapeKind.Edge||selection.DocumentId!=document.Id||
           !document.Definitions.TryGetValue(part,out var owner)||owner is not PartDefinition||
           !document.Features.TryGetValue(selection.FeatureId,out var feature)||feature.PartId!=part||feature.IsStale||feature.IsSuppressed||
           feature.Result.AssetId!=selection.Asset||feature.Result.Revision!=selection.Revision)
            throw new CadValidationException("Select a current native edge in the sketch's owning part.");
        using var hold=assets.Acquire(feature.Result.AssetId);
        return await Run(()=>CopyEdgeProjection(part,selection,feature.Result,targetPlane,assets,token),token).ConfigureAwait(false);
    }
    public async Task ConfirmEdgeProjectionAsync(ExactSketchEdgeProjection evidence,IAssetStore assets,CancellationToken token=default)
    {
        evidence.Validate();using var hold=assets.Acquire(evidence.Source.AssetId);
        await Run(()=>
        {
            var actual=CopyEdgeProjection(evidence.Part,evidence.Selection,evidence.Source,evidence.TargetPlane,assets,token);
            static bool Near(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y)<=1e-8;
            bool same=Near(actual.Curve.Start,evidence.Curve.Start)&&Near(actual.Curve.End,evidence.Curve.End);
            if(actual.Curve.Freeform is {} freeform)
                same&=SketchFreeformGeometry.Same(freeform,evidence.Curve.Freeform)&&evidence.Curve.Ellipse is null&&
                    evidence.Curve.Arc is null&&evidence.Curve.Spline is null&&evidence.Curve.Bezier is null;
            else if(actual.Curve.Ellipse is {} a&&evidence.Curve.Ellipse is {} b)
                same&=Near(a.Center,b.Center)&&Near(a.Cosine,b.Cosine)&&Near(a.Sine,b.Sine)&&
                    Math.Abs(a.StartAngle-b.StartAngle)<=1e-10&&Math.Abs(a.SweepAngle-b.SweepAngle)<=1e-10&&evidence.Curve.Freeform is null;
            else same&=actual.Curve.Ellipse is null&&evidence.Curve.Ellipse is null&&evidence.Curve.Arc is null&&
                evidence.Curve.Spline is null&&evidence.Curve.Bezier is null&&evidence.Curve.Freeform is null;
            if(!same)throw new CadValidationException("Stored projected curve differs from its exact native edge.");
            return true;
        },token).ConfigureAwait(false);
    }
    private static ExactSketchEdgeProjection CopyEdgeProjection(DefinitionId part,ExactTopologySelection selection,
        GeometryAssetRef source,RigidTransform3d plane,IAssetStore assets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(selection.AdapterVersion!=HistoryAdapterVersion)throw new CadValidationException("Unsupported edge projection adapter.");
        using var shape=OcctGeometryBridge.ReadShape(source,assets);using var map=RepairSnapshot.Create(shape);
        if(map.Fingerprint!=selection.Fingerprint||selection.FullTopologyIndex>=map.Topology.Count||
           map.Topology[selection.FullTopologyIndex].Kind!=ShapeKind.Edge)
            throw new CadValidationException("Exact edge selection no longer matches its BRep.");
        using var edge=map.CopySubshape(map.Select(selection.FullTopologyIndex));var info=edge.GetEdgeCurveSnapshot();
        double start=info.FirstParameter,sweep=info.LastParameter-start;
        if(!double.IsFinite(start)||!double.IsFinite(sweep)||sweep<=1e-12)
            throw new CadValidationException("Native edge has an invalid or unbounded interval.");
        var inverse=plane.Inverse();
        Point2d Point(GpPoint p){var value=inverse.Apply(new(p.X,p.Y,p.Z));return new(value.X,value.Y);}
        ExactSketchCurve curve;
        if(info.CurveType==CurveGeometryType.Line)curve=new(Point(info.StartPoint),Point(info.EndPoint));
        else if(info.CurveType is CurveGeometryType.Circle or CurveGeometryType.Ellipse)
        {
            if(sweep>Math.Tau+1e-10)throw new CadValidationException("Native conic edge exceeds one revolution.");
            // These are exact coefficients of a proven analytical conic, not a fit to sampled points.
            var value=edge.EvaluateEdgeDerivatives(start);var center=new Vector3d(value.Point.X+value.SecondDerivative.X,
                value.Point.Y+value.SecondDerivative.Y,value.Point.Z+value.SecondDerivative.Z);
            var radial=new Vector3d(-value.SecondDerivative.X,-value.SecondDerivative.Y,-value.SecondDerivative.Z);
            var derivative=new Vector3d(value.FirstDerivative.X,value.FirstDerivative.Y,value.FirstDerivative.Z);
            var cosine=inverse.Rotation.Rotate(radial*Math.Cos(start)-derivative*Math.Sin(start));
            var sine=inverse.Rotation.Rotate(radial*Math.Sin(start)+derivative*Math.Cos(start));var projected=inverse.Apply(center);
            var ellipse=new SketchEllipseGeometry(new(projected.X,projected.Y),new(cosine.X,cosine.Y),new(sine.X,sine.Y),start,sweep);
            curve=new(ellipse.At(0),ellipse.At(1),Ellipse:ellipse);
        }
        else if(info.CurveType is CurveGeometryType.BezierCurve or CurveGeometryType.BSplineCurve)
        {
            var definition=FreeformAuthoring.GetCurveDefinition(edge);
            var interval=definition.ParameterRange??throw new CadValidationException("Missing copied native curve interval.");
            var freeform=new SketchFreeformGeometry((SketchFreeformKind)definition.Kind,definition.Poles.Select(Point).ToImmutableArray(),
                definition.Weights.ToImmutableArray(),definition.Knots.ToImmutableArray(),definition.Multiplicities.ToImmutableArray(),
                definition.Degree,definition.Periodic,interval.First,interval.Last);
            curve=new(freeform.At(0),freeform.At(1),Freeform:freeform);
        }
        else throw new CadValidationException("Unsupported exact curve family; unknown native edges are not sampled.");
        var result=new ExactSketchEdgeProjection(part,selection,source,plane,curve);result.Validate();return result;
    }
}
