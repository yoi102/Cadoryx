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

public sealed class SketchCornerEditingTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    private static SketchDraft Rectangle(DefinitionId? part=null)
    {var d=new SketchDraft(CadSketch.Create(part??DefinitionId.New(),"Corners",RigidTransform3d.Identity));d.AddRectangle(new(10,0),new(50,30));return d;}
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void ChamferDistancesFollowExplicitLineSelectionAndKeepStableFarEnds(bool reverseA,bool reverseB)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Chamfer",RigidTransform3d.Identity));
        d.AddLine(reverseA?new(20,0):new(0,0),reverseA?new(0,0):new(20,0));var a=d.Value.Lines[0];var j=reverseA?a.End:a.Start;
        d.AddLine(reverseB?new(0,20):new(0,0),reverseB?new(0,0):new(0,20),start:reverseB?null:j,end:reverseB?j:null);var b=d.Value.Lines[1];
        var before=d.Value;d.Chamfer(a.Id,b.Id,2,3);var connector=d.Value.Lines[^1];Assert.Equal(new Point2d(2,0),P(d.Value,connector.Start));Assert.Equal(new Point2d(0,3),P(d.Value,connector.End));
        Assert.Equal(before.Lines.Select(l=>l.Id),d.Value.Lines.Take(2).Select(l=>l.Id));Assert.Single(d.CornerReplacements);d.Undo();Assert.Same(before,d.Value);Assert.Empty(d.CornerReplacements);d.Redo();Assert.Single(d.CornerReplacements);
    }
    [Theory] [InlineData(true,2)] [InlineData(true,4)] [InlineData(false,2)] [InlineData(false,4)]
    public void MultipleCornersCreateOneKnownClosedBoundaryAndOneUndo(bool fillet,int count)
    {
        var d=Rectangle();var before=d.Value;var points=before.Points.Take(count).Select(p=>p.Id).ToArray();
        d.EditCorners(points,fillet,3,2,3,constrainRadius:true);var after=d.Value;
        Assert.Equal(count,d.CornerReplacements.Length);Assert.Equal(4+(fillet?0:count),after.Lines.Length);Assert.Equal(fillet?count:0,after.Arcs.Length);
        Assert.Single(fillet?SketchMixedLoops.Find(after):SketchLoops.Find(after));Assert.Equal(fillet?count:0,after.Constraints.OfType<RadiusConstraint>().Count());
        d.Undo();Assert.Same(before,d.Value);Assert.Empty(d.CornerReplacements);d.Redo();Assert.Same(after,d.Value);Assert.Equal(count,d.CornerReplacements.Length);
    }
    [Theory] [InlineData(true,21,0,0)] [InlineData(false,0,21,21)] [InlineData(false,0,1,30)] [InlineData(true,double.NaN,0,0)]
    public void OneBadCornerOrCombinedLineConsumptionRejectsWholeEdit(bool fillet,double radius,double a,double b)
    {
        var d=Rectangle();var before=d.Value;Assert.Throws<CadValidationException>(()=>d.EditCorners(before.Points.Select(p=>p.Id),fillet,radius,a,b));Assert.Same(before,d.Value);Assert.Empty(d.CornerReplacements);
        d.Undo();Assert.Empty(d.Value.Lines); // Failed batch did not append a history entry.
    }
    [Fact] public void DuplicateCornersDifferentLoopsBranchAndConstructionAreNotGuessed()
    {
        var d=Rectangle();var a=d.Value.Points[0].Id;
        Assert.Throws<CadValidationException>(()=>d.EditCorners([a,a],true,2,0,0));
        d.AddRectangle(new(70,0),new(90,20));var b=d.Value.Points[^1].Id;Assert.Throws<CadValidationException>(()=>d.EditCorners([a,b],true,2,0,0));d.Undo();
        d.AddLine(P(d.Value,a),new(0,-5),a);Assert.Throws<CadValidationException>(()=>d.EditCorners([a],true,2,0,0));d.Undo();
        d.SetConstruction(d.Value.Lines.Select(l=>l.Id),true);Assert.Throws<CadValidationException>(()=>d.EditCorners([a],false,0,2,3));
    }
    private static void AnchorRoundedRectangle(SketchDraft d,CadSketch original)
    {
        var originalPoints=original.Points.Select(p=>p.Id).ToHashSet();
        d.Change(s=>s with{Constraints=[..s.Constraints,
            ..s.Points.Where(p=>originalPoints.Contains(p.Id)).Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            ..s.Lines.Take(4).Select(l=>Math.Abs(P(original,original.Lines.Single(o=>o.Id==l.Id).Start).Y-
                P(original,original.Lines.Single(o=>o.Id==l.Id).End).Y)<1e-7?(SketchConstraint)new HorizontalConstraint(SketchConstraintId.New(),l.Id):new VerticalConstraint(SketchConstraintId.New(),l.Id))]});
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitCornerConversionRebuildsExtrusionAndRevolutionWithoutGuessing(bool revolve)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=Rectangle(part);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));
        GeometryRecipe recipe=revolve?new RevolveRecipe(new([]),Math.Tau,s.Plane):new ExtrudeRecipe(new([]),5,s.Plane);
        recipe=source.Resolve(session.Snapshot,part,recipe);await session.ExecuteAsync(new AddBodyCommand(recipe,"Associated",part,sketchSource:source));
        var before=session.Snapshot;var feature=before.Features.Values.Single(f=>f.SketchSource is not null);var originalVolume=feature.Result.VolumeMm3;
        var edit=new SketchDraft(s);edit.EditCorners(s.Points.Select(p=>p.Id),true,3,0,0,constrainRadius:true);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver)));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver,cornerReplacements:edit.CornerReplacements,updateProfiles:[feature.Id]));var after=session.Snapshot;
        Assert.True(after.Features[feature.Id].Result.VolumeMm3<originalVolume);Assert.Equal(8,after.Features[feature.Id].SketchSource!.MixedBoundaryIds.Length);
        if(!revolve)Assert.Equal((1200-4*9*(1-Math.PI/4))*5,after.Features[feature.Id].Result.VolumeMm3,5);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var path=files.PathFor("converted.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(after.Features[feature.Id].SketchSource!.MixedBoundaryIds.ToArray(),loaded.Snapshot.Features[feature.Id].SketchSource!.MixedBoundaryIds.ToArray());
    }
    [Fact] public async Task CornerProfileConversionRequiresEveryAffectedFeatureAndRejectsForgedOrStaleCorrespondence()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var s=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));var recipe=source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,s.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"One",part,sketchSource:source));await session.ExecuteAsync(new AddBodyCommand(recipe,"Two",part,sketchSource:source));var before=session.Snapshot;
        var ids=before.Features.Values.Where(f=>f.SketchSource is not null).Select(f=>f.Id).ToArray();var edit=new SketchDraft(s);edit.Chamfer(s.Lines[0].Id,s.Lines[1].Id,2,3);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver,cornerReplacements:edit.CornerReplacements,updateProfiles:[ids[0]])));Assert.Same(before,session.Snapshot);
        var forged=edit.CornerReplacements.SetItem(0,edit.CornerReplacements[0] with{Connection=s.Lines[2].Id});
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver,cornerReplacements:forged,updateProfiles:ids)));Assert.Same(before,session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(edit.Value with{Revision=Guid.NewGuid()},solver,cornerReplacements:edit.CornerReplacements,updateProfiles:ids)));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver,cornerReplacements:edit.CornerReplacements,updateProfiles:ids));
        Assert.All(ids,id=>Assert.Equal(5985,session.Snapshot.Features[id].Result.VolumeMm3,5));
    }
    [Fact] public async Task TwoFilletsTwoChamfersEqualRadiusAndAssociatedDrawingFormOneAtomicSample()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var original=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(original,original.Lines.Select(l=>l.Id));
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,original.Plane)),"Mixed corners",part,sketchSource:source));
        var sheet=TechnicalDrawingSheet.A4Landscape("B7 sample");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
        var before=session.Snapshot;var feature=before.Features.Values.Single(f=>f.SketchSource is not null);d=new(original);
        d.Fillet(original.Lines[0].Id,original.Lines[1].Id,3,true);d.Fillet(original.Lines[1].Id,original.Lines[2].Id,3,true);
        d.Chamfer(original.Lines[2].Id,original.Lines[3].Id,2,3);d.Chamfer(original.Lines[3].Id,original.Lines[0].Id,2,3);
        Assert.Equal(4,d.Value.Constraints.OfType<EndpointTangentConstraint>().Count());
        var arcA=d.Value.Arcs[0];var arcB=d.Value.Arcs[1];var radius=d.Value.Constraints.OfType<RadiusConstraint>().First(r=>r.Circle==arcA.Id);
        var chamferPoints=d.CornerReplacements.Skip(2).SelectMany(r=>new[]{r.PointA,r.PointB}).ToHashSet();
        d.Change(s=>s with{Constraints=[..s.Constraints.Where(c=>c is not RadiusConstraint r||r.Circle!=arcB.Id),
            new EqualRadiusConstraint(SketchConstraintId.New(),arcA.Id,arcB.Id),
            ..s.Points.Where(p=>chamferPoints.Contains(p.Id)).Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            new HorizontalConstraint(SketchConstraintId.New(),original.Lines[0].Id),new VerticalConstraint(SketchConstraintId.New(),original.Lines[1].Id),
            new HorizontalConstraint(SketchConstraintId.New(),original.Lines[2].Id),new VerticalConstraint(SketchConstraintId.New(),original.Lines[3].Id),
            new OffsetXConstraint(SketchConstraintId.New(),s.Lines[0].Start,s.Lines[1].Start,37)]});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver,cornerReplacements:d.CornerReplacements,updateProfiles:[feature.Id]));
        Assert.Equal((1200-2*9*(1-Math.PI/4)-6)*5,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        var accepted=session.Snapshot;var changed=accepted.Sketches[original.Id] with{Constraints=accepted.Sketches[original.Id].Constraints.Select(c=>c.Id==radius.Id?radius with{Radius=4}:c).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(changed,solver));var after=session.Snapshot;
        Assert.Equal((1200-2*16*(1-Math.PI/4)-6)*5,after.Features[feature.Id].Result.VolumeMm3,5);
        Assert.All(after.Sketches[original.Id].Arcs,a=>Assert.Equal(4,SketchArcRadiusTests.Radius(after.Sketches[original.Id],a),6));
        Assert.Equal(after.Features[feature.Id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single().Revision);
        await session.UndoAsync();Assert.Same(accepted,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var path=files.PathFor("b7-sample.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(after.Features[feature.Id].Result.VolumeMm3,loaded.Snapshot.Features[feature.Id].Result.VolumeMm3);
        TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("b7-sample.pdf"));
    }
    [Fact] public async Task EditorExplicitCheckboxPreviewCancelConfirmAndRadiusEditRecomputeNativeVolume()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var s=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,s.Plane)),"Linked",part,sketchSource:source));
        var sheet=TechnicalDrawingSheet.A4Landscape("Associated");var viewId=Guid.NewGuid();await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,viewId,"Top",DrawingViewKind.Top,new(100,100),1));
        var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var canceled=new SketchEditorViewModel(document,kernel,solver,s,false){Tool="Fillet"})
            {
                canceled.Select(s.Lines[0].Id);canceled.Select(s.Lines[1].Id,true);canceled.ApplyFilletCommand.Execute(null);
                await canceled.PreviewCommand.ExecuteAsync(null);Assert.False(canceled.CanConfirm);Assert.Same(before,session.Snapshot);
                canceled.FeatureProfileUpdates.Single().IsSelected=true;await canceled.PreviewCommand.ExecuteAsync(null);Assert.True(canceled.CanConfirm,canceled.Status);canceled.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,s,false){Tool="Fillet"})
            {
                editor.Select(s.Lines[0].Id);editor.Select(s.Lines[1].Id,true);editor.ApplyFilletCommand.Execute(null);
                editor.FeatureProfileUpdates.Single().IsSelected=true;AnchorRoundedRectangle(editor.Draft,s);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var rounded=session.Snapshot.Sketches[s.Id];var radius=rounded.Constraints.OfType<RadiusConstraint>().Single();
            await using var dimension=new SketchEditorViewModel(document,kernel,solver,rounded,false);
            dimension.SelectedConstraint=dimension.Constraints.Single(c=>c.Id==radius.Id);dimension.DimensionValue=3;dimension.ApplyDimensionCommand.Execute(null);
            await dimension.PreviewCommand.ExecuteAsync(null);Assert.True(dimension.CanConfirm,dimension.Status);await dimension.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal((1200-9*(1-Math.PI/4))*5,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);
            Assert.Equal(3,SketchArcRadiusTests.Radius(session.Snapshot.Sketches[s.Id],session.Snapshot.Sketches[s.Id].Arcs.Single()),6);
            var view=session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==viewId);Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Equal(session.Snapshot.Features.Values.Single(f=>f.SketchSource is not null).Result.Revision,view.Sources.Single().Revision);
        }
        finally{document.Detach();}
    }
}
