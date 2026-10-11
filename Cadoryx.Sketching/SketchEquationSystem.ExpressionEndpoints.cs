using Cadoryx.Db;
using Scalar=Cadoryx.Sketching.SketchContactDifferentials.Scalar;
using Vector=Cadoryx.Sketching.SketchContactDifferentials.Vector;
using Geometry=Cadoryx.Sketching.SketchContactDifferentials.Geometry;

namespace Cadoryx.Sketching;

internal sealed partial class SketchEquationSystem
{
    private Scalar[] StructuralResiduals(double[] x,SketchExpressionEndpointEquation e)
    {
        var geometry=SketchExpressionDifferentials.EvaluateRegionContact(EvaluationSketch(x),e.Curve,Scalar.Constant(e.Parameter,x.Length),
            x,pointIndices,radiusIndices,origin,scale,1,false,regionChart);int i=pointIndices[e.Point];double tolerance=options.LinearToleranceMm/scale;
        return[(Scalar.Variable(x[i],i,x.Length)-geometry.Position.X)/tolerance,
            (Scalar.Variable(x[i+1],i+1,x.Length)-geometry.Position.Y)/tolerance];
    }
    private Geometry EndpointScalarGeometry(double[] x,SketchEntityId curve,SketchEntityId joint,bool curvature)
    {
        Scalar C(double value)=>Scalar.Constant(value,x.Length);
        if(sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==curve) is {} expression)
        {
            var e=expression.Endpoints!;double parameter=joint==e.Start?0:joint==e.End?1:throw new CadValidationException("Unknown expression endpoint.");
            var value=SketchExpressionDifferentials.EvaluateRegionContact(EvaluationSketch(x),curve,C(parameter),x,pointIndices,radiusIndices,origin,scale,curvature?2:1,curvature,regionChart);
            double sign=parameter==0?1:-1;return value with{Unit=value.Unit*sign,Curvature=value.Curvature*sign};
        }
        var old=EndpointTangent(x,curve,joint);var gx=new double[x.Length];var gy=new double[x.Length];
        foreach(var d in old.Gradients)
        {int i=pointIndices[d.Id];gx[i]+=d.Dx.X;gx[i+1]+=d.Dy.X;gy[i]+=d.Dx.Y;gy[i+1]+=d.Dy.Y;}
        var k=C(0);
        if(curvature)
        {
            var value=EndpointCurvature(x,curve,joint);var g=new double[x.Length];
            foreach(var d in value.Gradients){int i=pointIndices[d.Id];g[i]+=d.Gradient.X;g[i+1]+=d.Gradient.Y;}k=new(value.Value,g);
        }
        return new(new(C(0),C(0)),new(new(old.Unit.X,gx),new(old.Unit.Y,gy)),k,C(1));
    }
    private Scalar[] ExpressionEndpointContactResiduals(double[] x,SketchEntityId a,SketchEntityId b,SketchEntityId joint,bool curvature)
    {
        var first=EndpointScalarGeometry(x,a,joint,curvature);var second=EndpointScalarGeometry(x,b,joint,curvature);
        var tangent=(first.Unit.X*second.Unit.Y-first.Unit.Y*second.Unit.X)/options.AngularTolerance;
        return curvature?[tangent,(first.Curvature+second.Curvature)/(options.CurvatureTolerancePerMm*scale)]:[tangent];
    }
}
