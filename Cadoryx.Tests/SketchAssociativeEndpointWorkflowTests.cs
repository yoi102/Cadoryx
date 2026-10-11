using System.Collections.Immutable;
using System.Globalization;
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
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Tests;

public sealed class SketchAssociativeEndpointWorkflowTests
{
    public static IEnumerable<object[]> Sources()=>from kind in Enumerable.Range(0,7) from reverse in new[]{false,true} select new object[]{kind,reverse};
    [Theory][MemberData(nameof(Sources))]
    public async Task LiveSourceEditsMoveBothRealEndpointsWithStableIdsAndIndependentNativeValues(int kind,bool reverse)
    {
        var(d,id)=SketchAssociativeEndpointTests.Source(kind,reverse);var before=d.Value;var ends=before.ExpressionCurves.Single(c=>c.Id==id).Endpoints!;
        if(kind==5)d.EditEllipse(before.Ellipses[0].Id,before.Ellipses[0].Geometry with{Center=new(2.3,-2.8)});
        else if(kind==6)d.EditFreeform(before.Freeforms[0].Id,before.Freeforms[0].Geometry with{Poles=before.Freeforms[0].Geometry.Poles.Select(p=>new Point2d(p.X+.3,p.Y+.2)).ToImmutableArray()});
        else
        {
            var sourcePoints=before.Points.Where(p=>p.Id!=ends.Start&&p.Id!=ends.End).Select(p=>p with{Position=new(p.Position.X+.3,p.Position.Y+.2)}).ToImmutableArray();
            d.Change(s=>s with{Constraints=[..sourcePoints.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),..s.Constraints]});
        }
        var candidate=d.Value;
        if(kind<=4)Assert.False(new ManagedSketchConstraintSolver().Analyze(candidate).IsSatisfied);
        else Assert.Throws<CadValidationException>(()=>SketchAssociativeEndpoints.RequireSolved(candidate));
        var report=await new ManagedSketchConstraintSolver().SolveAsync(candidate);Assert.True(report.Succeeded,report.Diagnostic);var solved=report.Solution!;
        var c=solved.ExpressionCurves.Single(c=>c.Id==id);Assert.Equal(ends,c.Endpoints);SketchAssociativeEndpoints.RequireSolved(solved);SketchExpressionEntities.ValidateSolved(candidate,solved);
        foreach(var pair in new[]{(ends.Start,0d),(ends.End,1d)})
        {
            var previous=before.Points.Single(p=>p.Id==pair.Item1).Position;var actual=solved.Points.Single(p=>p.Id==pair.Item1).Position;
            SketchAssociativeEndpointTests.Near(new(previous.X+.3,previous.Y+.2),actual);
            SketchAssociativeEndpointTests.Near(new OcctGeometryKernel().EvaluateExpression(c.Geometry,pair.Item2).Point,actual);
        }
        foreach(var pin in candidate.Constraints.OfType<FixPointConstraint>())Assert.Equal(pin.Position,solved.Points.Single(p=>p.Id==pin.Point).Position);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RadiusDriverMovesClosedSeamEndpointsWithoutMergingTheirStableIdentities(bool reverse)
    {
        var(d,id)=SketchAssociativeEndpointTests.Source(1,reverse);var original=d.Value;var e=original.ExpressionCurves.Single().Endpoints!;
        d.Change(s=>s with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),s.Circles[0].Center,new(2,3)),
            new RadiusConstraint(SketchConstraintId.New(),s.Circles[0].Id,5.2)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);var solved=report.Solution!;
        Assert.NotEqual(e.Start,e.End);Assert.Equal(5.2,solved.Circles[0].Radius);SketchAssociativeEndpoints.RequireSolved(solved);
        var a=solved.Points.Single(p=>p.Id==e.Start).Position;var b=solved.Points.Single(p=>p.Id==e.End).Position;
        SketchAssociativeEndpointTests.Near(new(7.23,3),a);SketchAssociativeEndpointTests.Near(a,b);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FrozenDetachedAndResumedSourcesKeepEndpointEquationsAndLocalHistory(bool detach)
    {
        var(d,id)=SketchAssociativeEndpointTests.Source(0);var before=d.Value;var e=before.ExpressionCurves.Single().Endpoints!;
        if(detach)d.DetachExpression(id);else d.FreezeExpression(id);
        d.Change(s=>s with{Constraints=s.Points.Where(p=>p.Id!=e.Start&&p.Id!=e.End).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,new(p.Position.X+.3,p.Position.Y+.2))).ToImmutableArray()});
        var result=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(result.Succeeded,result.Diagnostic);d.UseSolvedCoordinates(result.Solution!);
        SketchAssociativeEndpointTests.Near(before.Points.Single(p=>p.Id==e.Start).Position,d.Value.Points.Single(p=>p.Id==e.Start).Position);
        if(!detach)
        {
            var frozen=d.Value;d.ResumeExpression(id);var resumed=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(resumed.Succeeded,resumed.Diagnostic);
            Assert.Equal(e,resumed.Solution!.ExpressionCurves.Single().Endpoints);SketchAssociativeEndpoints.RequireSolved(resumed.Solution);
            Assert.False(frozen.ExpressionCurves.Single().Geometry.SameValues(resumed.Solution.ExpressionCurves.Single().Geometry));d.Undo();Assert.Same(frozen,d.Value);d.Redo();
        }
        else Assert.Null(d.Value.ExpressionCurves.Single().Source);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitExpressionAndLineClosureKeepsDirectionAndRealFaceAndSolid(bool reverse)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),3);d.SetConstruction([source],true);var id=d.AddExactOffset(source,.03);
        if(reverse)d.EditExpression(id,d.Value.ExpressionCurves.Single().Geometry.Reverse());d.SetConstruction([id],false);
        var c=d.Value.ExpressionCurves.Single();var e=c.Endpoints!;d.AddLine(c.Geometry.At(1),c.Geometry.At(0),e.End,e.Start);var line=d.Value.Lines[^1].Id;
        var solved=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(solved.Succeeded,solved.Diagnostic);var s=solved.Solution!;
        Assert.Contains(SketchMixedLoops.Find(s),loop=>loop.ToHashSet().SetEquals([id,line]));
        var profile=SketchProfileBuilder.Mixed(s,[line,id]);profile.Validate();Assert.Contains(profile.BoundaryCurves,b=>b.Expression is not null);
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();var region=kernel.InspectRegion(profile.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray());
        using(var result=await kernel.EvaluateAsync(new ExtrudeRecipe(profile,3,s.Plane),assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-region.Area*3),0,1e-7);
        Assert.Equal(0,assets.Count);d.Undo();Assert.DoesNotContain(d.Value.Lines,l=>l.Id==line);d.Redo();Assert.Equal(line,d.Value.Lines[^1].Id);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task TypedExpressionHoleAndIslandReferencesRemainAvailableAndNativeMaterialAlternates(bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Nested",RigidTransform3d.Identity));var ids=new List<SketchEntityId>();
        foreach(double radius in new[]{4d,2d,1d})
        {
            d.AddCircle(new(0,0),radius,construction:true);var id=d.AddExactOffset(d.Value.Circles[^1].Id,.1);ids.Add(id);
            if(reverse)d.EditExpression(id,d.Value.ExpressionCurves.Single(c=>c.Id==id).Geometry.Reverse());d.SetConstruction([id],false);
        }
        var r=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(r.Succeeded,r.Diagnostic);var s=r.Solution!;
        var reference=SketchProfileReference.CreateMixed(s,[ids[0]]) with{MixedHoleIds=[[ids[1]]],IslandMixedIds=[[ids[2]]]};
        var(doc,_)=SketchCommandTests.Document();doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        Assert.False(reference.HasUnavailableEntities(doc));
        var profile=SketchProfileBuilder.Mixed(s,[ids[0]]) with{MixedHoles=[SketchProfileBuilder.Mixed(s,[ids[1]]).BoundaryCurves],
            Islands=[new([],null,SketchProfileBuilder.Mixed(s,[ids[2]]).BoundaryCurves)]};
        var assets=new MemoryAssetStore();using(var result=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,3,s.Plane),assets))
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-3*Math.PI*(4.1*4.1-2.1*2.1+1.1*1.1)),0,1e-7);
        Assert.Equal(0,assets.Count);var edit=new SketchDraft(s);edit.SetConstruction([ids[1]],true);
        Assert.True(reference.HasUnavailableEntities(doc with{Sketches=doc.Sketches.SetItem(s.Id,edit.Value)}));
    }
    [Theory][InlineData("point")][InlineData("identity")][InlineData("equation")][InlineData("missing")]
    public async Task InjectedEndpointCoordinatesOrRolesAreAtomicallyRejectedBeforeAnyBodyCanPublish(string mutation)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var(d,source)=SketchExpressionEntityTests.Source(part,3);var id=d.AddExactOffset(source,.03);d.SetConstruction([id],false);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ForgedSolver(mutation))));Assert.Same(initial,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class ForgedSolver(string mutation):ISketchConstraintSolver
    {
        public string Version=>"test-forged-endpoints";
        public async Task<SketchSolveReport> SolveAsync(CadSketch s,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var r=await new ManagedSketchConstraintSolver().SolveAsync(s,options,cancellationToken);s=r.Solution!;var c=s.ExpressionCurves.Single();var e=c.Endpoints!;
            if(mutation=="point")s=s with{Points=s.Points.Select(p=>p.Id==e.Start?p with{Position=new(p.Position.X+.01,p.Position.Y)}:p).ToImmutableArray()};
            else s=s with{ExpressionCurves=[c with{Endpoints=mutation switch{"missing"=>null,"identity"=>e with{Start=e.End,End=e.Start},_=>e with{StartEquation=SketchConstraintId.New()}}}]};
            return r with{Solution=s};
        }
    }
    [Fact] public async Task SharedEndpointProvidersAreExplicitAndOtherSourceThroughTheirPointCycleIsRejected()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Explicit shared endpoints",RigidTransform3d.Identity));d.AddLine(new(0,0),new(4,0),construction:true);
        var a=d.AddExactOffset(d.Value.Lines[0].Id,.1);d.AttachExpressionEndpoints(a);var e=d.Value.ExpressionCurves.Single().Endpoints!;
        d.AddLine(new(4,0),new(8,0),construction:true);var b=d.AddExactOffset(d.Value.Lines[1].Id,.1);d.AttachExpressionEndpoints(b,e.End);
        Assert.Equal(e.End,d.Value.ExpressionCurves.Single(c=>c.Id==b).Endpoints!.Start);
        var result=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(result.Succeeded,result.Diagnostic);SketchAssociativeEndpoints.RequireSolved(result.Solution!);
        var before=d.Value;var line=before.Lines[0];
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Lines=s.Lines.Replace(line,line with{End=e.End})}));Assert.Same(before,d.Value);
        d.FreezeExpression(a);d.FreezeExpression(b);d.Change(s=>s with{Lines=s.Lines.Replace(line,line with{End=e.End})});
        var frozen=d.Value;Assert.Throws<CadValidationException>(()=>d.ResumeExpression(a));Assert.Same(frozen,d.Value);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task TwoExpressionAuthorsShareExplicitSmoothSeamWithDirectedG1AndG2(bool reverse,bool curvature)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Expression seam",RigidTransform3d.Identity));d.AddCircle(new(0,0),2,construction:true);
        var a=d.AddExactOffset(d.Value.Circles[0].Id,.1);d.EditExpression(a,d.Value.ExpressionCurves.Single().Geometry.Trim(0,.5));
        if(reverse)d.EditExpression(a,d.Value.ExpressionCurves.Single().Geometry.Reverse());d.AttachExpressionEndpoints(a);var e=d.Value.ExpressionCurves.Single().Endpoints!;
        var b=d.AddExactOffset(d.Value.Circles[0].Id,.1);d.EditExpression(b,d.Value.ExpressionCurves.Single(c=>c.Id==b).Geometry.Trim(.5,1));
        if(reverse)d.EditExpression(b,d.Value.ExpressionCurves.Single(c=>c.Id==b).Geometry.Reverse());d.AttachExpressionEndpoints(b,e.End);
        d.Change(s=>s with{Constraints=s.Constraints.Add(curvature?new EndpointCurvatureConstraint(SketchConstraintId.New(),a,b,e.End):new EndpointTangentConstraint(SketchConstraintId.New(),a,b,e.End))});
        var r=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(r.Succeeded,r.Diagnostic);SketchAssociativeEndpoints.RequireSolved(r.Solution!);SketchContactSolutionVerification.Validate(d.Value,r.Solution!);
        var ga=new OcctGeometryKernel().EvaluateExpression(r.Solution!.ExpressionCurves.Single(c=>c.Id==a).Geometry,1);
        var gb=new OcctGeometryKernel().EvaluateExpression(r.Solution.ExpressionCurves.Single(c=>c.Id==b).Geometry,0);SketchAssociativeEndpointTests.Near(ga.Point,gb.Point);
        double ka=(ga.First.X*ga.Second.Y-ga.First.Y*ga.Second.X)/Math.Pow(double.Hypot(ga.First.X,ga.First.Y),3);
        double kb=(gb.First.X*gb.Second.Y-gb.First.Y*gb.Second.X)/Math.Pow(double.Hypot(gb.First.X,gb.First.Y),3);Assert.InRange(Math.Abs(ka-kb),0,1e-7);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DeletingEndpointOrCurveDropsOnlyItsExplicitRelationsAndRejectsLiveDescendantRemoval(bool point)
    {
        var s=SketchAssociativeEndpointTests.Pair(3,false,true,true);var d=new SketchDraft(s);var c=s.ExpressionCurves.Single();var e=c.Endpoints!;
        d.RemoveEntities([point?e.Start:c.Id]);Assert.Empty(d.Value.ExpressionCurves);Assert.DoesNotContain(d.Value.Constraints,r=>SketchConstraintEditing.Targets(r).Contains(c.Id));
        d.Undo();Assert.Same(s,d.Value);var child=d.AddExactOffset(c.Id,.01);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.RemoveEntities([c.Id]));Assert.Same(before,d.Value);Assert.Equal(child,d.Value.ExpressionCurves[^1].Id);
    }
    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task EditorCreatesEditsSelectsAndPreviewsAssociativeAuthorsWithoutPublishingUntilConfirmation(string language)
    {
        var old=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
            {
                var d=new SketchDraft(CadSketch.Create(part,"Editor",RigidTransform3d.Identity));d.AddCircle(new(8,1),2,construction:true);
                var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());try
                {
                    await using var editor=new SketchEditorViewModel(doc,kernel,new ManagedSketchConstraintSolver(),d.Value,true){OffsetDistance=.03};
                    editor.Select(d.Value.Circles[0].Id);editor.CreateExactOffsetCommand.Execute(null);var c=editor.Sketch.ExpressionCurves.Single();
                    Assert.False(c.IsConstruction);Assert.NotNull(c.Endpoints);Assert.True(editor.HasSelectedExpression);Assert.Contains(editor.Entities,e=>e.Id==c.Id&&e.Label.Contains(Strings.ExactAssociatedOffset));
                    Assert.Equal(Strings.ExpressionSourceActive,editor.ExpressionSourceStatus);editor.ExpressionDistance=.08;
                    await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Equal(.08,editor.Sketch.ExpressionCurves.Single().Geometry.SignedOffset);Assert.Same(initial,session.Snapshot);
                    editor.FreezeExpressionCommand.Execute(null);Assert.Equal(Strings.ExpressionSourceFrozen,editor.ExpressionSourceStatus);editor.ResumeExpressionCommand.Execute(null);Assert.Equal(Strings.ExpressionSourceActive,editor.ExpressionSourceStatus);
                    editor.DetachExpressionCommand.Execute(null);Assert.Equal(Strings.ExpressionSourceDetached,editor.ExpressionSourceStatus);editor.UndoCommand.Execute(null);Assert.NotNull(editor.Sketch.ExpressionCurves.Single().Source);
                    editor.Select(c.Id);editor.ReverseExpressionCommand.Execute(null);editor.ExpressionFirstParameter=.1;editor.ExpressionLastParameter=.9;editor.KeepExpressionIntervalCommand.Execute(null);
                    Assert.True(editor.Sketch.ExpressionCurves.Single().Geometry.Reversed);Assert.Equal(c.Endpoints,editor.Sketch.ExpressionCurves.Single().Endpoints);
                    await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);SketchAssociativeEndpoints.RequireSolved(editor.Sketch);
                    editor.CancelCommand.Execute(null);Assert.Same(initial,session.Snapshot);Assert.Null(doc.PreviewScene);
                }finally{doc.Detach();}
            }
            Assert.Equal(0,assets.Count);
        }finally{CultureInfo.CurrentUICulture=old;}
    }
}
