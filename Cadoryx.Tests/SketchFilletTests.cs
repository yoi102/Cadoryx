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

public sealed class SketchFilletTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    private static SketchDraft Corner(double angle=90,bool reverseA=false,bool reverseB=false,double translation=0)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Fillet",RigidTransform3d.Identity));
        var o=new Point2d(translation,translation);var a=new Point2d(translation+20,translation);
        var b=new Point2d(translation+20*Math.Cos(angle*Math.PI/180),translation+20*Math.Sin(angle*Math.PI/180));
        d.AddLine(reverseA?a:o,reverseA?o:a);var joint=reverseA?d.Value.Lines[0].End:d.Value.Lines[0].Start;
        d.AddLine(reverseB?b:o,reverseB?o:b,start:reverseB?null:joint,end:reverseB?joint:null);return d;
    }
    [Theory]
    [InlineData(90,false,false,0)] [InlineData(90,true,false,0)] [InlineData(90,false,true,0)] [InlineData(90,true,true,0)]
    [InlineData(30,false,true,0)] [InlineData(150,true,false,0)] [InlineData(-60,true,true,0)] [InlineData(90,true,true,1000000)]
    public async Task RadiusTrimTangencyAndIdentityAreExactAcrossAnglesAndDirections(double angle,bool reverseA,bool reverseB,double translation)
    {
        var d=Corner(angle,reverseA,reverseB,translation);var before=d.Value;
        d.Fillet(before.Lines[0].Id,before.Lines[1].Id,2);var after=d.Value;var arc=Assert.Single(after.Arcs);
        var g=SketchArcGeometry.Through(P(after,arc.Start),P(after,arc.Middle),P(after,arc.End));
        Assert.Equal(2,g.Radius,7);Assert.Equal(Math.PI-Math.Abs(angle)*Math.PI/180,Math.Abs(g.SweepAngle),7);
        Assert.Equal(before.Lines.Select(l=>l.Id),after.Lines.Select(l=>l.Id));
        double trim=2/Math.Tan(Math.Abs(angle)*Math.PI/360);
        Assert.Equal(trim,double.Hypot(P(after,arc.Start).X-translation,P(after,arc.Start).Y-translation),7);
        foreach(var c in after.Constraints.Cast<EndpointTangentConstraint>())
        {
            var line=after.Lines.Single(l=>l.Id==c.A);var p=P(after,c.Joint);var e=P(after,line.Start==c.Joint?line.End:line.Start);
            Assert.InRange(Math.Abs((e.X-p.X)*(p.X-g.Center.X)+(e.Y-p.Y)*(p.Y-g.Center.Y)),0,1e-6);
        }
        var report=await new ManagedSketchConstraintSolver().SolveAsync(after);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(after.Points.Select(p=>p.Id),report.Solution!.Points.Select(p=>p.Id));
        foreach(var point in after.Points)Assert.InRange(double.Hypot(point.Position.X-P(report.Solution!,point.Id).X,point.Position.Y-P(report.Solution!,point.Id).Y),0,1e-7);
        d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(1e-8)] [InlineData(20)] [InlineData(30)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidRadiusDoesNotChangeValueOrHistory(double radius)
    {
        var d=Corner();var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Fillet(before.Lines[0].Id,before.Lines[1].Id,radius));Assert.Same(before,d.Value);
        d.Undo();Assert.Single(d.Value.Lines);
    }
    [Theory] [InlineData(0)] [InlineData(180)] [InlineData(.00001)] [InlineData(179.99999)]
    public void CollinearAndUnstableCornersReject(double angle)
    {
        var d=Corner(angle);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Fillet(before.Lines[0].Id,before.Lines[1].Id,2));Assert.Same(before,d.Value);
    }
    [Fact] public void CoordinateCoincidenceDuplicateLinesBranchAndMixedConstructionReject()
    {
        var d=Corner();var a=d.Value.Lines[0];var b=d.Value.Lines[1];
        Assert.Throws<CadValidationException>(()=>d.Fillet(a.Id,a.Id,2));
        d.Change(s=>s with{Lines=s.Lines.SetItem(1,b with{Start=a.End,End=a.Start})});
        Assert.Throws<CadValidationException>(()=>d.Fillet(a.Id,b.Id,2));d.Undo();
        d.AddLine(new(0,0),new(-5,0));Assert.Throws<CadValidationException>(()=>d.Fillet(a.Id,d.Value.Lines[^1].Id,2));d.Undo();
        d.AddLine(new(0,0),new(-5,0),a.Start);Assert.Throws<CadValidationException>(()=>d.Fillet(a.Id,b.Id,2));d.Undo();
        d.SetConstruction([a.Id],true);Assert.Throws<CadValidationException>(()=>d.Fillet(a.Id,b.Id,2));
    }
    [Fact] public void RemovesAffectedEnabledAndDisabledConstraintsButPreservesFarPointAndOtherGeometry()
    {
        var d=Corner();var a=d.Value.Lines[0];var b=d.Value.Lines[1];
        var keep=new FixPointConstraint(SketchConstraintId.New(),a.End,P(d.Value,a.End));
        d.Change(s=>s with{Constraints=[keep,new HorizontalConstraint(SketchConstraintId.New(),a.Id),
            new LengthConstraint(SketchConstraintId.New(),b.Id,20,false),new FixPointConstraint(SketchConstraintId.New(),a.Start,new(0,0))]});
        d.Fillet(a.Id,b.Id,2);Assert.Equal(3,d.Value.Constraints.Length);Assert.Contains(keep,d.Value.Constraints);
        Assert.DoesNotContain(d.Value.Points,p=>p.Id==a.Start);Assert.Equal(new Point2d(20,0),P(d.Value,a.End));
    }
    [Fact] public void ProjectedLineRequiresExplicitDetach()
    {
        var source=Corner();var target=new SketchDraft(CadSketch.Create(source.Value.PartId,"Projected",RigidTransform3d.Identity));
        target.Project(source.Value,source.Value.Lines[0].Id);var a=target.Value.Lines[0];
        target.AddLine(P(target.Value,a.Start),new(0,20),a.Start,construction:true);var b=target.Value.Lines[^1];var before=target.Value;
        Assert.Throws<CadValidationException>(()=>target.Fillet(a.Id,b.Id,2));Assert.Same(before,target.Value);
        target.DetachProjection(a.Id);target.Fillet(a.Id,b.Id,2);Assert.True(Assert.Single(target.Value.Arcs).IsConstruction);
    }
    [Fact] public async Task ExistingPolygonFeatureRefusesChangedLoopAtomically()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Linked",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,20));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
        var reference=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));var recipe=reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked",part,sketchSource:reference));var before=session.Snapshot;
        var edit=new SketchDraft(s);edit.Fillet(s.Lines[0].Id,s.Lines[1].Id,2);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver)));
        Assert.Same(before,session.Snapshot);Assert.Equal(1200,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,6);
    }
    [Fact] public async Task EditorPreviewCancelConfirmNativeProfileUndoAndReopen()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Native fillet",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,20));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;var sketch=before.Sketches[d.Value.Id];
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var canceled=new SketchEditorViewModel(document,kernel,solver,sketch,false))
            {
                canceled.Select(sketch.Lines[0].Id);canceled.Select(sketch.Lines[1].Id,true);canceled.ApplyFilletCommand.Execute(null);
                await canceled.PreviewCommand.ExecuteAsync(null);Assert.True(canceled.CanConfirm,canceled.Status);canceled.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
            editor.Select(sketch.Lines[0].Id);editor.Select(sketch.Lines[1].Id,true);editor.FilletRadius=2;editor.ApplyFilletCommand.Execute(null);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var s=after.Sketches[sketch.Id];var source=SketchProfileReference.CreateMixed(s,[s.Lines[0].Id,s.Arcs[0].Id,s.Lines[1].Id,s.Lines[2].Id,s.Lines[3].Id]);
        var recipe=source.Resolve(after,part,new ExtrudeRecipe(new([]),3,s.Plane));await session.ExecuteAsync(new AddBodyCommand(recipe,"Rounded",part,sketchSource:source));
        Assert.Equal((400-(4-Math.PI))*3,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,6);
        var storage=new CadDocumentStorage();var path=files.PathFor("fillet.cadoryx");await session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,assets);loaded.Snapshot.Validate();var restored=loaded.Snapshot.Sketches[s.Id];
        Assert.Equal(s.Points.ToArray(),restored.Points.ToArray());Assert.Equal(s.Arcs.ToArray(),restored.Arcs.ToArray());Assert.Equal(s.Constraints.ToArray(),restored.Constraints.ToArray());
    }
}
