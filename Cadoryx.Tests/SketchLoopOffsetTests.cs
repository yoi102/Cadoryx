using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchLoopOffsetTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Offset",RigidTransform3d.Identity));
    private static SketchProfile Result(CadSketch before,CadSketch after)
    {
        var ids=after.Lines.Select(l=>l.Id).Concat(after.Arcs.Select(a=>a.Id)).Except(before.Lines.Select(l=>l.Id).Concat(before.Arcs.Select(a=>a.Id))).ToHashSet();
        var loop=SketchLoops.Find(after).Concat(SketchMixedLoops.Find(after)).Single(l=>l.All(ids.Contains));
        return loop.Any(id=>after.Arcs.Any(a=>a.Id==id))?SketchProfileBuilder.Mixed(after,loop):SketchProfileBuilder.Polygon(after,loop);
    }
    private static void Polygon(SketchDraft d,params Point2d[] points)
    {
        var ids=points.Select(_=>SketchEntityId.New()).ToArray();
        d.Change(s=>s with{Points=s.Points.AddRange(points.Select((p,i)=>new SketchPoint(ids[i],p))),
            Lines=s.Lines.AddRange(points.Select((_,i)=>new SketchLine(SketchEntityId.New(),ids[i],ids[(i+1)%ids.Length])))});
    }
    [Theory] [InlineData(2,false)] [InlineData(-2,false)] [InlineData(2,true)] [InlineData(-2,true)]
    public async Task RectangleOffsetsHaveDirectionIndependentSignSharedEndpointsAndNativeVolume(double distance,bool reverse)
    {
        var d=Draft();d.AddRectangle(new(0,0),new(20,10));
        if(reverse)d.Change(s=>s with{Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray()});
        var before=d.Value;d.OffsetLoop(before.Lines[0].Id,distance);var after=d.Value;var profile=Result(before,after);
        Assert.Equal(before.Constraints,after.Constraints);Assert.Equal(before.Lines.ToArray(),after.Lines.Take(4).ToArray());
        Assert.Equal(-distance,profile.Points.Min(p=>p.X),7);Assert.Equal(20+distance,profile.Points.Max(p=>p.X),7);
        Assert.Equal(8,after.Points.Length);Assert.Equal(2,SketchLoops.Find(after).Length);
        using var body=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,3,RigidTransform3d.Identity),new MemoryAssetStore());
        Assert.Equal((20+2*distance)*(10+2*distance)*3,body.Geometry.VolumeMm3,5);
        d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
    }
    [Theory] [InlineData(2,false)] [InlineData(-2,false)] [InlineData(2,true)] [InlineData(-2,true)]
    public async Task MixedLoopPreservesExactTangentArcAndClosedNativeBrep(double distance,bool reverse)
    {
        var d=Draft();Polygon(d,new(0,0),new(20,0),new(20,10),new(0,10));
        var top=d.Value.Lines[2];d.Change(s=>s with{Lines=s.Lines.Remove(top)});
        d.AddArc(new(20,10),new(10,20),new(0,10),existingStart:top.Start,existingEnd:top.End);
        if(reverse)d.Change(s=>s with{Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray(),
            Arcs=s.Arcs.Select(a=>a with{Start=a.End,End=a.Start}).ToImmutableArray()});
        var before=d.Value;d.OffsetLoop(before.Arcs[0].Id,distance);var profile=Result(before,d.Value);
        Assert.Equal(2,SketchMixedLoops.Find(d.Value).Length);Assert.Equal(before.Arcs[0],d.Value.Arcs[0]);
        var arc=SketchArcGeometry.Through(profile.BoundaryCurves.Single(c=>c.Middle is not null).Start,
            profile.BoundaryCurves.Single(c=>c.Middle is not null).Middle!.Value,profile.BoundaryCurves.Single(c=>c.Middle is not null).End);
        Assert.Equal(10+distance,arc.Radius,6);Assert.InRange(Math.Abs(Math.Abs(arc.SweepAngle)-Math.PI),0,1e-7);
        using var body=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,2,RigidTransform3d.Identity),new MemoryAssetStore());
        double area=(20+2*distance)*(10+distance)+Math.PI*Math.Pow(10+distance,2)/2;
        Assert.Equal(area*2,body.Geometry.VolumeMm3,5);
    }
    [Fact] public void ConcaveAndCollinearLoopsOffsetButCollapseAndBranchRejectAtomically()
    {
        var d=Draft();Polygon(d,new(0,0),new(10,0),new(20,0),new(20,10),new(10,10),new(10,20),new(0,20));
        var before=d.Value;d.OffsetLoop(before.Lines[0].Id,1);Result(before,d.Value).Validate();
        d.Undo();before=d.Value;Assert.Throws<CadValidationException>(()=>d.OffsetLoop(before.Lines[0].Id,-6));Assert.Same(before,d.Value);
        d.AddLine(new(0,0),new(-4,-4),start:before.Lines[0].Start);before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.OffsetLoop(before.Lines[0].Id,1));Assert.Same(before,d.Value);
        var square=Draft();square.AddRectangle(new(0,0),new(10,10));before=square.Value;
        Assert.Throws<CadValidationException>(()=>square.OffsetLoop(before.Lines[0].Id,-5));
        Assert.Throws<CadValidationException>(()=>square.OffsetLoop(before.Lines[0].Id,-6));Assert.Same(before,square.Value);
        Assert.Throws<CadValidationException>(()=>square.OffsetLoop(before.Lines[0].Id,0));Assert.Same(before,square.Value);
    }
    [Theory] [InlineData(1)] [InlineData(-1)]
    public async Task SharpLineArcAndArcArcJoinsUseExactSupportIntersections(double distance)
    {
        var d=Draft();d.AddLine(new(-10,0),new(10,0));var bottom=d.Value.Lines[0];
        d.AddArc(new(10,0),new(Math.Sqrt(50),Math.Sqrt(50)),new(0,10),existingStart:bottom.End);
        var first=d.Value.Arcs[0];d.AddArc(new(0,10),new(-8,8),new(-10,0),existingStart:first.End,existingEnd:bottom.Start);
        var before=d.Value;var radius=SketchAnalyticalCurve.From(before,before.Arcs[1].Id).Arc!.Value.Radius;
        d.OffsetLoop(first.Id,distance);var profile=Result(before,d.Value);var arcs=profile.BoundaryCurves.Where(c=>c.Middle is not null)
            .Select(c=>SketchArcGeometry.Through(c.Start,c.Middle!.Value,c.End).Radius).Order().ToArray();
        Assert.Equal(radius+distance,arcs[0],6);Assert.Equal(10+distance,arcs[1],6);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using var sourceBody=await kernel.EvaluateAsync(new ExtrudeRecipe(SketchProfileBuilder.Mixed(before,SketchMixedLoops.Find(before).Single()),2,RigidTransform3d.Identity),assets);
        using var result=await kernel.EvaluateAsync(new ExtrudeRecipe(profile,2,RigidTransform3d.Identity),assets);
        Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.True((result.Geometry.VolumeMm3-sourceBody.Geometry.VolumeMm3)*distance>0);
    }
    [Fact] public void ExtremeMitersAndCircleCollapseRejectWhileOnlyTheSelectedDisconnectedLoopIsCopied()
    {
        var d=Draft();Polygon(d,new(0,0),new(100,0),new(100,.1));var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.OffsetLoop(before.Lines[0].Id,1));Assert.Same(before,d.Value);
        var separate=Draft();separate.AddRectangle(new(0,0),new(10,10));separate.AddRectangle(new(30,0),new(40,10));before=separate.Value;
        separate.OffsetLoop(before.Lines[0].Id,1);Assert.Equal(12,separate.Value.Lines.Length);Assert.Equal(3,SketchLoops.Find(separate.Value).Length);
        separate.AddCircle(new(60,0),3);var circle=separate.Value.Circles[0];separate.OffsetLoop(circle.Id,2);Assert.Equal(5,separate.Value.Circles[1].Radius);
        before=separate.Value;Assert.Throws<CadValidationException>(()=>separate.OffsetLoop(circle.Id,-3));Assert.Same(before,separate.Value);
    }
    [Fact] public void NarrowNeckSplitAndUnsupportedCurveFailWithoutChangingTheDraft()
    {
        var d=Draft();Polygon(d,new(0,0),new(8,0),new(8,3),new(12,3),new(12,0),new(20,0),new(20,8),new(12,8),new(12,5),new(8,5),new(8,8),new(0,8));
        var before=d.Value;Assert.Throws<CadValidationException>(()=>d.OffsetLoop(before.Lines[0].Id,-1.5));Assert.Same(before,d.Value);
        var b=Draft();Polygon(b,new(0,0),new(10,0),new(10,10),new(0,10));var top=b.Value.Lines[2];b.Change(s=>s with{Lines=s.Lines.Remove(top)});
        b.AddBezier(new(10,10),new(5,12),new(0,10),existingStart:top.Start,existingEnd:top.End);before=b.Value;
        Assert.Throws<CadValidationException>(()=>b.OffsetLoop(before.Lines[0].Id,1));Assert.Same(before,b.Value);
    }
    [Fact] public async Task EditorOffsetPreviewCancelCommitLinkedBodyUndoAndReopenRemainConsistent()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,10));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;var sketch=before.Sketches[d.Value.Id];
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var cancel=new SketchEditorViewModel(document,kernel,solver,sketch,false){Tool="OffsetLoop",OffsetDistance=2})
            {
                cancel.EditCurve(sketch.Lines[0].Id,new(5,0));await cancel.PreviewCommand.ExecuteAsync(null);Assert.True(cancel.CanConfirm,cancel.Status);
                Assert.Same(before,session.Snapshot);cancel.CancelCommand.Execute(null);
            }
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false){Tool="OffsetLoop",OffsetDistance=2};
            editor.EditCurve(sketch.Lines[0].Id,new(5,0));await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;var result=after.Sketches[sketch.Id];var ids=SketchLoops.Find(result).Single(l=>!l.Contains(sketch.Lines[0].Id));
        var reference=SketchProfileReference.Create(result,ids);
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(after,part,new ExtrudeRecipe(new([]),2,result.Plane)),"Offset body",part,sketchSource:reference));
        Assert.Equal(672,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);
        await session.UndoAsync();Assert.Same(after,session.Snapshot);await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();await session.RedoAsync();
        var path=files.PathFor("offset.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Equal(result.Lines.ToArray(),loaded.Snapshot.Sketches[result.Id].Lines.ToArray());loaded.Snapshot.Validate();
    }
}
