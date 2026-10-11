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
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchEndpointTangencyTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    private static FixPointConstraint Fix(CadSketch s,SketchEntityId id)=>new(SketchConstraintId.New(),id,P(s,id));
    private static (SketchDraft Draft,SketchEntityId Joint,SketchEntityId Other) LineArc(DefinitionId part,bool reverseLine=false,bool reverseArc=false,double translation=0)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Endpoint tangency",RigidTransform3d.Identity));
        var joint=new Point2d(translation,translation);var left=new Point2d(translation-5,translation);
        d.AddLine(reverseLine?joint:left,reverseLine?left:joint);var line=d.Value.Lines[0];var id=reverseLine?line.Start:line.End;
        var end=new Point2d(translation+4,translation+4);var mid=new Point2d(translation+2,translation+.8);
        d.AddArc(reverseArc?end:joint,mid,reverseArc?joint:end,existingStart:reverseArc?null:id,existingEnd:reverseArc?id:null);
        var arc=d.Value.Arcs[0];var other=arc.Start==id?arc.End:arc.Start;
        d.Change(s=>s with{Constraints=[Fix(s,line.Start),Fix(s,line.End),Fix(s,other),
            new OffsetXConstraint(SketchConstraintId.New(),id,arc.Middle,2)]});
        return(d,id,other);
    }
    private static EndpointTangentConstraint AddTangency(SketchDraft d,SketchEntityId joint)
    {
        var c=new EndpointTangentConstraint(SketchConstraintId.New(),d.Value.Lines[0].Id,d.Value.Arcs[0].Id,joint);
        d.Change(s=>s with{Constraints=s.Constraints.Add(c)});return c;
    }
    private static Point2d Tangent(CadSketch s,SketchEntityId curve,SketchEntityId joint)
    {
        var endpoint=P(s,joint);Point2d tangent;
        if(s.Lines.SingleOrDefault(l=>l.Id==curve) is {} line)
        {var other=P(s,line.Start==joint?line.End:line.Start);tangent=new(other.X-endpoint.X,other.Y-endpoint.Y);}
        else
        {
            var arc=s.Arcs.Single(a=>a.Id==curve);var circle=SketchArcGeometry.Through(P(s,arc.Start),P(s,arc.Middle),P(s,arc.End));
            var radius=new Point2d(endpoint.X-circle.Center.X,endpoint.Y-circle.Center.Y);
            double direction=Math.Sign(circle.SweepAngle)*(arc.Start==joint?1:-1);
            tangent=new(-radius.Y*direction,radius.X*direction);
        }
        double length=double.Hypot(tangent.X,tangent.Y);return new(tangent.X/length,tangent.Y/length);
    }
    private static void AssertSmooth(CadSketch s,EndpointTangentConstraint c)
    {
        var a=Tangent(s,c.A,c.Joint);var b=Tangent(s,c.B,c.Joint);
        Assert.InRange(Math.Abs(a.X*b.Y-a.Y*b.X),0,2e-9);Assert.InRange(a.X*b.X+a.Y*b.Y,-1.0000000001,-.9999999999);
    }
    [Theory] [InlineData(false,false,0)] [InlineData(true,false,0)] [InlineData(false,true,0)] [InlineData(true,true,0)] [InlineData(true,true,1000000)]
    public async Task LineArcSolvesExactEndpointTangencyRegardlessOfCurveDirection(bool reverseLine,bool reverseArc,double translation)
    {
        var (d,joint,other)=LineArc(DefinitionId.New(),reverseLine,reverseArc,translation);var c=AddTangency(d,joint);
        var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(d.Value);
        Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(0,report.DegreesOfFreedom);Assert.Equal(8,report.EquationCount);
        AssertSmooth(report.Solution!,c);Assert.Equal(translation+4-Math.Sqrt(12),P(report.Solution!,d.Value.Arcs[0].Middle).Y,6);
        Assert.Equal(d.Value.Arcs[0],report.Solution!.Arcs[0]);
        var moved=d.Value with{Constraints=d.Value.Constraints.Select(c=>c is FixPointConstraint f&&f.Point==other?f with{Position=new(translation+6,translation+4)}:c).ToImmutableArray()};
        report=await solver.SolveAsync(moved);Assert.True(report.Succeeded,report.Diagnostic);AssertSmooth(report.Solution!,c);
        Assert.Equal(translation+6.5-Math.Sqrt(38.25),P(report.Solution!,d.Value.Arcs[0].Middle).Y,6);
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task ArcArcSolvesWithDistinctRadiiAndAllSharedEndpointOrientations(bool reverseFirst,bool reverseSecond)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Arc joins",RigidTransform3d.Identity));
        var joint=new Point2d(0,0);var firstEnd=new Point2d(4,4);var firstMid=new Point2d(2,4-Math.Sqrt(12));
        d.AddArc(reverseFirst?firstEnd:joint,firstMid,reverseFirst?joint:firstEnd);var a=d.Value.Arcs[0];var id=reverseFirst?a.End:a.Start;
        var secondEnd=new Point2d(-6,3);var secondMid=new Point2d(-2,.5);
        d.AddArc(reverseSecond?joint:secondEnd,secondMid,reverseSecond?secondEnd:joint,existingStart:reverseSecond?id:null,existingEnd:reverseSecond?null:id);
        var b=d.Value.Arcs[1];var c=new EndpointTangentConstraint(SketchConstraintId.New(),b.Id,a.Id,id);
        d.Change(s=>s with{Constraints=[Fix(s,a.Start),Fix(s,a.Middle),Fix(s,a.End),Fix(s,b.Start==id?b.End:b.Start),
            new OffsetXConstraint(SketchConstraintId.New(),id,b.Middle,-2),c]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(0,report.DegreesOfFreedom);AssertSmooth(report.Solution!,c);Assert.Equal(7.5-Math.Sqrt(52.25),P(report.Solution!,b.Middle).Y,6);
    }
    [Fact] public async Task MajorArcRetainsItsAuthoredSweepAndCuspIsNeverAcceptedAsTangency()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Major arc",RigidTransform3d.Identity));d.AddLine(new(0,0),new(5,0));var line=d.Value.Lines[0];
        d.AddArc(new(0,0),new(-3.8,4),new(4,4),existingStart:line.Start);var arc=d.Value.Arcs[0];
        var c=AddTangency(d,line.Start);d.Change(s=>s with{Constraints=[Fix(s,line.Start),Fix(s,line.End),Fix(s,arc.End),
            new OffsetYConstraint(SketchConstraintId.New(),line.Start,arc.Middle,4),c]});
        var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);AssertSmooth(report.Solution!,c);
        Assert.Equal(-4,P(report.Solution!,arc.Middle).X,6);Assert.True(Math.Abs(SketchArcGeometry.Through(P(report.Solution!,arc.Start),P(report.Solution!,arc.Middle),P(report.Solution!,arc.End)).SweepAngle)>Math.PI);
        var cusp=report.Solution! with{Points=report.Solution!.Points.Select(p=>p.Id==line.End?p with{Position=new(-5,0)}:p).ToImmutableArray()};
        report=await solver.SolveAsync(cusp);Assert.False(report.Succeeded);Assert.Equal("SKETCH.TANGENCY_BRANCH",report.Diagnostic);
        var disabled=cusp with{Constraints=cusp.Constraints.Select(v=>v.Id==c.Id?v with{IsEnabled=false}:v).ToImmutableArray()};
        Assert.True((await solver.SolveAsync(disabled)).Succeeded);
    }
    [Fact] public async Task ConflictBudgetDisableAndDuplicateDiagnosticsRetainTheirMeaning()
    {
        var (d,joint,_)=LineArc(DefinitionId.New());var c=AddTangency(d,joint);var solver=new ManagedSketchConstraintSolver();
        Assert.Equal(SketchSolveStatus.LimitExceeded,(await solver.SolveAsync(d.Value,new(MaxEquations:7))).Status);
        var disabled=d.Value with{Constraints=d.Value.Constraints.Select(v=>v.Id==c.Id?v with{IsEnabled=false}:v).ToImmutableArray()};
        Assert.Equal(1,(await solver.SolveAsync(disabled)).DegreesOfFreedom);
        var conflict=d.Value with{Constraints=d.Value.Constraints.Add(Fix(d.Value,d.Value.Arcs[0].Middle))};
        var failed=await solver.SolveAsync(conflict);Assert.Equal(SketchSolveStatus.DidNotConverge,failed.Status);Assert.Null(failed.Solution);
        Assert.Empty(failed.ConflictingConstraints);Assert.Contains(failed.Residuals,r=>r.ConstraintId==c.Id&&r.NormalizedResidual>1);
        var duplicate=d.Value with{Constraints=d.Value.Constraints.Add(c with{Id=SketchConstraintId.New()})};
        var report=await solver.SolveAsync(duplicate);Assert.True(report.Succeeded);Assert.Equal(0,report.DegreesOfFreedom);Assert.NotEmpty(report.RedundantConstraints);
    }
    [Fact] public void TypedIdentityRequiresOneSharedEndpointAndEditingCleansRelationsAtomically()
    {
        var (d,joint,_)=LineArc(DefinitionId.New());var c=AddTangency(d,joint);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{Joint=s.Arcs[0].Middle}]}));
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{Branch=(EndpointTangentBranch)2,IsEnabled=false}]}));Assert.Same(before,d.Value);
        d.RemoveEntities([joint]);Assert.Empty(d.Value.Constraints.OfType<EndpointTangentConstraint>());Assert.Empty(d.Value.Arcs);d.Undo();Assert.Same(before,d.Value);
        d.AddLine(new(0,0),new(-4,-1));var unshared=d.Value.Lines[^1];
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{A=unshared.Id}]}));
        d.AddCircle(new(0,0),2);Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{B=s.Circles[0].Id}]}));
        d.AddArc(new(0,0),new(1,-2),new(4,4),existingStart:joint,existingEnd:before.Arcs[0].End);
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=[c with{A=s.Arcs[0].Id,B=s.Arcs[1].Id}]}));
        var trimmed=new SketchDraft(before);trimmed.AddLine(new(-2,-2),new(-2,2));trimmed.TrimLine(before.Lines[0].Id,new(-4,0));
        Assert.Empty(trimmed.Value.Constraints.OfType<EndpointTangentConstraint>());trimmed.Undo();Assert.Single(trimmed.Value.Constraints.OfType<EndpointTangentConstraint>());
        Assert.Throws<CadValidationException>(()=>new SketchDraft(before).MovePoint(before.Arcs[0].Middle,new(2,2)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AnalyticalJacobianMatchesIndependentCentralDifferencesIncludingTheSharedPoint(bool twoArcs)
    {
        var (d,joint,_)=LineArc(DefinitionId.New());var c=AddTangency(d,joint);
        if(twoArcs)
        {
            d.AddArc(new(-5,0),new(-3,-1),new(0,0),existingStart:d.Value.Lines[0].Start,existingEnd:joint);
            c=c with{A=d.Value.Arcs[1].Id};
        }
        var sketch=d.Value with{Constraints=[c]};var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions()],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();const double step=1e-6;plus[col]+=step;minus[col]-=step;
            double numeric=(((double[])residual.Invoke(system,[plus])!)[0]-((double[])residual.Invoke(system,[minus])!)[0])/(2*step);
            Assert.InRange(Math.Abs(numeric-jac[0,col]),0,Math.Max(1,Math.Abs(numeric))*2e-6);
        }
    }
    [Fact] public async Task EditorPreviewCommitAssociatedNativeVolumeUndoAndReopenRemainAtomic()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var (d,joint,other)=LineArc(part);var c=AddTangency(d,joint);var left=d.Value.Lines[0].Start;
        d.AddLine(new(4,4),new(-5,4),start:other);var topLeft=d.Value.Lines[^1].End;
        d.AddLine(new(-5,4),new(-5,0),start:topLeft,end:left);d.Change(s=>s with{Constraints=s.Constraints.Add(Fix(s,topLeft))});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var reference=SketchProfileReference.CreateMixed(sketch,SketchMixedLoops.Find(sketch).Single());
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Smooth corner",part,sketchSource:reference));
        var before=session.Snapshot;Assert.Equal(40+8*Math.PI,before.Bodies.Values.Single().Geometry.VolumeMm3,5);var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        double expected=25+42.25*Math.Asin(12d/13);
        try
        {
            await using(var cancel=new SketchEditorViewModel(document,kernel,solver,sketch,false))
            {
                cancel.SelectedConstraint=cancel.Constraints.Single(v=>v.Id==sketch.Constraints.OfType<FixPointConstraint>().Single(f=>f.Point==other).Id);
                cancel.DimensionValue=6;cancel.DimensionY=4;cancel.ApplyDimensionCommand.Execute(null);await cancel.PreviewCommand.ExecuteAsync(null);
                Assert.True(cancel.CanConfirm,cancel.Status);Assert.Same(before,session.Snapshot);Assert.Contains(document.PreviewScene!.Items,i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5);
            }
            Assert.Same(before,session.Snapshot);
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
            editor.SelectedConstraint=editor.Constraints.Single(v=>v.Id==sketch.Constraints.OfType<FixPointConstraint>().Single(f=>f.Point==other).Id);
            editor.DimensionValue=6;editor.DimensionY=4;editor.ApplyDimensionCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;Assert.Equal(expected,after.Bodies.Values.Single().Geometry.VolumeMm3,5);AssertSmooth(after.Sketches[sketch.Id],c);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var conflict=after.Sketches[sketch.Id] with{Constraints=after.Sketches[sketch.Id].Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),sketch.Arcs[0].Middle,new(2,.8)))};
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(conflict,solver)));Assert.Same(after,session.Snapshot);
        var path=files.PathFor("smooth-corner.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(expected,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);AssertSmooth(loaded.Snapshot.Sketches[sketch.Id],c);
        Assert.Equal(c,loaded.Snapshot.Sketches[sketch.Id].Constraints.OfType<EndpointTangentConstraint>().Single());
    }
    [Fact] public async Task UnorderedEditorSelectionStorageMigrationAndMalformedBranchesAreValidated()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var (d,joint,_)=LineArc(part);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[d.Value.Id],false);
            editor.Select(d.Value.Arcs[0].Id);editor.Select(d.Value.Lines[0].Id,true);editor.ConstraintKind="EndpointTangent";editor.AddConstraintCommand.Execute(null);
            var c=editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Single();Assert.Equal(joint,c.Joint);
            editor.UndoCommand.Execute(null);Assert.Empty(editor.Sketch.Constraints.OfType<EndpointTangentConstraint>());editor.RedoCommand.Execute(null);
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var storage=new CadDocumentStorage();var path=files.PathFor("tangency.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Single(loaded.Snapshot.Sketches[d.Value.Id].Constraints.OfType<EndpointTangentConstraint>());
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=8}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        var disabled=session.Snapshot with{Sketches=session.Snapshot.Sketches.SetItem(d.Value.Id,session.Snapshot.Sketches[d.Value.Id] with{Constraints=session.Snapshot.Sketches[d.Value.Id].Constraints.Select(c=>c is EndpointTangentConstraint?c with{IsEnabled=false}:c).ToImmutableArray()})};
        await storage.SaveAsync(disabled,assets,path);SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=8}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        foreach(string mutation in new[]{"branch","joint","arguments"})
        {
            await session.SaveAsync(storage,path);FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
            {
                var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var constraints=pack.Sketches[0].Constraints;int i=Array.FindIndex(constraints,c=>c.Kind=="endpoint-tangent-curves");
                constraints[i]=mutation switch{"branch"=>constraints[i] with{Values=[1.5]},"joint"=>constraints[i] with{Targets=[..constraints[i].Targets.Take(2),d.Value.Arcs[0].Middle.Value]},_=>constraints[i] with{Values=[]}};
                return MessagePackSerializer.Serialize(pack);
            });await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        }
        await storage.SaveAsync(before,assets,path);SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=8}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);SketchLegacyPointFixture.SamePoints(before.Sketches[d.Value.Id],migrated.Snapshot.Sketches[d.Value.Id]);
    }
}
