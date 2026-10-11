using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;
using static Cadoryx.Tests.SketchMixedFiniteContactTests;

namespace Cadoryx.Tests;

public sealed class SketchMixedFiniteContactIntegrationTests
{
    private static ParameterBook Drive(CadSketch sketch,double value)
    {
        var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var parameter=new CadParameter(Guid.NewGuid(),"mixed_contact_t",ParameterDimension.Scalar,value.ToString("R",CultureInfo.InvariantCulture));
        return new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(sketch.Id,c.Id,"ParameterA"))]);
    }
    private static double Expected(CadSketch s,SketchEntityId id)
    {
        var b=s.Beziers.Single(b=>b.Id==id);Point2d P(SketchEntityId point)=>s.Points.Single(p=>p.Id==point).Position;
        var a=P(b.Start);var e=P(b.End);var control=P(b.Control);
        return 2*Math.Abs((e.X-a.X)*(control.Y-a.Y)-(e.Y-a.Y)*(control.X-a.X))/3;
    }

    [Theory][InlineData("Circle",false)][InlineData("Ellipse",true)][InlineData("RationalSpline",false)]
    public async Task G2ParameterRebuildsExactBodyAndDrawingAtomicallyAndSurvivesReopen(string kind,bool opposite)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new UpsertSketchCommand(Pair(kind,opposite,true) with{PartId=part},new ManagedSketchConstraintSolver()));
            var seed=session.Snapshot.Sketches.Values.Single();var c=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            var source=SketchProfileReference.CreateBezierSegment(seed,c.B);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,seed.Plane)),"Mixed G2 body",part,sketchSource:source));
            Assert.Equal(Expected(seed,c.B),session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,6);
            var sheet=TechnicalDrawingSheet.A4Landscape("Mixed continuity");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(seed,.41)));var after=session.Snapshot;var solved=after.Sketches[seed.Id];
            SketchFiniteParametricContact.RequireSolvedRelations(solved);Assert.Equal(FiniteContactContinuity.Curvature,solved.Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
            var feature=after.Features.Values.Single();var view=after.DrawingSheets[sheet.Id].Views.Single();
            Assert.Equal(Expected(solved,c.B),feature.Result.VolumeMm3,6);Assert.NotEqual(before.Features[feature.Id].Result.Revision,feature.Result.Revision);
            Assert.Equal(solved.Revision,feature.SketchSource!.Revision);Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Equal(feature.Result.Revision,view.Sources.Single().Revision);
            Assert.Equal(seed.Ellipses,solved.Ellipses);Assert.Equal(seed.Freeforms,solved.Freeforms);
            long generation=session.Generation;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(solved,1))));
            Assert.Same(after,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();string path=files.PathFor("mixed-body.cadoryx");await session.SaveAsync(storage,path);
            using var reopened=await storage.LoadAsync(path,assets);reopened.Snapshot.Validate();SketchFiniteParametricContact.RequireSolvedRelations(reopened.Snapshot.Sketches[seed.Id]);
            Assert.Equal(view.Sources.ToArray(),reopened.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());
            Assert.Equal(c.Continuity,reopened.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("Circle",true)][InlineData("Ellipse",false)][InlineData("RationalSpline",true)]
    public async Task IndependentPartRetainsMixedG2AuthorsAndDrivesOnlyItsOwnGraph(string kind,bool opposite)
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(Pair(kind,opposite,true) with{PartId=part},new ManagedSketchConstraintSolver()));
        var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Independent",RigidTransform3d.Translate(20,0,0)));
        var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
        var original=session.Snapshot.Sketches.Values.Single(s=>s.PartId==part);var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
        Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Constraints.ToArray(),copied.Constraints.ToArray());
        Assert.Equal(original.Ellipses,copied.Ellipses);Assert.Equal(original.Freeforms,copied.Freeforms);
        await session.ExecuteAsync(new SetParametersCommand(Drive(copied,.41)));var changed=session.Snapshot.Sketches[copied.Id];
        SketchFiniteParametricContact.RequireSolvedRelations(changed);Assert.Same(original,session.Snapshot.Sketches[original.Id]);
        Assert.Equal(FiniteContactContinuity.Curvature,changed.Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
    }

    [Theory][InlineData("Bezier",false)][InlineData("Circle",false)][InlineData("Circle",true)][InlineData("Ellipse",true)][InlineData("RationalSpline",true)]
    public async Task LegacyNineteenCannotSmuggleMixedOrG2WhileActualLegacyG1Migrates(string kind,bool curvature)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var report=await new ManagedSketchConstraintSolver().SolveAsync(Pair(kind,order:curvature?FiniteContactContinuity.Curvature:FiniteContactContinuity.Tangent));
        Assert.True(report.Succeeded,report.Diagnostic);var sketch=report.Solution! with{PartId=part};
        var doc=initial with{Sketches=initial.Sketches.Add(sketch.Id,sketch)};string path=files.PathFor("mixed-schema.cadoryx");
        await storage.SaveAsync(doc,assets,path);var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-mixed-finite-continuity.1",manifest.RequiredCapabilities);
        using(var reopened=await storage.LoadAsync(path,assets))
            Assert.Equal(sketch.Constraints.ToArray(),reopened.Snapshot.Sketches[sketch.Id].Constraints.ToArray());
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind is "document" or "sketches"?s with{SchemaVersion=s.Kind=="document"?50:19}:s).ToImmutableArray()});
        if(curvature||kind!="Bezier")await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        else
        {
            using var reopened=await storage.LoadAsync(path,assets);
            Assert.Equal(FiniteContactContinuity.Tangent,reopened.Snapshot.Sketches[sketch.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
            Assert.Equal(sketch.Constraints.ToArray(),reopened.Snapshot.Sketches[sketch.Id].Constraints.ToArray());
        }
    }

    [Fact]
    public async Task StrictTerminalCanSetG2AndCannotSilentlyDowngradeAnExistingRelation()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var seed=Pair("Ellipse",true);var contact=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        seed=seed with{PartId=part,Constraints=seed.Constraints.Remove(contact)};
        await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();
            string Json(string? continuity,bool edit)=>JsonSerializer.Serialize(new Dictionary<string,object?>
            {
                ["action"]="set_sketch_finite_contact",["id"]=seed.Id.ToString(),["revision"]=session.Snapshot.Sketches[seed.Id].Revision,
                ["curveA"]=contact.A.ToString(),["curveB"]=contact.B.ToString(),["parameterA"]=.37,["parameterB"]=.5,["direction"]="OppositeDirection"
            }.Concat(continuity is null?[]:new Dictionary<string,object?>{["continuity"]=continuity})
                .Concat(edit?new Dictionary<string,object?>{["constraintId"]=session.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Id.ToString()}:[]).ToDictionary(p=>p.Key,p=>p.Value));
            var result=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+Json("G2",false),new CadCommandContext(document));
            Assert.True(result.Success,result.Message);var before=session.Snapshot;
            result=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+Json(null,true),new CadCommandContext(document));
            Assert.True(result.Success,result.Message);Assert.Same(before,session.Snapshot);
            Assert.Equal(FiniteContactContinuity.Curvature,session.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
            foreach(string order in new[]{"G3","Automatic","curvature"})
            {Assert.False((await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+Json(order,true),new CadCommandContext(document))).Success);Assert.Same(before,session.Snapshot);}
            var inspect=await service.ExecuteAsync("SKETCH_GEOMETRY",new CadCommandContext(document));Assert.True(inspect.Success);Assert.Contains("G2",inspect.Message);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task EditorExplicitOrderInvalidatesPreviewAndPersistsThroughUndoRedo(string language)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
            await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
            var seed=Pair("Circle",perturb:true);var contact=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            seed=seed with{PartId=part,Constraints=seed.Constraints.Remove(contact)};
            await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(),session.Snapshot.Sketches[seed.Id],false);
                editor.ConstraintKind="FiniteParametricContact";editor.ContactParameterA=.37;editor.ContactParameterB=.5;editor.ContactContinuity=FiniteContactContinuity.Curvature;
                editor.SelectedEntities.Add(contact.A);editor.SelectedEntities.Add(contact.B);editor.AddConstraintCommand.Execute(null);
                var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();Assert.Equal(FiniteContactContinuity.Curvature,c.Continuity);
                editor.SelectedConstraint=editor.Constraints.Single(v=>v.Id==c.Id);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                editor.ContactContinuity=FiniteContactContinuity.Tangent;Assert.False(editor.CanConfirm);
                editor.ApplyFiniteContactCommand.Execute(null);Assert.Equal(FiniteContactContinuity.Tangent,editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity);
                editor.UndoCommand.Execute(null);Assert.Equal(FiniteContactContinuity.Curvature,editor.ContactContinuity);
                editor.RedoCommand.Execute(null);Assert.Equal(FiniteContactContinuity.Tangent,editor.ContactContinuity);
                var beforeInvalid=editor.Sketch;editor.ContactParameterA=1;Assert.False(editor.CanConfirm);
                editor.ApplyFiniteContactCommand.Execute(null);
                Assert.Same(beforeInvalid,editor.Sketch);Assert.Equal(Cadoryx.Lang.Strings.Strings.FiniteContactInvalidParameterOrder,editor.Status);
                await editor.PreviewCommand.ExecuteAsync(null);
                Assert.Same(beforeInvalid,editor.Sketch);Assert.False(editor.CanConfirm);
                Assert.Equal(Cadoryx.Lang.Strings.Strings.FiniteContactInvalidParameterOrder,editor.Status);
                Assert.Equal(2,editor.FiniteContactContinuities.Count);Assert.All(editor.FiniteContactContinuities,o=>Assert.False(string.IsNullOrWhiteSpace(o.Label)));
            }
            finally{document.Detach();}
        }
        finally{CultureInfo.CurrentUICulture=previous;}
    }

    [Theory][InlineData("Circle")][InlineData("Ellipse")][InlineData("RationalSpline")]
    public async Task BezierTrimSplitOrExtendClearsMixedG2InsteadOfMovingContact(string kind)
    {
        var report=await new ManagedSketchConstraintSolver().SolveAsync(Pair(kind));Assert.True(report.Succeeded);var s=report.Solution!;
        var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        foreach(var edited in new[]{SketchExactBezierEditing.Keep(s,c.B,.1,.9),SketchExactBezierEditing.Remove(s,c.B,.3,.7),SketchExactBezierEditing.Extend(s,c.B,false,.1)})
        {edited.Validate();Assert.Empty(edited.Constraints.OfType<FiniteParametricContactConstraint>());}
        var draft=new SketchDraft(s);draft.RemoveEntities([c.A]);Assert.Empty(draft.Value.Constraints.OfType<FiniteParametricContactConstraint>());
        draft.Undo();Assert.Equal(c,draft.Value.Constraints.OfType<FiniteParametricContactConstraint>().Single());
    }
}
