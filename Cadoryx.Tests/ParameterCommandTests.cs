using System.Collections.Immutable;
using System.IO.Compression;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ParameterCommandTests
{
    private static CadParameter Length(string name,string expression)=>new(Guid.NewGuid(),name,ParameterDimension.Length,expression);
    private static ParameterBinding Drive(CadParameter parameter,ParameterTarget target)=>new(Guid.NewGuid(),parameter.Id,target);
    [Fact] public async Task OneParameterUpdatesSketchExtrusionDrawingUndoRedoAndRoundtripAtomically()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Plate",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,10));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Plate",part,sketchSource:source));
        var feature=session.Snapshot.Features.Values.Single();var sheet=TechnicalDrawingSheet.A4Landscape("Parameters");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
        var width=Length("width","40 mm");var depth=Length("depth","width / 10");var book=new ParameterBook([width,depth],[Drive(width,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<OffsetXConstraint>().Single().Id)),Drive(depth,ParameterTarget.Feature(feature.Id,"Distance"))]);
        var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var after=session.Snapshot;Assert.Equal(1600,after.Features[feature.Id].Result.VolumeMm3,5);
        Assert.Equal(40,after.Sketches[sketch.Id].Points.Max(p=>p.Position.X),6);Assert.Equal(after.Features[feature.Id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single().Revision);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[width with{Expression="5 cm"},depth]}));Assert.Equal(2500,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        var path=files.PathFor("parameters.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        loaded.Snapshot.Validate();Assert.Equal(50,ParameterGraph.Evaluate(loaded.Snapshot)[width.Id].Value);Assert.Equal(2500,loaded.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Contains("cadoryx.document-parameters.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
    }
    [Fact] public async Task ReferenceDimensionChangesPropagateAndBoundManualEditsAreRejected()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("References"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Source"));var box=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,5,new(new(50,0,0),Quaterniond.Identity)),"Dependent",box.PartId));var cylinder=session.Snapshot.Features.Values.Single(f=>f.Id!=box.Id);
        var reference=new CadParameter(Guid.NewGuid(),"source_width",ParameterDimension.Length,"",ParameterTarget.Feature(box.Id,"X"));var radius=Length("radius","source_width / 10");
        var book=new ParameterBook([reference,radius],[Drive(radius,ParameterTarget.Feature(cylinder.Id,"Radius"))]);await session.ExecuteAsync(new SetParametersCommand(book));
        await session.ExecuteAsync(new RecomputeCommand(box.Id,new BoxRecipe(30,10,5,RigidTransform3d.Identity)));Assert.Equal(3,Assert.IsType<CylinderRecipe>(session.Snapshot.Features[cylinder.Id].Recipe).Radius);
        Assert.Equal(45*Math.PI,session.Snapshot.Features[cylinder.Id].Result.VolumeMm3,5);var before=session.Snapshot;int count=assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(cylinder.Id,((CylinderRecipe)cylinder.Recipe) with{Radius=4})));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        var cycle=book with{Bindings=book.Bindings.Add(Drive(radius,ParameterTarget.Feature(box.Id,"X")))};
        Assert.Contains("cycle",(await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(cycle)))).Message);Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(book with{Bindings=[]}));await session.ExecuteAsync(new RecomputeCommand(cylinder.Id,((CylinderRecipe)cylinder.Recipe) with{Radius=4}));
    }
    [Fact] public async Task InvalidDimensionsNegativeDriverConflictAndCancellationKeepOriginalSnapshotAndAssets()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Atomic"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Box"));var feature=session.Snapshot.Features.Values.Single();var before=session.Snapshot;int count=assets.Count;
        foreach(string expression in new[]{"-1 mm","1 deg","missing"})
        {
            var value=Length("x",expression);await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([value],[Drive(value,ParameterTarget.Feature(feature.Id,"X"))]))));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        var p=Length("x","30 mm");using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,ParameterTarget.Feature(feature.Id,"X"))])),cancel.Token));Assert.Same(before,session.Snapshot);
        var duplicate=Drive(p,ParameterTarget.Feature(feature.Id,"X"));await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[duplicate,duplicate with{Id=Guid.NewGuid()}]))));Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task VersionTwentyMigratesEmptyTableAndMasqueradedParameterSectionIsRefused()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=files.PathFor("legacy.cadoryx");
        await storage.SaveAsync(DocumentSnapshot.Create("Empty"),assets,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=20}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Where(s=>s.Kind is not ("parameters" or "datums" or "assembly-patterns")).ToImmutableArray()});using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){zip.GetEntry("sections/parameters.msgpack")!.Delete();zip.GetEntry("sections/datums.msgpack")!.Delete();zip.GetEntry("sections/assembly-patterns.msgpack")!.Delete();}
        using var loaded=await storage.LoadAsync(path,assets);Assert.Empty(loaded.Snapshot.Parameters.Parameters);Assert.Empty(loaded.Snapshot.Parameters.Bindings);
    }
    [Fact] public async Task RadiusBindingRebuildsExactCircularExtrusionAndForgedSolverIdentityIsRejected()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Circle",RigidTransform3d.Identity));d.AddCircle(new(0,0),3);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),5,sketch.Plane)),"Round",part,sketchSource:source));
        var radius=Length("radius","4 mm");var book=new ParameterBook([radius],[Drive(radius,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<RadiusConstraint>().Single().Id))]);
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book,new ForgedSolver())));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(book));Assert.Equal(80*Math.PI,session.Snapshot.Features.Values.Single().Result.VolumeMm3,5);
    }
    [Fact] public async Task ConstraintConflictAndDownstreamKernelFailureReleaseAllStagedResults()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new SketchAssociationTests.InspectingKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Locked",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(20,10));
        d.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),s.Points[1].Id,s.Points[1].Position))});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var p=Length("width","30 mm");var before=session.Snapshot;
        await Assert.ThrowsAsync<SketchSolveException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<OffsetXConstraint>().Single().Id))]))));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"A",part));var a=session.Snapshot.Features.Values.Single();var bodyA=session.Snapshot.Bodies.Keys.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Identity),"B",part));var bodyB=session.Snapshot.Bodies.Keys.Single(id=>id!=bodyA);
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[bodyA,bodyB]));before=session.Snapshot;int count=assets.Count;kernel.FailBoolean=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[Drive(p,ParameterTarget.Feature(a.Id,"X"))]))));Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    [Fact] public async Task EditorPreviewInputGuardCancelConfirmAndStaleStateUseOneAtomicCandidate()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Editor"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Box"));var feature=session.Snapshot.Features.Values.Single();var before=session.Snapshot;
        var document=new Cadoryx.ViewModels.CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
        try
        {
            await using(var editor=new Cadoryx.ViewModels.ParameterEditorViewModel(document,kernel))
            {
                editor.ParameterName="width";editor.Expression="30 mm";editor.ApplyParameterCommand.Execute(null);editor.DrivingTarget=editor.Targets.Single(t=>t.Target==ParameterTarget.Feature(feature.Id,"X"));editor.BindDimensionCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                editor.Expression="40 mm";Assert.False(editor.CanConfirm);await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
                editor.ApplyParameterCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm);editor.CancelCommand.Execute(null);Assert.Same(before,session.Snapshot);
            }
            await using(var editor=new Cadoryx.ViewModels.ParameterEditorViewModel(document,kernel))
            {
                editor.ParameterName="width";editor.Expression="30 mm";editor.ApplyParameterCommand.Execute(null);editor.DrivingTarget=editor.Targets.Single(t=>t.Target==ParameterTarget.Feature(feature.Id,"X"));editor.BindDimensionCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(1500,session.Snapshot.Features[feature.Id].Result.VolumeMm3,6);
            }
            await using var stale=new Cadoryx.ViewModels.ParameterEditorViewModel(document,kernel);await stale.PreviewCommand.ExecuteAsync(null);Assert.True(stale.CanConfirm);
            await session.ExecuteAsync(DocumentEdits.RenameBody(session.Snapshot.Bodies.Keys.Single(),"Changed"));Assert.True(stale.IsStale);Assert.False(stale.CanConfirm);
        }
        finally{document.Detach();}
    }
    private sealed class ForgedSolver:ISketchConstraintSolver
    {
        public string Version=>"test";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {var report=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);return report with{Solution=report.Solution! with{Id=SketchId.New()}};}
    }
    [Fact] public async Task AngularRecipeBindingConvertsDegreesToRadiansAndRebuildsExactRevolution()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var d=new SketchDraft(CadSketch.Create(part,"Torus",RigidTransform3d.Identity));d.AddCircle(new(10,0),2);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
        await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new RevolveRecipe(new([]),Math.PI,sketch.Plane)),"Revolved",part,sketchSource:source));var feature=session.Snapshot.Features.Values.Single();
        var angle=new CadParameter(Guid.NewGuid(),"angle",ParameterDimension.Angle,"90 deg");await session.ExecuteAsync(new SetParametersCommand(new([angle],[Drive(angle,ParameterTarget.Feature(feature.Id,"Angle"))])));
        Assert.Equal(Math.PI/2,Assert.IsType<RevolveRecipe>(session.Snapshot.Features[feature.Id].Recipe).AngleRadians,12);Assert.Equal(20*Math.PI*Math.PI,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
    }
}
