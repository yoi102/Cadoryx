using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionRegionSelectionTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Explicit region",RigidTransform3d.Identity));
    [Theory][InlineData(false)][InlineData(true)]
    public void ReversedIndividualAuthorsAndRepeatedSeedsKeepExactlyTheSameOriginalIdentities(bool reverse)
    {
        var d=Draft();d.AddRectangle(new(0,0),new(8,4));
        if(reverse)d.Change(s=>s with{Lines=s.Lines.Select((l,i)=>i%2==1?l with{Start=l.End,End=l.Start}:l).ToImmutableArray()});
        var before=d.Value;var selected=SketchExpressionRegionSelection.Select(before,[before.Lines[0].Id,before.Lines[2].Id,before.Lines[0].Id]);
        var loop=Assert.Single(selected);Assert.Equal(before.Lines.Select(l=>l.Id),loop.Select(s=>s.Id));
        Assert.Equal(reverse?new[]{false,true,false,true}:new[]{false,false,false,false},loop.Select(s=>s.Reversed));
        var result=new OcctGeometryKernel().OffsetExpressionRegions(selected,.25,ExactExpressionRegionJoin.Miter);
        Assert.Equal(38.25,result.FilledArea,8);Assert.Same(before,d.Value);
    }
    [Fact] public void AnExplicitBezierClosingEdgeKeepsBothAuthorIdsAndItsEndpointDirection()
    {
        var d=Draft();d.AddBezier(new(0,0),new(4,4),new(8,0));var b=d.Value.Beziers.Single();
        d.AddLine(new(8,0),new(0,0),b.End,b.Start);
        var selected=SketchExpressionRegionSelection.Select(d.Value,[b.Id]);var loop=Assert.Single(selected);
        Assert.Equal(new[]{b.Id,d.Value.Lines.Single().Id},loop.Select(s=>s.Id));Assert.All(loop,s=>Assert.False(s.Reversed));
        Assert.Null(loop[1].Geometry.Primitive!.BezierControl);Assert.Equal(new Point2d(4,4),loop[0].Geometry.Primitive!.BezierControl);
    }
    [Fact] public void EndpointEquationsIdentifyExpressionAuthorsAndNeverTheirSameCoordinateNeighbours()
    {
        var d=Draft();var g=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0)){BezierControl=new(4,4)}).Offset(.05);
        var expression=d.AddExpression(g);d.AttachExpressionEndpoints(expression);d.SetExpressionConstruction(expression,false);
        var e=d.Value.ExpressionCurves.Single();d.AddLine(g.At(1),g.At(0),e.Endpoints!.End,e.Endpoints.Start);
        var loop=Assert.Single(SketchExpressionRegionSelection.Select(d.Value,[expression]));
        Assert.Equal(expression,loop[0].Id);Assert.True(g.SameValues(loop[0].Geometry));Assert.Equal(2,loop.Length);
    }
    [Fact] public void AFullEllipseAndCircleStaySingleAuthorsWithoutAddingClosingChords()
    {
        var d=Draft();d.AddCircle(new(0,0),3);d.AddEllipse(new(new(20,0),new(7,0),new(0,3),.3,Math.Tau));
        var before=d.Value;var seeds=new[]{before.Ellipses.Single().Id,before.Circles.Single().Id};
        var selected=SketchExpressionRegionSelection.Select(before,seeds);Assert.Equal(2,selected.Length);
        Assert.All(selected,l=>Assert.Single(l));Assert.Equal(seeds,selected.Select(l=>l.Single().Id));Assert.Same(before,d.Value);
    }
    [Fact] public void RepeatedSeedEnumerationCannotBypassTheExistingInputBudget()
    {
        var d=Draft();d.AddRectangle(new(0,0),new(8,4));var before=d.Value;int yielded=0;
        IEnumerable<SketchEntityId> Seeds(){while(true){yielded++;yield return before.Lines[0].Id;}}
        Assert.Throws<CadValidationException>(()=>SketchExpressionRegionSelection.Select(before,Seeds()));
        Assert.Equal(257,yielded);Assert.Same(before,d.Value);
    }
    [Theory][InlineData("implicit-bezier")][InlineData("implicit-expression")][InlineData("open-ellipse")][InlineData("coincident-point-ids")][InlineData("branch")][InlineData("construction")][InlineData("missing")][InlineData("empty")]
    public void AnUnprovenIdentityChainRejectsWithoutMutatingTheDraft(string fault)
    {
        var d=Draft();SketchEntityId seed;
        if(fault is "implicit-bezier" or "coincident-point-ids")
        {d.AddBezier(new(0,0),new(4,4),new(8,0));seed=d.Value.Beziers.Single().Id;if(fault=="coincident-point-ids")d.AddLine(new(8,0),new(0,0));}
        else if(fault=="implicit-expression")
        {seed=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0)){BezierControl=new(4,4)}));d.AttachExpressionEndpoints(seed);d.SetExpressionConstruction(seed,false);}
        else if(fault=="open-ellipse")
        {d.AddEllipse(new(new(0,0),new(7,0),new(0,3),0,Math.PI));seed=d.Value.Ellipses.Single().Id;}
        else
        {
            d.AddRectangle(new(0,0),new(8,4),fault=="construction");seed=d.Value.Lines[0].Id;
            if(fault=="branch")d.AddLine(new(0,0),new(-1,0),d.Value.Lines[0].Start);
            if(fault=="missing")seed=SketchEntityId.New();
        }
        var before=d.Value;bool undo=d.CanUndo,redo=d.CanRedo;
        Assert.Throws<CadValidationException>(()=>SketchExpressionRegionSelection.Select(before,fault=="empty"?[]:[seed]));
        Assert.Same(before,d.Value);Assert.Equal(undo,d.CanUndo);Assert.Equal(redo,d.CanRedo);
    }
}
