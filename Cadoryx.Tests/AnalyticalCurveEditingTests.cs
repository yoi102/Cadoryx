using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AnalyticalCurveEditingTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Curves",RigidTransform3d.Identity));
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    [Fact] public void StraightLineTrimsAndExtendsAgainstFiniteCircularGeometry()
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0));var line=d.Value.Lines[0];d.AddCircle(new(5,0),2);
        var before=d.Value;d.TrimCurve(line.Id,new(5,0));var pieces=d.Value.Lines.OrderBy(l=>P(d.Value,l.Start).X).ToArray();
        Assert.Equal(2,pieces.Length);Assert.DoesNotContain(pieces,l=>l.Id==line.Id);
        Assert.Equal(new Point2d(3,0),P(d.Value,pieces[0].End));Assert.Equal(new Point2d(7,0),P(d.Value,pieces[1].Start));
        var after=d.Value;d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
        var e=Draft();e.AddLine(new(0,0),new(2,0));line=e.Value.Lines[0];e.AddCircle(new(5,0),1);
        e.ExtendCurve(line.Id,new(1.9,0));Assert.Equal(new Point2d(4,0),P(e.Value,e.Value.Lines.Single().End));
    }
    [Fact] public async Task CircleTrimPreservesExactSemicircleWithNewIdentityAndNativeBrep()
    {
        var d=Draft();d.AddCircle(new(0,0),5);var circle=d.Value.Circles[0];d.AddLine(new(0,-8),new(0,8),construction:true);
        d.TrimCurve(circle.Id,new(-5,0));Assert.Empty(d.Value.Circles);var arc=Assert.Single(d.Value.Arcs);
        Assert.NotEqual(circle.Id,arc.Id);Assert.Empty(d.Value.Constraints);Assert.DoesNotContain(d.Value.Points,p=>p.Id==circle.Center);
        var geometry=SketchAnalyticalCurve.From(d.Value,arc.Id);Assert.InRange(Math.Abs(geometry.Arc!.Value.SweepAngle-Math.PI),0,1e-8);
        Assert.InRange(Math.Abs(geometry.At(.5).X-5),0,1e-8);var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=SketchProfileBuilder.ArcSegment(d.Value,arc.Id);
        using var extrusion=await kernel.EvaluateAsync(new ExtrudeRecipe(profile,5,RigidTransform3d.Identity),assets);
        Assert.Equal(BodyKind.Solid,extrusion.Geometry.Kind);Assert.Equal(62.5*Math.PI,extrusion.Geometry.VolumeMm3,5);
        using var revolution=await kernel.EvaluateAsync(new RevolveRecipe(profile,Math.Tau,RigidTransform3d.Identity),assets);
        Assert.Equal(500*Math.PI/3,revolution.Geometry.VolumeMm3,5);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ArcTrimAndExtensionRespectBothDirections(bool reverse)
    {
        var d=Draft();var a=new Point2d(5,0);var b=new Point2d(-5,0);
        d.AddArc(reverse?b:a,new(0,5),reverse?a:b);var id=d.Value.Arcs[0].Id;
        d.AddLine(new(0,-1),new(0,7),construction:true);
        d.TrimCurve(id,reverse?new(-4,3):new(4,3));var result=Assert.Single(d.Value.Arcs);Assert.Equal(id,result.Id);
        Assert.InRange(Math.Abs(SketchAnalyticalCurve.From(d.Value,id).Arc!.Value.SweepAngle),Math.PI/2-1e-8,Math.PI/2+1e-8);
        var e=Draft();e.AddArc(reverse?new(0,5):new(5,0),new(Math.Sqrt(12.5),Math.Sqrt(12.5)),reverse?new(5,0):new(0,5));
        id=e.Value.Arcs[0].Id;e.AddLine(new(-6,0),new(-4,0));
        e.ExtendCurve(id,new(1,Math.Sqrt(24)));
        var extended=SketchAnalyticalCurve.From(e.Value,id);Assert.InRange(Math.Abs(extended.Arc!.Value.SweepAngle),Math.PI-1e-8,Math.PI+1e-8);
    }
    [Theory] [InlineData(2)] [InlineData(-2)]
    public void OffsetsAreIndependentWithSignedLineSideAndExactCircularRadius(double distance)
    {
        var d=Draft();d.AddLine(new(0,0),new(10,0),construction:true);var original=d.Value;
        d.OffsetCurve(original.Lines[0].Id,distance);Assert.Equal(original.Lines[0],d.Value.Lines[0]);
        Assert.Equal(new Point2d(0,distance),P(d.Value,d.Value.Lines[1].Start));Assert.True(d.Value.Lines[1].IsConstruction);
        d.AddCircle(new(20,0),5);var circle=d.Value.Circles[0];int constraints=d.Value.Constraints.Length;
        d.OffsetCurve(circle.Id,distance);Assert.Equal(5+distance,d.Value.Circles[1].Radius);Assert.Equal(constraints,d.Value.Constraints.Length);
        d.AddArc(new(35,0),new(30,5),new(25,0));var arc=d.Value.Arcs[0];d.OffsetCurve(arc.Id,distance);
        var shifted=SketchAnalyticalCurve.From(d.Value,d.Value.Arcs[1].Id).Arc!.Value;
        Assert.Equal(5+distance,shifted.Radius,7);Assert.Equal(new Point2d(30,0),shifted.Center);
    }
    [Fact] public void TangentCoincidentFiniteMissAndCollapsedOffsetsAreAtomic()
    {
        var d=Draft();d.AddCircle(new(0,0),5);var id=d.Value.Circles[0].Id;var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ExtendCurve(id,new(5,0)));
        Assert.Throws<CadValidationException>(()=>d.OffsetCurve(id,-5));Assert.Throws<CadValidationException>(()=>d.OffsetCurve(id,0));Assert.Same(before,d.Value);
        d.AddLine(new(-8,5),new(8,5));before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimCurve(id,new(0,-5)));Assert.Same(before,d.Value);
        d.Undo();d.AddCircle(new(0,0),5);before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimCurve(id,new(0,-5)));Assert.Same(before,d.Value);
        var e=Draft();e.AddLine(new(0,0),new(2,0));id=e.Value.Lines[0].Id;e.AddArc(new(5,1),new(6,0),new(5,-1));
        e.ExtendCurve(id,new(1.8,0));Assert.Equal(new Point2d(6,0),P(e.Value,e.Value.Lines[0].End)); // Supporting circle's left hit is outside the finite arc.
    }
    [Fact] public async Task CircleCircleIntersectionsRetainExactMajorArcWithoutChangingTheCutter()
    {
        var d=Draft();d.AddCircle(new(0,0),5);var id=d.Value.Circles[0].Id;d.AddCircle(new(6,0),5);var cutter=d.Value.Circles[1];
        d.TrimCurve(id,new(5,0));Assert.Equal(cutter,Assert.Single(d.Value.Circles));var arc=d.Value.Arcs[0];
        Assert.InRange(P(d.Value,arc.Start).X,3-1e-7,3+1e-7);Assert.InRange(P(d.Value,arc.End).X,3-1e-7,3+1e-7);
        var geometry=SketchAnalyticalCurve.From(d.Value,arc.Id).Arc!.Value;Assert.True(geometry.SweepAngle>Math.PI);
        double theta=2*Math.Acos(3d/5),area=25*Math.PI-12.5*(theta-Math.Sin(theta));
        using var result=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(SketchProfileBuilder.ArcSegment(d.Value,arc.Id),10,RigidTransform3d.Identity),new MemoryAssetStore());
        Assert.Equal(area*10,result.Geometry.VolumeMm3,5);
    }
}
