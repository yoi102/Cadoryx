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

public sealed class SketchExpressionCurveEditingTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static SketchExpressionCurve Curve(SketchDraft d,SketchEntityId id)=>d.Value.ExpressionCurves.Single(c=>c.Id==id);
    private static void Near(Point2d expected,Point2d actual)=>Assert.InRange(double.Hypot(actual.X-expected.X,actual.Y-expected.Y),0,1e-8);
    public static IEnumerable<object[]> Families()=>from kind in Enumerable.Range(0,8) from reverse in new[]{false,true} select new object[]{kind,reverse};
    [Theory][MemberData(nameof(Families))]
    public void FiniteTrimPreservesCapturedAuthorAndReplacesOnlyChangedEndpointIdentities(int kind,bool reverse)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),kind);var id=d.AddExactOffset(source,.075);
        if(reverse)d.EditExpression(id,Curve(d,id).Geometry.Reverse());d.SetExpressionConstruction(id,false);
        var before=d.Value;var c=Curve(d,id);var old=c.Endpoints!;var oldStart=before.Points.Single(p=>p.Id==old.Start);
        d.TrimExpression(id,.2,1,Kernel);var after=d.Value;var trimmed=Curve(d,id);var ends=trimmed.Endpoints!;
        Assert.Equal(id,trimmed.Id);Assert.Equal(c.Source,trimmed.Source);Assert.True(trimmed.Geometry.Basis!.SameValues(c.Geometry.Basis!));
        Assert.NotEqual(old.Start,ends.Start);Assert.NotEqual(old.StartEquation,ends.StartEquation);Assert.Equal(old.End,ends.End);Assert.Equal(old.EndEquation,ends.EndEquation);
        Assert.DoesNotContain(oldStart,after.Points);Assert.Equal(SketchPointOrigin.CurveGenerated,after.Points.Single(p=>p.Id==ends.Start).Origin);
        foreach(double t in new[]{0d,.13,.51,1})Near(Kernel.EvaluateExpression(c.Geometry,.2+.8*t).Point,trimmed.Geometry.At(t));
        SketchAssociativeEndpoints.RequireSolved(after);d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SplitCreatesTwoAuthorsAndNewStructuralRowsWhileRetainingUnchangedOuterPoints(bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Split",RigidTransform3d.Identity));
        var g=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0))).Offset(.1);if(reverse)g=g.Reverse();
        var id=d.AddExpression(g);d.SetExpressionConstruction(id,false);var c=Curve(d,id);var old=c.Endpoints!;
        d.RemoveExpressionInterval(id,.25,.75,Kernel);var result=d.Value.ExpressionCurves;Assert.Equal(2,result.Length);Assert.DoesNotContain(result,c=>c.Id==id);
        Assert.Equal(old.Start,result[0].Endpoints!.Start);Assert.Equal(old.End,result[1].Endpoints!.End);
        Assert.All(result,c=>{Assert.NotEqual(old.StartEquation,c.Endpoints!.StartEquation);Assert.NotEqual(old.EndEquation,c.Endpoints.EndEquation);});
        Near(reverse?new(6,-.1):new(2,-.1),result[0].Geometry.At(1));Near(reverse?new(2,-.1):new(6,-.1),result[1].Geometry.At(0));
        Assert.True(result[0].Geometry.Basis!.SameValues(g.Basis!));SketchAssociativeEndpoints.RequireSolved(d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SplitWithAnUnfrozenDependentRejectsWithoutGuessingAReplacement(bool freeze)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.1);var child=d.AddExactOffset(id,.1);
        if(freeze)d.FreezeExpression(child);var before=d.Value;
        if(freeze){d.RemoveExpressionInterval(id,.25,.75,Kernel);Assert.True(Curve(d,child).Source!.IsFrozen);Assert.True(Curve(d,child).Geometry.SameValues(before.ExpressionCurves.Single(c=>c.Id==child).Geometry));}
        else{Assert.Throws<CadValidationException>(()=>d.RemoveExpressionInterval(id,.25,.75,Kernel));Assert.Same(before,d.Value);}
    }
    public static IEnumerable<object[]> Extensions()=>from reverse in new[]{false,true} from start in new[]{false,true} select new object[]{reverse,start};
    [Theory][MemberData(nameof(Extensions))]
    public void ParabolaContinuationMatchesIndependentNormalFormulaInBothDirections(bool reverse,bool start)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Continue",RigidTransform3d.Identity));
        var basis=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(1,1)){BezierControl=new(.5,0)});if(reverse)basis=basis.Reverse();
        var id=d.AddExpression(basis.Offset(.075));d.SetExpressionConstruction(id,false);var before=Curve(d,id);var ends=before.Endpoints!;
        d.ExtendExpression(id,start,.25,Kernel);var actual=Curve(d,id);var g=actual.Geometry;
        Assert.True(g.Basis!.SameValues(before.Geometry));Assert.Equal(start?ends.End:ends.Start,start?actual.Endpoints!.End:actual.Endpoints!.Start);
        Assert.Equal(start?ends.EndEquation:ends.StartEquation,start?actual.Endpoints!.EndEquation:actual.Endpoints!.StartEquation);
        double a=start!=reverse?-.25:0,b=start!=reverse?1:1.25,distance=reverse?-.075:.075;
        foreach(double t in new[]{0d,.2,.7,1})
        {double u=a+(b-a)*(reverse?1-t:t),q=Math.Sqrt(1+4*u*u);Near(new(u+2*distance*u/q,u*u-distance/q),g.At(t));}
        SketchAssociativeEndpoints.RequireSolved(d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LinkedContinuationMovesItsCapturedBasisPathExactlyOneLevelAndKeepsItRefreshable(bool start)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.1);var before=Curve(d,id);
        d.ExtendExpression(id,start,.25,Kernel);var after=Curve(d,id);Assert.True(SketchExpressionOperationProof.IsSingleContinuation(before,after));
        Assert.Equal(before.Source!.BasisDepth+1,after.Source!.BasisDepth);Assert.True(after.Source.Geometry.SameValues(before.Source.Geometry));
        d.MovePoint(d.Value.Lines.Single().End,new(10,5));Assert.True(Curve(d,id).Source!.Geometry.SameValues(SketchExpressionEntities.Author(d.Value,source).Geometry));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NativeTrimAcrossCircleSeamKeepsTheExactComplement(bool reverse)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Periodic trim",RigidTransform3d.Identity));d.AddCircle(new(0,0),5,construction:true);
        var id=d.AddExactOffset(d.Value.Circles.Single().Id,.2);if(reverse)d.EditExpression(id,Curve(d,id).Geometry.Reverse());d.SetExpressionConstruction(id,false);
        d.AddLine(new(0,-8),new(0,8),construction:true);var before=Curve(d,id);
        d.TrimExpressionAt(id,new(-5.2,0),Kernel,Kernel);var after=Curve(d,id);
        Assert.True(SketchExpressionOperationProof.IsPeriodicComplement(before,after));Assert.Equal(before.Source!.BasisDepth+1,after.Source!.BasisDepth);
        Near(new(5.2,0),after.Geometry.At(.5));Near(reverse?new(0,5.2):new(0,-5.2),after.Geometry.At(0));Near(reverse?new(0,-5.2):new(0,5.2),after.Geometry.At(1));
        var face=Kernel.InspectRegion([new(after.Geometry.At(0),after.Geometry.At(1),Expression:after.Geometry),new(after.Geometry.At(1),after.Geometry.At(0))]);
        Assert.InRange(Math.Abs(face.Area-Math.PI*5.2*5.2/2),0,1e-7);SketchAssociativeEndpoints.RequireSolved(d.Value);
    }
    [Theory][MemberData(nameof(Families))]
    public void ExactOffsetToolPreservesWholeTypedSourceAndUsesTheExistingDirectedLeftSide(int kind,bool reverse)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),kind);
        if(reverse){source=d.AddExpression(SketchExpressionEntities.Author(d.Value,source).Geometry.Reverse());d.SetExpressionConstruction(source,false);}
        var author=SketchExpressionEntities.Author(d.Value,source);var before=d.Value;var id=d.OffsetExactCurve(source,.075,Kernel);var value=Curve(d,id);
        Assert.Equal(source,value.Source!.Entity);Assert.Equal(author.Kind,value.Source.Kind);Assert.True(value.Source.Geometry.SameValues(author.Geometry));Assert.Equal(-.075,value.Geometry.SignedOffset);
        foreach(double t in new[]{0d,.2,.51,1})
        {var native=Kernel.EvaluateExpression(author.Geometry,t);double length=double.Hypot(native.First.X,native.First.Y);Near(new(native.Point.X-.075*native.First.Y/length,native.Point.Y+.075*native.First.X/length),value.Geometry.At(t));}
        var child=d.OffsetExactCurve(id,-.025,Kernel);Assert.Equal(id,Curve(d,child).Source!.Entity);Assert.True(Curve(d,child).Geometry.Basis!.SameValues(value.Geometry));
        Assert.Equal(before.Points.ToArray(),d.Value.Points.Take(before.Points.Length).ToArray());d.Value.Validate();SketchAssociativeEndpoints.RequireSolved(d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void OldBezierCanTrimAgainstAnExpressionAndExpressionCanTrimAgainstOldBezier(bool expressionTarget)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Typed intersections",RigidTransform3d.Identity));d.AddBezier(new(0,0),new(4,3),new(8,0));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(2,-3),new(2,3))));
        if(expressionTarget){d.TrimExpressionAt(id,new(2,-1),Kernel,Kernel);Near(new(2,1.125),Curve(d,id).Geometry.At(0));}
        else{d.TrimBezierAt(d.Value.Beziers.Single().Id,new(.8,.54),Kernel);Near(new(2,1.125),d.Value.Points.Single(p=>p.Id==d.Value.Beziers.Single().Start).Position);}
    }
    [Fact] public void NativeExtensionStopsAtTheFirstFiniteBoundaryAndRejectsAmbiguousOrMissingPicks()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Finite continuation",RigidTransform3d.Identity));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(2,0))).Offset(.1));
        d.AddLine(new(3,-1),new(3,1),construction:true);d.AddEllipse(new(new(5,0),new(1,0),new(0,1),0,Math.Tau),true);
        d.ExtendExpressionAt(id,new(1.9,-.1),Kernel,Kernel);Near(new(3,-.1),Curve(d,id).Geometry.At(1));
        var before=d.Value;Assert.Throws<CadValidationException>(()=>d.ExtendExpressionAt(id,new(1.5,-.1),Kernel,Kernel));Assert.Same(before,d.Value);
        Assert.Throws<CadValidationException>(()=>d.ExtendExpressionAt(id,new(.01,-.1),Kernel,Kernel));Assert.Same(before,d.Value);
    }
    [Theory][InlineData("entity")][InlineData("kind")][InlineData("captured")][InlineData("basis")][InlineData("depth")][InlineData("offset")][InlineData("direction")][InlineData("range")][InlineData("frozen-old")][InlineData("frozen-new")]
    public void AContinuationProofRejectsAllUnprovenAuthorChanges(string fault)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.1);var before=Curve(d,id);
        var after=before with{Geometry=before.Geometry.ContinueNativeRange(-2,before.Geometry.LastParameter),Source=before.Source! with{BasisDepth=2}};
        Assert.True(SketchExpressionOperationProof.IsSingleContinuation(before,after));
        after=fault switch{
            "entity"=>after with{Source=after.Source! with{Entity=SketchEntityId.New()}},"kind"=>after with{Source=after.Source! with{Kind=SketchExpressionSourceKind.Bezier}},
            "captured"=>after with{Source=after.Source! with{Geometry=after.Source!.Geometry.Reverse()}},"basis"=>after with{Geometry=after.Geometry with{Basis=before.Geometry.Reverse()}},
            "depth"=>after with{Source=after.Source! with{BasisDepth=3}},"offset"=>after with{Geometry=after.Geometry with{SignedOffset=.1}},
            "direction"=>after with{Geometry=after.Geometry.Reverse()},"range"=>after with{Geometry=after.Geometry with{LastParameter=before.Geometry.LastParameter-1}},
            "frozen-new"=>after with{Source=after.Source! with{IsFrozen=true}},_=>after};
        if(fault=="frozen-old"){d.FreezeExpression(id);before=Curve(d,id);}
        Assert.False(SketchExpressionOperationProof.IsSingleContinuation(before,after));
        if(fault is not "offset")Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{ExpressionCurves=s.ExpressionCurves.Replace(Curve(d,id),after)}));
    }
    [Fact] public void AuthorContinuationPermissionDoesNotPermitAnInjectedSolverToAddAContinuation()
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.1);var before=d.Value;d.ExtendExpression(id,true,.25,Kernel);
        Assert.Throws<CadValidationException>(()=>SketchExpressionEntities.ValidateSolved(before,d.Value));
    }
    [Theory][InlineData(-.01)][InlineData(0)][InlineData(4.01)][InlineData(double.NaN)]
    public void ExtensionBudgetFailuresAreAtomic(double amount)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.1);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ExtendExpression(id,true,amount,Kernel));Assert.Same(before,d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProductionEditorRoutesTrimAndOffsetAndKeepsCancelIndependentOfDocument(bool offset)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,Kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
        try{
            var d=new SketchDraft(CadSketch.Create(part,"Production tools",RigidTransform3d.Identity));var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0))).Offset(.1));
            d.SetExpressionConstruction(id,false);d.AddLine(new(2,-1),new(2,1),construction:true);
            await using var editor=new SketchEditorViewModel(document,Kernel,new ManagedSketchConstraintSolver(),d.Value,true){Tool=offset?"OffsetCurve":"TrimCurve",OffsetDistance=.1};
            editor.EditCurve(id,new(1,-.1));if(offset)Assert.Equal(2,editor.Sketch.ExpressionCurves.Length);else Near(new(2,-.1),editor.Sketch.ExpressionCurves.Single().Geometry.At(0));
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(initial,session.Snapshot);
            editor.CancelCommand.Execute(null);Assert.Null(document.PreviewScene);Assert.Same(initial,session.Snapshot);
        }finally{document.Detach();}Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task SplitAndContinuationRoundtripWithOriginsAndExactNewRowsAndUndo()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,Kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Stored edits",RigidTransform3d.Identity));var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0))).Offset(.1));d.SetExpressionConstruction(id,false);
        d.RemoveExpressionInterval(id,.25,.75,Kernel);d.ExtendExpression(d.Value.ExpressionCurves[0].Id,true,.1,Kernel);d.Value.Validate();
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var saved=session.Snapshot;var storage=new CadDocumentStorage();var path=files.PathFor("edited.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets)){Assert.True(SketchExpressionEntities.SameTable(saved.Sketches[d.Value.Id],loaded.Snapshot.Sketches[d.Value.Id]));Assert.Equal(saved.Sketches[d.Value.Id].Points.ToArray(),loaded.Snapshot.Sketches[d.Value.Id].Points.ToArray());}
        await session.UndoAsync();Assert.Same(initial,session.Snapshot);await session.RedoAsync();Assert.Same(saved,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ChangingAnExpressionEndpointKeepsAnExplicitIndependentPointAndAnEndpointUsedByAnotherCurve(bool independent)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Retain point",RigidTransform3d.Identity));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0))).Offset(.1));
        if(independent){d.AddPoint(new(0,-.1));d.AttachExpressionEndpoints(id,d.Value.Points.Single().Id);d.SetExpressionConstruction(id,false);}
        else{d.SetExpressionConstruction(id,false);d.AddLine(new(0,-.1),new(0,-2),Curve(d,id).Endpoints!.Start,construction:true);}
        var point=d.Value.Points.Single(p=>p.Id==Curve(d,id).Endpoints!.Start);var constraint=new FixPointConstraint(SketchConstraintId.New(),point.Id,point.Position);
        d.Change(s=>s with{Constraints=s.Constraints.Add(constraint)});d.TrimExpression(id,.2,1,Kernel);
        Assert.Contains(point,d.Value.Points);Assert.Contains(constraint,d.Value.Constraints);d.Value.Validate();
    }
    [Theory][InlineData("endpoint")][InlineData("intersection")][InlineData("overlap")]
    public void AmbiguousNativeTrimPicksRejectWithoutDraftChanges(string fault)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Pick guards",RigidTransform3d.Identity));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0))).Offset(.1));
        d.AddLine(fault=="overlap"?new(1,-.1):new(2,-1),fault=="overlap"?new(4,-.1):new(2,1),construction:true);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.TrimExpressionAt(id,fault=="endpoint"?new(0,-.1):fault=="intersection"?new(2,-.1):new(1,-.1),Kernel,Kernel));Assert.Same(before,d.Value);
    }
    [Fact] public void AContinuationCannotExceedTheUnchanged64NodeAuthorBudget()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Depth guard",RigidTransform3d.Identity));var g=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,0)));
        for(int i=1;i<64;i++)g=g.Offset(0);var id=d.AddExpression(g);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ExtendExpression(id,true,.1,Kernel));Assert.Same(before,d.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CornerEditsKeepIndependentOrUnknownJointPointsAndTheirUserFixConstraint(bool independent)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Joint provenance",RigidTransform3d.Identity));d.AddPoint(new(0,0));var point=d.Value.Points.Single();
        if(!independent)d=new SketchDraft(d.Value with{Points=[point with{Origin=SketchPointOrigin.Unknown}]});point=d.Value.Points.Single();
        d.AddLine(new(-4,0),new(0,0),end:point.Id);d.AddLine(new(0,0),new(0,4),start:point.Id);
        var fix=new FixPointConstraint(SketchConstraintId.New(),point.Id,point.Position);d.Change(s=>s with{Constraints=s.Constraints.Add(fix)});
        d.Fillet(d.Value.Lines[0].Id,d.Value.Lines[1].Id,1);Assert.Contains(point,d.Value.Points);Assert.Contains(fix,d.Value.Constraints);Assert.All(d.Value.Points.Where(p=>p.Id!=point.Id),p=>Assert.Equal(SketchPointOrigin.CurveGenerated,p.Origin));d.Value.Validate();
    }
}
