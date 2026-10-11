using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

/// <summary>Shared exact profile faces for modeling and interactive construction previews.</summary>
public static class OcctSketchProfileGeometry
{
    private static Shape Place(Shape source,RigidTransform3d placement)
    {
        using(source)using(var transform=OcctGeometryBridge.ToNative(placement))return source.Transformed(transform);
    }
    public static Shape CreateFace(SketchProfile profile,bool radial)=>CreateFace(profile,radial,false);
    internal static Shape CreateFace(SketchProfile profile,bool radial,bool canonicalExpressions)
    {
        if(radial)return Place(CreateFace(profile,false,canonicalExpressions),new(new(0,0,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)));
        if(!profile.BoundaryCurves.IsEmpty)
        {
            if(profile.BoundaryCurves.Any(c=>c.Expression is not null))return OcctGeometryKernel.ExpressionFace(profile.BoundaryCurves,canonicalExpressions);
            var curves=profile.BoundaryCurves.Select(edge=>
            {
                if(edge.Freeform is {} freeform)return OcctFreeformSketchGeometry.Create(freeform);
                if(edge.Ellipse is {} ellipse)
                {
                    var p=ellipse.Principal();return SketchCurve2d.EllipticArc(new(ellipse.Center.X,ellipse.Center.Y),
                        p.MajorRadius,p.MinorRadius,p.StartAngle,p.SweepAngle,p.AxisAngle);
                }
                if(edge.BezierControl is {} control)
                    return SketchCurve2d.Bezier([new(edge.Start.X,edge.Start.Y),
                        new(control.X,control.Y),new(edge.End.X,edge.End.Y)]);
                if(!edge.SplineControls.IsDefaultOrEmpty)
                {
                    var definition=edge.SplineDefinition;definition.Validate();
                    return SketchCurve2d.BSpline(edge.SplineControls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),
                        definition.Knots,definition.Multiplicities,SketchSplineGeometry.Degree);
                }
                if(edge.Middle is not {} middle)
                    return SketchCurve2d.Segment(new(edge.Start.X,edge.Start.Y),new(edge.End.X,edge.End.Y));
                var arc=SketchArcGeometry.Through(edge.Start,middle,edge.End);
                return SketchCurve2d.CircularArc(new(arc.Center.X,arc.Center.Y),arc.Radius,arc.StartAngle,arc.SweepAngle);
            }).ToArray();
            var chain=SketchCurveChain2d.Create(curves,requireClosed:true);
            if(chain.Curves.Count!=curves.Length||!chain.Curves.Select((c,i)=>ReferenceEquals(c,curves[i])).All(same=>same))
                throw new CadValidationException("Exact region preparation changed the authored order or direction.");
            return SketchProfile2d.Create(chain).CreateFace(SketchPlane.XY);
        }
        if(profile.Arc is {} segment)
        {
            var geometry=SketchArcGeometry.Through(segment.Start,segment.Middle,segment.End);
            var arc=SketchCurve2d.CircularArc(new(geometry.Center.X,geometry.Center.Y),geometry.Radius,
                geometry.StartAngle,geometry.SweepAngle);
            var chord=SketchCurve2d.Segment(new(segment.End.X,segment.End.Y),new(segment.Start.X,segment.Start.Y));
            var chain=SketchCurveChain2d.Create([arc,chord],requireClosed:true);
            return SketchProfile2d.Create(chain).CreateFace(SketchPlane.XY);
        }
        if(profile.Bezier is {} bezier)
        {
            var curve=SketchCurve2d.Bezier([new(bezier.Start.X,bezier.Start.Y),
                new(bezier.Control.X,bezier.Control.Y),new(bezier.End.X,bezier.End.Y)]);
            var chord=SketchCurve2d.Segment(new(bezier.End.X,bezier.End.Y),new(bezier.Start.X,bezier.Start.Y));
            return SketchProfile2d.Create(SketchCurveChain2d.Create([curve,chord],requireClosed:true)).CreateFace(SketchPlane.XY);
        }
        if(profile.Spline is {} spline)
        {
            var controls=spline.Controls;
            var definition=spline.Definition;definition.Validate();
            var curve=SketchCurve2d.BSpline(controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),
                definition.Knots,definition.Multiplicities,
                SketchSplineGeometry.Degree);
            if(controls[0]==controls[^1])
                return SketchProfile2d.Create(SketchCurveChain2d.Create([curve],true)).CreateFace(SketchPlane.XY);
            var chord=SketchCurve2d.Segment(new(controls[^1].X,controls[^1].Y),new(controls[0].X,controls[0].Y));
            return SketchProfile2d.Create(SketchCurveChain2d.Create([curve,chord],requireClosed:true)).CreateFace(SketchPlane.XY);
        }
        if(profile.Circle is {} circle)
        {
            using var edge=ShapeFactory.CreateCircleEdge(new GpPoint(circle.Center.X,circle.Center.Y,0),new GpPoint(0,0,1),circle.Radius);
            using var circleWire=ShapeFactory.CreateWire([edge]);
            return ShapeFactory.CreatePlanarFace(circleWire);
        }
        using var wire=ShapeFactory.CreatePolygonWire(profile.Points.Select(p=>new GpPoint(p.X,p.Y,0)).ToArray(),true);
        return ShapeFactory.CreatePlanarFace(wire);
    }
}
