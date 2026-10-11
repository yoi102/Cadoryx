using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionDifferentialTests
{
    private static readonly Type Differential=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchExpressionDifferentials")!;
    private static readonly Type Scalar=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!.GetNestedType("Scalar",BindingFlags.NonPublic)!;
    private static object Property(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value)!;
    private sealed class Fixture
    {
        internal CadSketch Sketch {get;}
        internal SketchEntityId Curve {get;}
        internal Point2d Origin {get;}=new(2,-3);
        internal double Scale {get;}=7;
        internal Dictionary<SketchEntityId,int> Points {get;}=[];
        internal Dictionary<SketchEntityId,int> Radii {get;}=[];
        internal double[] Coordinates {get;}
        internal int ParameterIndex {get;}
        internal Fixture(CadSketch sketch,SketchEntityId curve,double parameter=.37)
        {
            Sketch=sketch;Curve=curve;sketch.Validate();var coordinates=new List<double>();
            foreach(var p in sketch.Points){Points.Add(p.Id,coordinates.Count);coordinates.Add((p.Position.X-Origin.X)/Scale);coordinates.Add((p.Position.Y-Origin.Y)/Scale);}
            foreach(var c in sketch.Circles){Radii.Add(c.Id,coordinates.Count);coordinates.Add(c.Radius/Scale);}
            ParameterIndex=coordinates.Count;coordinates.Add(parameter);Coordinates=coordinates.ToArray();
        }
        internal object[] Analytic(double[]? coordinates=null,int order=3)
        {
            var x=coordinates??Coordinates;var gradient=new double[x.Length];gradient[ParameterIndex]=1;
            var parameter=Activator.CreateInstance(Scalar,[x[ParameterIndex],gradient])!;
            var result=Differential.GetMethod("Evaluate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,
                [Sketch,Curve,parameter,x,Points,Radii,Origin,Scale,order])!;
            return [Property(Property(result,"Position"),"X"),Property(Property(result,"Position"),"Y"),
                Property(Property(result,"Unit"),"X"),Property(Property(result,"Unit"),"Y"),Property(result,"Curvature"),Property(result,"Speed")];
        }
        internal CadSketch Candidate(double[] coordinates)=>SketchExpressionEntities.Refresh(Sketch with
        {
            Points=Sketch.Points.Select(p=>{int i=Points[p.Id];return p with{Position=new(coordinates[i]*Scale+Origin.X,coordinates[i+1]*Scale+Origin.Y)};}).ToImmutableArray(),
            Circles=Sketch.Circles.Select(c=>c with{Radius=coordinates[Radii[c.Id]]*Scale}).ToImmutableArray()
        });
        internal double[] Domain(double[] x)
        {
            var expression=Candidate(x).ExpressionCurves.Single(e=>e.Id==Curve).Geometry;var value=expression.Evaluate(x[ParameterIndex]);
            double speed=double.Hypot(value.First.X,value.First.Y);double curvature=(value.First.X*value.Second.Y-value.First.Y*value.Second.X)/(speed*speed*speed);
            return [(value.Point.X-Origin.X)/Scale,(value.Point.Y-Origin.Y)/Scale,value.First.X/speed,value.First.Y/speed,curvature*Scale,speed/Scale];
        }
    }
    internal static (CadSketch Sketch,SketchEntityId Curve) Source(int kind,bool reverse,bool partial)
    {
        SketchDraft draft;SketchEntityId source;
        if(kind<7)(draft,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),kind);
        else
        {
            draft=new(CadSketch.Create(DefinitionId.New(),"Fixed rational source",RigidTransform3d.Identity));
            draft.AddFreeform(SketchFreeformGeometryTests.Geometry(kind==7?0:kind==8?1:3));source=draft.Value.Freeforms[0].Id;
        }
        var curve=draft.AddExactOffset(source,.04);var author=draft.Value.ExpressionCurves.Single();
        var geometry=(reverse?author.Geometry.Reverse():author.Geometry).Offset(-.01);
        if(partial)geometry=geometry.Trim(.1,.9);
        var updated=author with{Geometry=geometry,Source=author.Source! with{BasisDepth=2}};
        var sketch=draft.Value with{ExpressionCurves=[updated]};sketch.Validate();return(sketch,curve);
    }
    public static IEnumerable<object[]> Sources()=>from kind in Enumerable.Range(0,10) from reverse in new[]{false,true}
        from partial in new[]{false,true} select new object[]{kind,reverse,partial};
    [Theory][MemberData(nameof(Sources))]
    public void EverySourceCoordinateAndParameterColumnMatchesIndependentAuthorPerturbations(int kind,bool reverse,bool partial)
    {
        var(sketch,curve)=Source(kind,reverse,partial);var fixture=new Fixture(sketch,curve);CheckColumns(fixture);
        // A different numerical path checks the actual native offset point, tangent and curvature.
        var native=new OcctGeometryKernel().EvaluateExpression(sketch.ExpressionCurves.Single().Geometry,.37);
        var actual=fixture.Analytic();double speed=double.Hypot(native.First.X,native.First.Y);
        Near((native.Point.X-fixture.Origin.X)/fixture.Scale,Value(actual[0]),1e-8);
        Near((native.Point.Y-fixture.Origin.Y)/fixture.Scale,Value(actual[1]),1e-8);
        Near(native.First.X/speed,Value(actual[2]),1e-8);Near(native.First.Y/speed,Value(actual[3]),1e-8);
        Near((native.First.X*native.Second.Y-native.First.Y*native.Second.X)/(speed*speed*speed)*fixture.Scale,Value(actual[4]),1e-8);
    }
    private static double Value(object scalar)=>(double)Property(scalar,"Value");
    private static double[] Gradient(object scalar)=>(double[])Property(scalar,"Gradient");
    private static void Near(double expected,double actual,double tolerance)=>Assert.InRange(Math.Abs(expected-actual),0,tolerance*Math.Max(1,Math.Abs(expected)));
    private static void CheckColumns(Fixture fixture)
    {
        var actual=fixture.Analytic();var expected=fixture.Domain(fixture.Coordinates);
        for(int row=0;row<6;row++)
        {
            Near(expected[row],Value(actual[row]),1e-8);
            for(int column=0;column<fixture.Coordinates.Length;column++)
            {
                // Only the independent test oracle perturbs authors; production differentiates analytically.
                const double h=2e-6;var plus=(double[])fixture.Coordinates.Clone();var minus=(double[])fixture.Coordinates.Clone();plus[column]+=h;minus[column]-=h;
                double difference=(fixture.Domain(plus)[row]-fixture.Domain(minus)[row])/(2*h);
                Near(difference,Gradient(actual[row])[column],3e-5);
            }
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FrozenAndDetachedSourcesHaveNoSourceCoordinateSensitivity(bool detach)
    {
        var(sketch,id)=Source(2,true,true);var draft=new SketchDraft(sketch);if(detach)draft.DetachExpression(id);else draft.FreezeExpression(id);
        var fixture=new Fixture(draft.Value,id);CheckColumns(fixture);var actual=fixture.Analytic();
        Assert.All(actual,v=>Assert.All(Gradient(v).Take(fixture.ParameterIndex),g=>Assert.Equal(0,g)));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ExpressionSourceChainsAndContinuedNativeWindowsPreserveFullGradients(bool continued)
    {
        var(draft,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),4);var parent=draft.AddExactOffset(source,.04);var id=draft.AddExactOffset(parent,-.01);
        var curve=draft.Value.ExpressionCurves.Single(c=>c.Id==id);
        if(continued)
        {
            var geometry=curve.Geometry.ContinueNativeRange(curve.Geometry.FirstParameter-.03,curve.Geometry.LastParameter+.04).Reverse();
            draft=new(draft.Value with{ExpressionCurves=draft.Value.ExpressionCurves.Replace(curve,curve with{Geometry=geometry,Source=curve.Source! with{BasisDepth=2}})});
        }
        CheckColumns(new(draft.Value,id));
    }
    [Theory][InlineData(0d)][InlineData(1d)]
    public void ExplicitEndpointsKeepOneSidedNativeDirectionAndSourceDerivatives(double parameter)
    {
        var(sketch,id)=Source(2,true,false);var fixture=new Fixture(sketch,id,parameter);var actual=fixture.Analytic();var expected=fixture.Domain(fixture.Coordinates);
        for(int row=0;row<6;row++)Near(expected[row],Value(actual[row]),1e-8);
        for(int column=0;column<fixture.ParameterIndex;column++)
        {
            const double h=2e-6;var plus=(double[])fixture.Coordinates.Clone();var minus=(double[])fixture.Coordinates.Clone();plus[column]+=h;minus[column]-=h;
            var a=fixture.Domain(plus);var b=fixture.Domain(minus);
            for(int row=0;row<6;row++)Near((a[row]-b[row])/(2*h),Gradient(actual[row])[column],3e-5);
        }
    }
    [Fact] public void EveryIntermediateOffsetMustKeepPositiveTangentialScale()
    {
        var(sketch,id)=Source(1,false,false);var circle=sketch.Circles.Single();var basis=SketchExpressionEntities.Author(sketch,circle.Id).Geometry;
        var bad=basis.Offset(-6).Offset(6);var curve=sketch.ExpressionCurves.Single();
        var fixture=new Fixture(sketch with{ExpressionCurves=[curve with{Geometry=bad,Source=null}]},id);
        var error=Assert.Throws<TargetInvocationException>(()=>fixture.Analytic());Assert.IsType<CadValidationException>(error.InnerException);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LinkedNonuniformSplineKnotsKeepTheirNativeUnitsAndAllControlColumns(bool reverse)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Nonuniform live source",RigidTransform3d.Identity));
        draft.AddSpline(new CubicSplineDefinition([new(0,0),new(1,2),new(3,4),new(5,3),new(7,1),new(9,0)],
            [2,2.35,3.7,5],[4,1,1,4]));
        var id=draft.AddExactOffset(draft.Value.Splines[0].Id,.03);var curve=draft.Value.ExpressionCurves.Single();
        var geometry=(reverse?curve.Geometry.Reverse():curve.Geometry).Trim(.1,.9);
        draft.EditExpression(id,geometry);CheckColumns(new(draft.Value,id));
    }
    [Fact] public void Full64NodeCircleKeepsTheIndependentRadiusAndCenterGradient()
    {
        var(draft,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),1);var id=draft.AddExactOffset(source,0);
        var curve=draft.Value.ExpressionCurves.Single();var expression=curve.Geometry;
        while(Depth(expression)<64)expression=expression.Offset(0);
        var sketch=draft.Value with{ExpressionCurves=[curve with{Geometry=expression,Source=curve.Source! with{BasisDepth=63}}]};
        var fixture=new Fixture(sketch,id);var actual=fixture.Analytic();var circle=sketch.Circles.Single();
        double angle=fixture.Coordinates[fixture.ParameterIndex]*Math.Tau;int center=fixture.Points[circle.Center],radius=fixture.Radii[circle.Id];
        Near(Math.Cos(angle),Gradient(actual[0])[radius],1e-9);Near(Math.Sin(angle),Gradient(actual[1])[radius],1e-9);
        Near(1,Gradient(actual[0])[center],1e-9);Near(1,Gradient(actual[1])[center+1],1e-9);
        Near(-fixture.Scale*fixture.Scale/(circle.Radius*circle.Radius),Gradient(actual[4])[radius],1e-9);
        Assert.Throws<CadValidationException>(()=>expression.Offset(0));
        static int Depth(SketchCurveExpressionGeometry g){int count=1;while(g.Basis is {} basis){g=basis;count++;}return count;}
    }
    [Fact] public void OffsetDerivativeAmbiguityAtPeriodicKnotKeepsOriginalAuthorValues()
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"One-sided proof",RigidTransform3d.Identity));
        draft.AddFreeform(SketchFreeformGeometryTests.Geometry(3));var id=draft.AddExactOffset(draft.Value.Freeforms[0].Id,.02);
        var fixture=new Fixture(draft.Value,id,.5);var before=draft.Value;
        var error=Assert.Throws<TargetInvocationException>(()=>fixture.Analytic());
        Assert.IsType<CadValidationException>(error.InnerException);Assert.Equal("SlidingContactDerivativeAmbiguous",error.InnerException!.Data["SketchContact.Message"]);
        Assert.Same(before,draft.Value);
    }
}
