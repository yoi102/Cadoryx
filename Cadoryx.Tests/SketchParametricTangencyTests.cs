using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchParametricTangencyTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    private static FixPointConstraint Fix(CadSketch s,SketchEntityId id)=>new(SketchConstraintId.New(),id,P(s,id));
    private static SketchEntityId AddCurve(SketchDraft d,string kind,Point2d[] points,SketchEntityId joint,bool reverse)
    {
        var ordered=reverse?points.Reverse().ToArray():points;
        if(kind=="Bezier")d.AddBezier(ordered[0],ordered[1],ordered[2],existingStart:reverse?null:joint,existingEnd:reverse?joint:null);
        else if(kind=="Arc")d.AddArc(ordered[0],ordered[1],ordered[2],existingStart:reverse?null:joint,existingEnd:reverse?joint:null);
        else d.AddSpline(ordered,existingStart:reverse?null:joint,existingEnd:reverse?joint:null);
        return kind=="Bezier"?d.Value.Beziers[^1].Id:kind=="Arc"?d.Value.Arcs[^1].Id:d.Value.Splines[^1].Id;
    }
    private static (SketchDraft Draft,SketchEntityId Joint,SketchEntityId Curve,SketchEntityId Neighbor,EndpointTangentConstraint Relation) LineCurve(
        DefinitionId part,string kind,bool reverse=false,int count=4,double translation=0)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Parametric tangent",RigidTransform3d.Identity));
        d.AddLine(new(translation-6,translation),new(translation,translation));var line=d.Value.Lines[0];var joint=line.End;
        Point2d At(double x,double y)=>new(translation+x,translation+y);
        var points=kind=="Bezier"?new[]{At(0,0),At(3,.5),At(6,4)}:count==4?new[]{At(0,0),At(2,.5),At(4,1),At(6,4)}:
            Enumerable.Range(0,count).Select(i=>At(6d*i/(count-1),4*Math.Pow(i/(double)(count-1),2))).ToArray();
        var curve=AddCurve(d,kind,points,joint,reverse);var neighbor=SketchEndpointTangency.TangentNeighbor(d.Value,curve,joint);
        var relation=new EndpointTangentConstraint(SketchConstraintId.New(),curve,line.Id,joint);
        d.Change(s=>s with{Constraints=[..s.Points.Where(p=>p.Id!=neighbor).Select(p=>Fix(s,p.Id)),
            new OffsetXConstraint(SketchConstraintId.New(),joint,neighbor,P(s,neighbor).X-translation),relation]});
        return(d,joint,curve,neighbor,relation);
    }
    private static Point2d IndependentTangent(CadSketch s,SketchEntityId id,SketchEntityId joint)
    {
        var p=P(s,joint);Point2d d;
        if(s.Lines.SingleOrDefault(l=>l.Id==id) is {} line)
        {var q=P(s,line.Start==joint?line.End:line.Start);d=new(q.X-p.X,q.Y-p.Y);}
        else if(s.Arcs.SingleOrDefault(a=>a.Id==id) is {} arc)
        {
            var circle=SketchArcGeometry.Through(P(s,arc.Start),P(s,arc.Middle),P(s,arc.End));double sign=Math.Sign(circle.SweepAngle)*(joint==arc.Start?1:-1);
            d=new(-(p.Y-circle.Center.Y)*sign,(p.X-circle.Center.X)*sign);
        }
        else if(s.Beziers.SingleOrDefault(b=>b.Id==id) is {} bezier)
        {var q=P(s,bezier.Control);d=new(2*(q.X-p.X),2*(q.Y-p.Y));}
        else
        {
            var spline=s.Splines.Single(v=>v.Id==id);var controls=spline.Controls.Select(point=>
                new Point2d(P(s,point).X-p.X,P(s,point).Y-p.Y)).ToImmutableArray();
            // Independent one-sided de Boor sample verifies the clamped endpoint derivative direction.
            d=SketchSplineGeometry.At(controls,joint==spline.Controls[0]?1e-7:1-1e-7,spline.Knots);
        }
        double length=double.Hypot(d.X,d.Y);return new(d.X/length,d.Y/length);
    }
    private static void AssertSmooth(CadSketch s,EndpointTangentConstraint c)
    {
        var a=IndependentTangent(s,c.A,c.Joint);var b=IndependentTangent(s,c.B,c.Joint);
        Assert.InRange(Math.Abs(a.X*b.Y-a.Y*b.X),0,2e-5);Assert.True(a.X*b.X+a.Y*b.Y<-.99999999);
    }
    [Theory] [InlineData("Bezier",false,4,0)] [InlineData("Bezier",true,4,0)] [InlineData("Bezier",true,4,1000000)]
    [InlineData("Spline",false,4,0)] [InlineData("Spline",true,4,0)] [InlineData("Spline",false,5,0)] [InlineData("Spline",true,16,0)] [InlineData("Spline",true,5,1000000)]
    public async Task LineParametricTangencySolvesBothEndpointsAndControlCounts(string kind,bool reverse,int count,double translation)
    {
        var (d,joint,curve,neighbor,c)=LineCurve(DefinitionId.New(),kind,reverse,count,translation);var solver=new ManagedSketchConstraintSolver();
        var report=await solver.SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(0,report.DegreesOfFreedom);
        Assert.Equal(translation,P(report.Solution!,neighbor).Y,6);AssertSmooth(report.Solution!,c);
        var moved=d.Value with{Constraints=d.Value.Constraints.Select(v=>v is FixPointConstraint f&&f.Point==d.Value.Lines[0].Start?
            f with{Position=new(translation-6,translation-1.5)}:v).ToImmutableArray()};
        report=await solver.SolveAsync(moved);Assert.True(report.Succeeded,report.Diagnostic);AssertSmooth(report.Solution!,c);
        Assert.Equal(translation+(P(d.Value,neighbor).X-translation)/4,P(report.Solution!,neighbor).Y,6);
        Assert.Equal(joint,SketchEndpointTangency.CommonEndpoint(report.Solution!,curve,d.Value.Lines[0].Id));
    }
    [Theory] [InlineData("Bezier","Bezier",false)] [InlineData("Bezier","Bezier",true)] [InlineData("Bezier","Spline",false)] [InlineData("Bezier","Spline",true)]
    [InlineData("Spline","Spline",false)] [InlineData("Spline","Spline",true)] [InlineData("Arc","Bezier",false)] [InlineData("Arc","Bezier",true)]
    [InlineData("Arc","Spline",false)] [InlineData("Arc","Spline",true)]
    public async Task ParametricCurvePairsAndCircularArcsUseTheSameSmoothBranch(string firstKind,string secondKind,bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Pair",RigidTransform3d.Identity));d.AddPoint(new(0,0));var joint=d.Value.Points[0].Id;
        var firstPoints=firstKind=="Spline"?new Point2d[]{new(0,0),new(2,0),new(4,1),new(6,4)}:
            firstKind=="Arc"?[new(0,0),new(2,4-Math.Sqrt(12)),new(4,4)]:[new(0,0),new(3,0),new(6,4)];
        var first=AddCurve(d,firstKind,firstPoints,joint,reverse);
        var secondPoints=secondKind=="Spline"?new Point2d[]{new(0,0),new(-2,-.4),new(-4,1),new(-6,4)}:[new(0,0),new(-3,-.5),new(-6,4)];
        var second=AddCurve(d,secondKind,secondPoints,joint,!reverse);var neighbor=SketchEndpointTangency.TangentNeighbor(d.Value,second,joint);
        var c=new EndpointTangentConstraint(SketchConstraintId.New(),second,first,joint);
        d.Change(s=>s with{Constraints=[..s.Points.Where(p=>p.Id!=neighbor).Select(p=>Fix(s,p.Id)),
            new OffsetXConstraint(SketchConstraintId.New(),joint,neighbor,P(s,neighbor).X),c]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(0,report.DegreesOfFreedom);
        Assert.Equal(0,P(report.Solution!,neighbor).Y,6);AssertSmooth(report.Solution!,c);
    }
    [Theory] [InlineData("Bezier")] [InlineData("Spline")]
    public async Task FlatCurveSemanticsConflictDisableBudgetAndRedundancyAreDiagnosed(string kind)
    {
        var (d,joint,_,neighbor,c)=LineCurve(DefinitionId.New(),kind);var solver=new ManagedSketchConstraintSolver();
        var disabled=d.Value with{Constraints=d.Value.Constraints.Select(v=>v.Id==c.Id?v with{IsEnabled=false}:v).ToImmutableArray()};
        Assert.Equal(1,(await solver.SolveAsync(disabled)).DegreesOfFreedom);
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(d.Value,new(MaxEquations:d.Value.Points.Length*2-1))).Status);
        var duplicate=d.Value with{Constraints=d.Value.Constraints.Add(c with{Id=SketchConstraintId.New()})};var report=await solver.SolveAsync(duplicate);
        Assert.True(report.Succeeded);Assert.NotEmpty(report.RedundantConstraints);
        var conflict=d.Value with{Constraints=d.Value.Constraints.Add(Fix(d.Value,neighbor))};report=await solver.SolveAsync(conflict);
        Assert.False(report.Succeeded);Assert.Equal(SketchSolveStatus.DidNotConverge,report.Status);Assert.Empty(report.ConflictingConstraints);
        var flat=d.Value with{Constraints=d.Value.Constraints.Select(v=>v is FixPointConstraint f&&f.Point==d.Value.Lines[0].Start?f with{Position=new(-6,-4)}:v).ToImmutableArray()};
        report=await solver.SolveAsync(flat,new(LinearToleranceMm:1e-10,AngularTolerance:1e-12));
        if(kind=="Spline")
        {
            // An explicit spline is a curve, not an assertion that its chord encloses a region.
            Assert.True(report.Succeeded,report.Diagnostic);AssertSmooth(report.Solution!,c);
        }
        else {Assert.False(report.Succeeded);Assert.Null(report.Solution);}
        var cusp=d.Value with{Points=d.Value.Points.Select(p=>p.Id==d.Value.Lines[0].Start?p with{Position=new(6,0)}:p).ToImmutableArray()};
        report=await solver.SolveAsync(cusp);Assert.False(report.Succeeded);Assert.Equal("SKETCH.TANGENCY_BRANCH",report.Diagnostic);
        Assert.Equal(new Point2d(0,0),P(d.Value,joint));
    }
    [Fact] public void EndpointDerivativeIdentityAndSplineReuseAreAtomicAndDoNotMergeInteriorControls()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Identity",RigidTransform3d.Identity));d.AddPoint(new(0,0));d.AddPoint(new(6,4));var first=d.Value.Points[0].Id;var last=d.Value.Points[1].Id;
        d.AddSpline([new(0,0),new(2,.5),new(4,1),new(6,4)],existingStart:first,existingEnd:last);var spline=d.Value.Splines[0];
        Assert.Equal(first,spline.Controls[0]);Assert.Equal(last,spline.Controls[^1]);Assert.Equal(4,d.Value.Points.Length);
        var before=d.Value;Assert.Throws<CadValidationException>(()=>d.AddSpline([new(0,0),new(2,.5),new(4,1),new(6,4)],existingStart:first,existingEnd:first));Assert.Same(before,d.Value);
        d.AddLine(new(-6,0),new(0,0),end:first);var c=new EndpointTangentConstraint(SketchConstraintId.New(),d.Value.Lines[0].Id,spline.Id,first);
        d.Change(s=>s with{Constraints=[c]});before=d.Value;d.RemoveEntities([spline.Controls[1]]);Assert.Empty(d.Value.Constraints);d.Undo();Assert.Same(before,d.Value);
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{Joint=spline.Controls[1],IsEnabled=false}]}));
        var tiny=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Tiny derivative",RigidTransform3d.Identity));tiny.AddLine(new(-1e-6,0),new(0,0));var line=tiny.Value.Lines[0];
        tiny.AddBezier(new(0,0),new(1e-8,0),new(1e-6,1e-6),existingStart:line.End);
        tiny.AddSpline([new(0,0),new(1e-8,0),new(.6e-6,.3e-6),new(1e-6,1e-6)],existingStart:line.End);
        before=tiny.Value;
        foreach(var curve in new[]{tiny.Value.Beziers[0].Id,tiny.Value.Splines[0].Id})foreach(bool enabled in new[]{true,false})
            Assert.Throws<CadValidationException>(()=>tiny.Change(s=>s with{Constraints=[new EndpointTangentConstraint(SketchConstraintId.New(),line.Id,curve,line.End,IsEnabled:enabled)]}));
        Assert.Same(before,tiny.Value);
    }
    [Theory] [InlineData("Bezier","Spline")] [InlineData("Spline","Spline")] [InlineData("Arc","Bezier")]
    public void ParametricAnalyticalJacobianMatchesIndependentCentralDifferences(string firstKind,string secondKind)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Jacobian",RigidTransform3d.Identity));d.AddPoint(new(0,0));var joint=d.Value.Points[0].Id;
        var firstPoints=firstKind=="Arc"?new Point2d[]{new(0,0),new(2,.8),new(4,4)}:
            firstKind=="Bezier"?[new(0,0),new(2,.5),new(6,4)]:[new(0,0),new(2,.5),new(4,1),new(6,4)];
        var secondPoints=secondKind=="Bezier"?new Point2d[]{new(0,0),new(-3,-.4),new(-6,4)}:[new(0,0),new(-2,-.5),new(-4,1),new(-6,4)];
        var a=AddCurve(d,firstKind,firstPoints,joint,false);var b=AddCurve(d,secondKind,secondPoints,joint,true);
        var sketch=d.Value with{Constraints=[new EndpointTangentConstraint(SketchConstraintId.New(),a,b,joint)]};
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions()],null)!;var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;var residual=type.GetMethod("Residual",flags)!;
        for(int column=0;column<x.Length;column++)
        {
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();const double step=1e-6;plus[column]+=step;minus[column]-=step;
            double numeric=(((double[])residual.Invoke(system,[plus])!)[0]-((double[])residual.Invoke(system,[minus])!)[0])/(2*step);
            Assert.InRange(Math.Abs(numeric-jac[0,column]),0,Math.Max(1,Math.Abs(numeric))*2e-6);
        }
    }
    [Theory] [InlineData("Bezier",80,92)] [InlineData("Spline",81,96)]
    public async Task ParametricTangencyDrivesExactMixedNativeVolumePreviewCancelUndoAndReopen(string kind,double originalVolume,double changedVolume)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var (d,joint,curve,_,c)=LineCurve(part,kind);
        var ends=SketchEndpointTangency.Endpoints(d.Value,curve);var other=ends.Start==joint?ends.End:ends.Start;var left=d.Value.Lines[0].Start;
        d.AddLine(new(6,4),new(-6,4),start:other);var topLeft=d.Value.Lines[^1].End;d.AddLine(new(-6,4),new(-6,0),start:topLeft,end:left);
        d.Change(s=>s with{Constraints=s.Constraints.Add(Fix(s,topLeft))});await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var source=SketchProfileReference.CreateMixed(sketch,SketchMixedLoops.Find(sketch).Single());
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Parametric join",part,sketchSource:source));
        var before=session.Snapshot;Assert.Equal(originalVolume,before.Bodies.Values.Single().Geometry.VolumeMm3,4);var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            for(int attempt=0;attempt<2;attempt++)
            {
                await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
                editor.SelectedConstraint=editor.Constraints.Single(v=>v.Id==sketch.Constraints.OfType<FixPointConstraint>().Single(f=>f.Point==left).Id);
                editor.DimensionValue=-6;editor.DimensionY=-3;editor.ApplyDimensionCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);
                Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);Assert.Contains(document.PreviewScene!.Items,i=>Math.Abs(i.Geometry.VolumeMm3-changedVolume)<1e-4);
                if(attempt==1)await editor.ConfirmCommand.ExecuteAsync(null);
            }
        }
        finally{document.Detach();}
        var after=session.Snapshot;Assert.Equal(changedVolume,after.Bodies.Values.Single().Geometry.VolumeMm3,4);AssertSmooth(after.Sketches[sketch.Id],c);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var neighbor=SketchEndpointTangency.TangentNeighbor(after.Sketches[sketch.Id],curve,joint);
        var conflict=after.Sketches[sketch.Id] with{Constraints=after.Sketches[sketch.Id].Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),neighbor,new(P(after.Sketches[sketch.Id],neighbor).X,0)))};
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(conflict,solver)));Assert.Same(after,session.Snapshot);
        var path=files.PathFor("parametric.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(changedVolume,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);AssertSmooth(loaded.Snapshot.Sketches[sketch.Id],c);
    }
    [Theory] [InlineData("Bezier")] [InlineData("Spline")]
    public async Task EditorSelectionAndVersionNineMigrationRejectNewSemanticsIncludingDisabledRelations(string kind)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var (d,joint,curve,_,relation)=LineCurve(part,kind);
        d.Change(s=>s with{Constraints=s.Constraints.Remove(relation),
            Splines=s.Splines.Select(v=>v with{Knots=null}).ToImmutableArray()});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[d.Value.Id],false);
            editor.Select(curve);editor.Select(d.Value.Lines[0].Id,true);editor.ConstraintKind="EndpointTangent";editor.AddConstraintCommand.Execute(null);
            Assert.Equal(joint,editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Single().Joint);editor.UndoCommand.Execute(null);Assert.Empty(editor.Sketch.Constraints.OfType<EndpointTangentConstraint>());
            editor.RedoCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var storage=new CadDocumentStorage();var path=files.PathFor("v10.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Single(loaded.Snapshot.Sketches[d.Value.Id].Constraints.OfType<EndpointTangentConstraint>());
        foreach(bool enabled in new[]{true,false})
        {
            var snapshot=session.Snapshot;var sketch=snapshot.Sketches[d.Value.Id];snapshot=snapshot with{Sketches=snapshot.Sketches.SetItem(sketch.Id,sketch with{Constraints=sketch.Constraints.Select(c=>c is EndpointTangentConstraint?c with{IsEnabled=enabled}:c).ToImmutableArray()})};
            await storage.SaveAsync(snapshot,assets,path);SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=9}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        }
        await storage.SaveAsync(before,assets,path);SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=9}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);SketchLegacyPointFixture.SamePoints(before.Sketches[d.Value.Id],migrated.Snapshot.Sketches[d.Value.Id]);
    }
    [Fact] public async Task VersionNineCircularTangencyStillMigratesWithItsIdentityAndBranchIntact()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Circular legacy",RigidTransform3d.Identity));d.AddLine(new(-5,0),new(0,0));var line=d.Value.Lines[0];
        d.AddArc(new(0,0),new(2,4-Math.Sqrt(12)),new(4,4),existingStart:line.End);
        var relation=new EndpointTangentConstraint(SketchConstraintId.New(),line.Id,d.Value.Arcs[0].Id,line.End);d.Change(s=>s with{Constraints=[relation]});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var storage=new CadDocumentStorage();var path=files.PathFor("circular-v9.cadoryx");await session.SaveAsync(storage,path);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=9}:s).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,assets);var sketch=loaded.Snapshot.Sketches[d.Value.Id];
        Assert.Equal(session.Snapshot.Sketches[d.Value.Id].Revision,sketch.Revision);Assert.Equal(relation,sketch.Constraints.Single());AssertSmooth(sketch,relation);
    }
}
