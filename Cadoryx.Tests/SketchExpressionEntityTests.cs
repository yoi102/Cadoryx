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
using Strings=Cadoryx.Lang.Strings.Strings;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionEntityTests
{
    internal static (SketchDraft Draft,SketchEntityId Source) Source(DefinitionId part,int kind)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Expression authors",new(new(3,4,5),Quaterniond.FromEulerDegrees(25,15,0))));
        switch(kind)
        {
            case 0:d.AddLine(new(0,0),new(8,4));return(d,d.Value.Lines[0].Id);
            case 1:d.AddCircle(new(2,3),5);return(d,d.Value.Circles[0].Id);
            case 2:d.AddArc(new(4,0),new(0,4),new(-4,0));return(d,d.Value.Arcs[0].Id);
            case 3:d.AddBezier(new(0,0),new(4,3),new(8,0));return(d,d.Value.Beziers[0].Id);
            case 4:d.AddSpline([new(0,0),new(2,3),new(5,4),new(8,0)]);return(d,d.Value.Splines[0].Id);
            case 5:d.AddEllipse(new(new(2,-3),new(7,1.5),new(-.8,2.6),.2,1.8));return(d,d.Value.Ellipses[0].Id);
            case 6:d.AddFreeform(SketchFreeformGeometryTests.Geometry(2));return(d,d.Value.Freeforms[0].Id);
            default:return(d,d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,4))).Offset(.1)));
        }
    }
    private static SketchExpressionCurve Curve(CadSketch sketch,SketchEntityId id)=>sketch.ExpressionCurves.Single(e=>e.Id==id);
    private static void Near(Point2d expected,Point2d actual)=>Assert.InRange(double.Hypot(expected.X-actual.X,expected.Y-actual.Y),0,1e-9);

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public void StableSourceValuesAndIdsGiveTheDirectedRightNormalForEveryAuthorFamily(int kind)
    {
        var(d,source)=Source(DefinitionId.New(),kind);var original=d.Value;var id=d.AddExactOffset(source,.075);
        var value=Curve(d.Value,id);Assert.Equal(original.Revision,d.Value.Revision);Assert.Equal(source,value.Source!.Entity);
        Assert.True(value.Source.Geometry.SameValues(SketchExpressionEntities.Author(original,source).Geometry));
        var second=d.AddExactOffset(id,-.025);Assert.Equal(id,Curve(d.Value,second).Source!.Entity);
        foreach(double t in new[]{0d,.13,.5,.91,1})
        {
            var p=value.Source.Geometry.Evaluate(t);double speed=double.Hypot(p.First.X,p.First.Y);
            Near(new(p.Point.X+.075*p.First.Y/speed,p.Point.Y-.075*p.First.X/speed),value.Geometry.At(t));
        }
        d.Undo();Assert.DoesNotContain(d.Value.ExpressionCurves,e=>e.Id==second);d.Redo();Assert.Equal(second,d.Value.ExpressionCurves[^1].Id);
        d.Value.Validate();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LinkedChainsRefreshAtomicallyAndOwnedCopiesKeepTheirValues(bool freeze)
    {
        var(d,source)=Source(DefinitionId.New(),0);var a=d.AddExactOffset(source,.2);var b=d.AddExactOffset(a,-.1);
        var copy=d.AddExactOffset(source,.3,false);if(freeze)d.FreezeExpression(a);
        var before=d.Value;d.MovePoint(before.Lines[0].End,new(9,7));var after=d.Value;
        Assert.Equal(before.ExpressionCurves.Select(e=>e.Id),after.ExpressionCurves.Select(e=>e.Id));
        Assert.True(Curve(before,copy).Geometry.SameValues(Curve(after,copy).Geometry));
        Assert.Equal(freeze,Curve(before,a).Geometry.SameValues(Curve(after,a).Geometry));
        Assert.Equal(freeze,Curve(before,b).Geometry.SameValues(Curve(after,b).Geometry));
        d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
        if(freeze){d.ResumeExpression(a);Assert.False(Curve(before,a).Geometry.SameValues(Curve(d.Value,a).Geometry));}
    }
    [Fact] public void FreezeRetainsProvenanceWhileDetachEnablesIndependentBasisEditing()
    {
        var(d,source)=Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.2);d.FreezeExpression(id);var frozen=d.Value;
        Assert.Throws<CadValidationException>(()=>d.EditExpression(id,Curve(frozen,id).Geometry with{SignedOffset=.4}));Assert.Same(frozen,d.Value);
        d.Change(s=>s with{Lines=[]});Assert.Equal(source,Curve(d.Value,id).Source!.Entity);
        var removed=d.Value;Assert.Throws<CadValidationException>(()=>d.ResumeExpression(id));Assert.Same(removed,d.Value);
        d.DetachExpression(id);Assert.Null(Curve(d.Value,id).Source);
        var basis=SketchCurveExpressionGeometry.FromBoundary(new(new(1,1),new(4,7)));d.EditExpression(id,basis.Offset(.4));
        Assert.True(basis.SameValues(Curve(d.Value,id).Geometry.Basis!));Assert.Equal(id,Curve(d.Value,id).Id);
    }
    [Fact] public void RemovalBasisForgeryAndSourceDomainChangeLeaveDraftAndHistoryUntouched()
    {
        var(d,source)=Source(DefinitionId.New(),6);var id=d.AddExactOffset(source,.05);var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Freeforms=[]}));Assert.Same(before,d.Value);
        Assert.Throws<CadValidationException>(()=>d.EditExpression(id,SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(1,1))).Offset(.05)));Assert.Same(before,d.Value);
        d.EditExpression(id,Curve(before,id).Geometry.Trim(.1,.5));before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.EditFreeform(source,before.Freeforms[0].Geometry with{FirstParameter=.6}));Assert.Same(before,d.Value);
        var child=d.AddExactOffset(id,.025);before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.RemoveExpression(id));Assert.Same(before,d.Value);Assert.Equal(child,d.Value.ExpressionCurves[^1].Id);
    }
    [Fact] public async Task AuthorIdsCannotBeReusedAsLegacyGeometryOrUnsupportedProfileBoundaries()
    {
        var(d,source)=Source(DefinitionId.New(),0);var id=d.AddExactOffset(source,.2);var before=d.Value;var c=Curve(before,id);
        Assert.Throws<CadValidationException>(()=>(before with{ExpressionCurves=[c with{Id=source}]}).Validate());
        Assert.Throws<CadValidationException>(()=>(before with{ExpressionCurves=[c with{IsConstruction=false}]}).Validate());
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Mixed(before,[id]));
        Assert.Equal(before.Points.Length*2,(await new ManagedSketchConstraintSolver().SolveAsync(before)).VariableCount);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SourceSolverCoordinatesAndExpressionValuesCommitUndoAndRecoverTogether(bool cancel)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,source)=Source(part,0);var id=d.AddExactOffset(source,.2);var end=d.Value.Lines[0].End;
            d.Change(s=>s with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),s.Lines[0].Start,new(0,0)),new FixPointConstraint(SketchConstraintId.New(),end,new(10,6))]});
            using var cancellation=new CancellationTokenSource();if(cancel)cancellation.Cancel();
            if(cancel){await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()),cancellation.Token));Assert.Same(initial,session.Snapshot);return;}
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var after=session.Snapshot;var s=after.Sketches[d.Value.Id];
            Near(new(10,6),s.Points.Single(p=>p.Id==end).Position);Assert.Equal(id,Curve(s,id).Id);
            Assert.True(Curve(s,id).Source!.Geometry.SameValues(SketchExpressionEntities.Author(s,source).Geometry));
            await session.UndoAsync();Assert.Same(initial,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();RecoveryKey key;
            using(var recovery=new CadRecoveryStore(files.PathFor("recovery"),storage))
            {var sessionId=Guid.NewGuid();key=new(recovery.RunId,sessionId);await recovery.WriteAsync(sessionId,after,assets,null);}
            using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,assets);
            Assert.True(SketchExpressionEntities.SameTable(s,recovered.Document.Snapshot.Sketches[s.Id]));Assert.Equal(after.StateId,recovered.Document.Snapshot.StateId);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("distance")][InlineData("id")][InlineData("source")][InlineData("frozen")]
    public async Task InjectableSolverCannotReplaceExpressionIntent(string mutation)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var(d,source)=Source(part,0);d.AddExactOffset(source,.2);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(d.Value,new CorruptSolver(mutation))));Assert.Same(initial,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class CorruptSolver(string mutation):ISketchConstraintSolver
    {
        public string Version=>"corrupt-expression";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var result=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);var s=result.Solution!;var c=s.ExpressionCurves.Single();
            c=mutation switch{"id"=>c with{Id=SketchEntityId.New()},"source"=>c with{Source=null},"frozen"=>c with{Source=c.Source! with{IsFrozen=true}},
                _=>c with{Geometry=c.Geometry with{SignedOffset=.4}}};return result with{Solution=s with{ExpressionCurves=[c]}};
        }
    }
    [Theory][InlineData(0)][InlineData(5)][InlineData(6)]
    public async Task CommittedSourceEditsKeepStableDescendantIdsAndCanBeUndone(int kind)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var(d,source)=Source(part,kind);var a=d.AddExactOffset(source,.05);var b=d.AddExactOffset(a,-.01);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var before=session.Snapshot;var old=before.Sketches[d.Value.Id];var edit=new SketchDraft(old);
            if(kind==0)edit.MovePoint(old.Lines[0].End,new(9,7));
            else if(kind==5)edit.EditEllipse(source,old.Ellipses[0].Geometry with{Cosine=old.Ellipses[0].Geometry.Cosine with{X=8}});
            else edit.EditFreeform(source,old.Freeforms[0].Geometry with{Poles=old.Freeforms[0].Geometry.Poles.Select(p=>p with{Y=p.Y*1.1}).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver()));var after=session.Snapshot;var current=after.Sketches[old.Id];
            Assert.Equal(a,current.ExpressionCurves[0].Id);Assert.Equal(b,current.ExpressionCurves[1].Id);Assert.False(Curve(old,b).Geometry.SameValues(Curve(current,b).Geometry));
            Assert.True(Curve(current,b).Source!.Geometry.SameValues(Curve(current,a).Geometry));after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SourceDirectionRefreshPreservesRelativeTraversalAndDirectedNormal(bool reverseTarget)
    {
        var(d,source)=Source(DefinitionId.New(),5);var id=d.AddExactOffset(source,.075);
        if(reverseTarget)d.EditExpression(id,Curve(d.Value,id).Geometry.Reverse());
        d.EditEllipse(source,d.Value.Ellipses[0].Geometry.Reversed());var curve=Curve(d.Value,id);var basis=SketchExpressionEntities.Author(d.Value,source).Geometry;
        Assert.Equal(reverseTarget,basis.Reversed!=curve.Geometry.Reversed);
        foreach(double t in new[]{0d,.13,.5,.91,1})
        {
            var p=basis.Evaluate(reverseTarget?1-t:t);double length=double.Hypot(p.First.X,p.First.Y);
            Near(new(p.Point.X+.075*p.First.Y/length,p.Point.Y-.075*p.First.X/length),curve.Geometry.At(t));
        }
        new OcctGeometryKernel().PrepareExpression(curve.Geometry);
    }
    [Theory][InlineData("missing-index")][InlineData("source-basis")][InlineData("wrong-field")][InlineData("frozen")]
    public void DistanceDriversCannotAddressFrozenValuesOrCopiedSourceOperations(string mutation)
    {
        var(doc,part)=SketchCommandTests.Document();var(d,source)=Source(part,7);var id=d.AddExactOffset(source,.075);
        if(mutation=="frozen")d.FreezeExpression(id);
        doc=doc with{Sketches=doc.Sketches.Add(d.Value.Id,d.Value)};var t=ParameterTarget.SketchCurve(d.Value.Id,id,"OffsetDistance",0);
        t=mutation switch{"missing-index"=>t with{Index=null},"source-basis"=>t with{Index=1},"wrong-field"=>t with{Property="PoleX"},_=>t};
        Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(doc,t));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void StableSourceIdCannotSilentlySwitchItsUnderlyingAuthorFamily(bool expressionSource)
    {
        var(d,source)=Source(DefinitionId.New(),expressionSource?7:6);d.AddExactOffset(source,.05);var before=d.Value;
        if(expressionSource)
        {
            var g=new SketchEllipseGeometry(new(0,0),new(5,0),new(0,5),0,Math.Tau);
            Assert.Throws<CadValidationException>(()=>d.EditExpression(source,SketchCurveExpressionGeometry.FromBoundary(new(g.At(0),g.At(1)){Ellipse=g}).Offset(.1)));
        }
        else Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Freeforms=[s.Freeforms[0] with{Geometry=SketchFreeformGeometryTests.Geometry(0)}]}));
        var circle=new SketchEllipseGeometry(new(0,0),new(5,0),new(0,5),0,Math.Tau);
        var forgedBasis=SketchCurveExpressionGeometry.FromBoundary(new(circle.At(0),circle.At(1)){Ellipse=circle});
        var target=before.ExpressionCurves[^1];var forged=before with{ExpressionCurves=before.ExpressionCurves.Replace(target,
            target with{Geometry=forgedBasis.Offset(.05),Source=target.Source! with{Geometry=forgedBasis}})};
        Assert.Throws<CadValidationException>(()=>SketchExpressionEntities.ValidateDrivenEdit(before,forged));
        Assert.Same(before,d.Value);
    }
    [Theory][InlineData("en")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task ExistingParameterEditorOffersLocalizedDistanceAndKeepsPreviewCancelable(string culture)
    {
        var originalCulture=System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture=System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
            await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
            {
                var(d,source)=Source(part,0);var id=d.AddExactOffset(source,.2);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
                var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
                try
                {
                    await using var editor=new ParameterEditorViewModel(doc,kernel);var target=ParameterTarget.SketchCurve(d.Value.Id,id,"OffsetDistance",0);
                    var choice=Assert.Single(editor.Targets,t=>t.Target==target);Assert.Contains(Strings.OffsetCurve,choice.Label);Assert.Contains(Strings.OffsetDistance,choice.Label);
                    editor.ParameterName="distance";editor.Expression="0.4 mm";editor.ApplyParameterCommand.Execute(null);editor.DrivingTarget=choice;editor.BindDimensionCommand.Execute(null);
                    var before=session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                    editor.CancelCommand.Execute(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
                }
                finally{doc.Detach();}
            }
            Assert.Equal(0,assets.Count);
        }
        finally{System.Globalization.CultureInfo.CurrentUICulture=originalCulture;}
    }
    [Theory][InlineData(0)][InlineData(5)][InlineData(6)]
    public async Task ProjectedPrimitiveAndLocalOffsetChainRefreshInTheSameDocumentCommit(int kind)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var(d,sourceId)=Source(part,kind);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var source=session.Snapshot.Sketches[d.Value.Id];
            var target=new SketchDraft(CadSketch.Create(part,"Projected offsets",RigidTransform3d.Identity));target.Project(source,sourceId);
            var projected=target.Value.Projections[0].TargetEntity;var a=target.AddExactOffset(projected,.025);var b=target.AddExactOffset(a,-.01);
            await session.ExecuteAsync(new UpsertSketchCommand(target.Value,new ManagedSketchConstraintSolver()));var before=session.Snapshot;var edit=new SketchDraft(source);
            if(kind==0)edit.MovePoint(source.Lines[0].End,new(9,7));
            else if(kind==5)edit.EditEllipse(sourceId,source.Ellipses[0].Geometry with{Center=new(3,-2)});
            else edit.EditFreeform(sourceId,source.Freeforms[0].Geometry with{Poles=source.Freeforms[0].Geometry.Poles.Select(p=>p with{Y=p.Y*1.1}).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver()));var after=session.Snapshot;var updated=after.Sketches[target.Value.Id];
            Assert.Equal(after.Sketches[source.Id].Revision,updated.Projections[0].SourceRevision);Assert.Equal(a,updated.ExpressionCurves[0].Id);Assert.Equal(b,updated.ExpressionCurves[1].Id);
            Assert.False(Curve(before.Sketches[updated.Id],b).Geometry.SameValues(Curve(updated,b).Geometry));
            Assert.True(Curve(updated,a).Source!.Geometry.SameValues(SketchExpressionEntities.Author(updated,projected).Geometry));after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task NamedDistanceAndSourceDefinitionsRecomputeSuccessorsAndFoldFailureRollsBack()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using(var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Driven circle offsets",RigidTransform3d.Identity));d.AddEllipse(new(new(0,0),new(5,0),new(0,5),0,Math.Tau));
            var source=d.Value.Ellipses[0].Id;var a=d.AddExactOffset(source,.2);var b=d.AddExactOffset(a,.1);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            var s=session.Snapshot.Sketches[d.Value.Id];var distance=SketchCurveParameterTests.Number("distance",.4);
            var radius=SketchCurveParameterTests.Number("radius",7);var distanceTarget=ParameterTarget.SketchCurve(s.Id,a,"OffsetDistance",0);
            var pairs=new[]{(distance,distanceTarget),(radius,ParameterTarget.SketchCurve(s.Id,source,"CosineX")),(radius,ParameterTarget.SketchCurve(s.Id,source,"SineY"))};
            var book=new ParameterBook([distance,radius],pairs.Select(p=>SketchCurveParameterTests.Drive(p.Item1,p.Item2)).ToImmutableArray());
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var after=session.Snapshot;s=after.Sketches[s.Id];
            Near(new(7.4,0),Curve(s,a).Geometry.At(0));Near(new(7.5,0),Curve(s,b).Geometry.At(0));Assert.Equal(2,ParameterTargets.All(after).Count(t=>t.Curve==a.Value||t.Curve==b.Value));
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();await session.SaveAsync(storage,files.PathFor("driven.cadoryx"));using(var loaded=await storage.LoadAsync(files.PathFor("driven.cadoryx"),assets))
            {Assert.True(SketchExpressionEntities.SameTable(s,loaded.Snapshot.Sketches[s.Id]));Assert.Equal(.4,ParameterTargets.Read(loaded.Snapshot,distanceTarget).Value);}
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=book.Parameters.SetItem(0,distance with{Expression="-8 mm"})})));Assert.Same(after,session.Snapshot);
            var edit=new SketchDraft(s);edit.EditExpression(a,Curve(s,a).Geometry with{SignedOffset=.5});
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,new ManagedSketchConstraintSolver())));Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
}
