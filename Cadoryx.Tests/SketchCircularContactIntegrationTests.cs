using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using MessagePack;
using Xunit;
using static Cadoryx.Tests.SketchCircularContactTests;

namespace Cadoryx.Tests;

public sealed class SketchCircularContactIntegrationTests
{
    private static CadSketch Owned(CadSketch s,DefinitionId part)=>s with{PartId=part};
    private static CadSketch DrivenPair(DefinitionId part)
    {
        var s=Owned(Pair(CircularContactBranch.External),part);var a=s.Circles[0];var b=s.Circles[1];
        return s with{Constraints=s.Constraints.Add(Fix(s,a.Center)).Add(new OffsetYConstraint(SketchConstraintId.New(),a.Center,b.Center,0))};
    }
    private static ParameterBook Drive(CadSketch sketch,double radius)
    {
        var r=sketch.Constraints.OfType<RadiusConstraint>().Single(r=>r.Circle==sketch.Circles[1].Id);
        var value=new CadParameter(Guid.NewGuid(),"contact_radius",ParameterDimension.Length,radius.ToString("R",CultureInfo.InvariantCulture)+" mm");
        return new([value],[new(Guid.NewGuid(),value.Id,ParameterTarget.Sketch(sketch.Id,r.Id))]);
    }
    [Fact] public async Task RadiusDriverRebuildsExactBodiesAndDrawingInOneUndoableCommitAndReopens()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new UpsertSketchCommand(DrivenPair(part),new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches.Values.Single();
            foreach(var circle in sketch.Circles)
            {
                var source=SketchProfileReference.CreateCircle(sketch,circle.Id);
                await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Round "+circle.Radius,part,sketchSource:source));
            }
            var sheet=TechnicalDrawingSheet.A4Landscape("Contact");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));var view=Guid.NewGuid();
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(sketch,3)));var after=session.Snapshot;
            var solved=after.Sketches[sketch.Id];SketchCircularContact.RequireSolvedContacts(solved);
            Assert.Equal(8,P(solved,sketch.Circles[1].Center).X,5);Assert.Equal(68*Math.PI,after.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3),4);
            Assert.All(after.Features.Values,f=>{Assert.False(f.IsStale);Assert.Equal(solved.Revision,f.SketchSource!.Revision);});
            var drawing=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(drawing.StaleReason);Assert.NotEmpty(drawing.Strokes);
            Assert.All(drawing.Sources,s=>Assert.Equal(after.Bodies[s.Body].Geometry.Revision,s.Revision));
            Assert.NotEqual(before.DrawingSheets[sheet.Id].Views.Single().Sources,drawing.Sources);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var path=files.PathFor("contact-driver.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();SketchCircularContact.RequireSolvedContacts(loaded.Snapshot.Sketches[sketch.Id]);
            Assert.Equal(solved.Constraints.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Constraints.ToArray());Assert.Equal(solved.Revision,loaded.Snapshot.Sketches[sketch.Id].Revision);
            Assert.Equal(after.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray(),loaded.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ProjectedCircleFiniteContactFailureRollsBackDriverAndAllDependents()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(DrivenPair(part),new ManagedSketchConstraintSolver()));var source=session.Snapshot.Sketches.Values.Single();
        var d=new SketchDraft(CadSketch.Create(part,"Projected contact",RigidTransform3d.Identity));d.Project(source,source.Circles[1].Id);d.AddLine(new(9,-5),new(9,5));
        var line=d.Value.Lines.Single();var circle=d.Value.Circles.Single();d.Change(s=>s with{Constraints=s.Constraints.Add(Fix(s,line.Start)).Add(Fix(s,line.End)).Add(
            new CircularContactConstraint(SketchConstraintId.New(),line.Id,circle.Id,CircularContactBranch.LineLeft))});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var before=session.Snapshot;long generation=session.Generation;int count=assets.Count;
        await Assert.ThrowsAsync<SketchSolveException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(source,3))));
        Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);Assert.Empty(session.Snapshot.Parameters.Parameters);
        SketchCircularContact.RequireSolvedContacts(session.Snapshot.Sketches[d.Value.Id]);
    }

    [Fact] public async Task ProductionEditorCreatesOrderedContactCancelsAndChangesBranchAtomically()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var seed=Owned(Fixed(LineArc()),part);
        seed=seed with{Constraints=seed.Constraints.Where(c=>c is not CircularContactConstraint).ToImmutableArray()};await session.ExecuteAsync(new UpsertSketchCommand(seed,solver));
        var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[seed.Id],false))
            {
                editor.Select(seed.Arcs[0].Id);editor.Select(seed.Lines[0].Id,true);editor.ConstraintKind="CircularContact";editor.ContactBranch=CircularContactBranch.LineLeft;editor.AddConstraintCommand.Execute(null);
                var relation=editor.Sketch.Constraints.OfType<CircularContactConstraint>().Single();Assert.Equal(seed.Arcs[0].Id,relation.A);Assert.Equal(seed.Lines[0].Id,relation.B);
                editor.UndoCommand.Execute(null);Assert.Empty(editor.Sketch.Constraints.OfType<CircularContactConstraint>());editor.RedoCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);editor.CancelCommand.Execute(null);
            }
            Assert.Same(before,session.Snapshot);
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[seed.Id],false))
            {
                editor.Select(seed.Arcs[0].Id);editor.Select(seed.Lines[0].Id,true);editor.ConstraintKind="CircularContact";editor.ContactBranch=CircularContactBranch.LineLeft;editor.AddConstraintCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var committed=session.Snapshot;
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,committed.Sketches[seed.Id],false))
            {
                var relation=committed.Sketches[seed.Id].Constraints.OfType<CircularContactConstraint>().Single();editor.SelectedConstraint=editor.Constraints.Single(c=>c.Id==relation.Id);
                Assert.True(editor.ShowContactBranch);Assert.Equal(relation.Branch,editor.ContactBranch);editor.ContactBranch=CircularContactBranch.LineRight;
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(committed,session.Snapshot);Assert.Null(document.PreviewScene);
                editor.ContactBranch=CircularContactBranch.LineLeft;editor.ApplyContactBranchCommand.Execute(null);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Equal(relation.Id,editor.Sketch.Constraints.OfType<CircularContactConstraint>().Single().Id);
            }
            Assert.Same(committed,session.Snapshot);
        }
        finally{document.Detach();}
    }

    [Fact] public async Task CommandAndParameterRoutesRecheckInjectedSolverFiniteContact()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(Owned(Fixed(LineArc(outsideArc:true)),part),new UnverifiedSolver())));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new UpsertSketchCommand(DrivenPair(part),new ManagedSketchConstraintSolver()));before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(before.Sketches.Values.Single(),3),new UnverifiedSolver())));Assert.Same(before,session.Snapshot);
    }
    private sealed class UnverifiedSolver:ISketchConstraintSolver
    {
        public string Version=>"test-unverified";
        public Task<SketchSolveReport> SolveAsync(CadSketch s,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)=>
            Task.FromResult(new SketchSolveReport(SketchSolveStatus.UnderConstrained,s,1,1,1,1,0,0,[],[],[],"FAKE.SUCCESS"));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SchemaSixteenCannotSmuggleEnabledOrDisabledContactAndLegacyDataMigrates(bool enabled)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var s=Owned(Fixed(LineArc()),part);s=s with{Constraints=s.Constraints.Select(c=>c is CircularContactConstraint?c with{IsEnabled=enabled}:c).ToImmutableArray()};
        var doc=initial with{Sketches=initial.Sketches.Add(s.Id,s)};var path=files.PathFor("contact-schema.cadoryx");await storage.SaveAsync(doc,assets,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["sketches"].Version,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-circular-contact.1",manifest.RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Equal(s.Constraints.ToArray(),loaded.Snapshot.Sketches[s.Id].Constraints.ToArray());
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=16}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        var legacy=s with{Constraints=s.Constraints.Where(c=>c is not CircularContactConstraint).ToImmutableArray()};
        await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(s.Id,legacy)},assets,path);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=16}:s.Kind=="document"?s with{SchemaVersion=42}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);Assert.Equal(legacy.Constraints.ToArray(),migrated.Snapshot.Sketches[s.Id].Constraints.ToArray());Assert.Equal(legacy.Revision,migrated.Snapshot.Sketches[s.Id].Revision);
    }

    [Theory][InlineData("fractional")][InlineData("unknown")][InlineData("nan")][InlineData("arity")][InlineData("wrong-family")][InlineData("foreign")]
    public async Task StorageRejectsMalformedOrWrongTypedContact(string mutation)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var s=Owned(Fixed(LineArc()),part);
        var path=files.PathFor("malformed.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(s.Id,s)},assets,path);
        FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var constraints=pack.Sketches[0].Constraints;int i=Array.FindIndex(constraints,c=>c.Kind=="circular-contact");
            constraints[i]=mutation switch{"fractional"=>constraints[i] with{Values=[1.5]},"unknown"=>constraints[i] with{Values=[99]},"nan"=>constraints[i] with{Values=[double.NaN]},
                "arity"=>constraints[i] with{Targets=[s.Arcs[0].Id.Value]},"wrong-family"=>constraints[i] with{Values=[3]},_=>constraints[i] with{Targets=[Guid.NewGuid(),s.Lines[0].Id.Value]}};
            return MessagePackSerializer.Serialize(pack);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task IndependentPartRetainsSketchLocalContactIdsAndIsolatesFurtherEdits()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());await session.ExecuteAsync(new UpsertSketchCommand(DrivenPair(part),new ManagedSketchConstraintSolver()));
        var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Copy",RigidTransform3d.Translate(20,0,0)));
        var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);var after=session.Snapshot;
        var original=after.Sketches.Values.Single(s=>s.PartId==part);var copied=after.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
        Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Constraints.ToArray(),copied.Constraints.ToArray());
        await session.ExecuteAsync(new SetParametersCommand(Drive(copied,3)));SketchCircularContact.RequireSolvedContacts(session.Snapshot.Sketches[copied.Id]);
        Assert.Same(original,session.Snapshot.Sketches[original.Id]);Assert.Equal(2,original.Circles[1].Radius,6);Assert.Equal(3,session.Snapshot.Sketches[copied.Id].Circles[1].Radius,6);
    }

    [Fact] public async Task StructuredAuthorCreatesEditsInspectsRejectsStaleAndDeletesWithStableId()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var seed=Owned(Fixed(LineArc()),part);
        seed=seed with{Constraints=seed.Constraints.Where(c=>c is not CircularContactConstraint).ToImmutableArray()};await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);
            async Task<JsonDocument> Call(string name,object args)=>JsonDocument.Parse(await tools.ExecuteAsync(new AiToolCall(Guid.NewGuid().ToString("N"),name,JsonSerializer.Serialize(args)),default));
            object Args(string branch,Guid? id=null)=>new{action="set_sketch_contact",documentId=session.Snapshot.Id.ToString(),stateId=session.Snapshot.StateId.ToString(),
                id=seed.Id.ToString(),revision=session.Snapshot.Sketches[seed.Id].Revision.ToString("D"),constraintId=id?.ToString("D"),curveA=seed.Arcs[0].Id.ToString(),curveB=seed.Lines[0].Id.ToString(),branch,enabled=true};
            // Null is not an omitted optional identity: use the exact creation shape.
            var create=new{action="set_sketch_contact",documentId=session.Snapshot.Id.ToString(),stateId=session.Snapshot.StateId.ToString(),id=seed.Id.ToString(),
                revision=session.Snapshot.Sketches[seed.Id].Revision.ToString("D"),curveA=seed.Arcs[0].Id.ToString(),curveB=seed.Lines[0].Id.ToString(),branch="LineLeft"};
            using var created=await Call("cad_edit",create);Assert.True(created.RootElement.GetProperty("success").GetBoolean(),created.RootElement.ToString());
            var relation=session.Snapshot.Sketches[seed.Id].Constraints.OfType<CircularContactConstraint>().Single();var before=session.Snapshot;
            using var noop=await Call("cad_edit",Args("LineLeft",relation.Id.Value));Assert.True(noop.RootElement.GetProperty("success").GetBoolean());Assert.False(noop.RootElement.GetProperty("changed").GetBoolean());Assert.Same(before,session.Snapshot);
            using var inspect=await Call("cad_inspect",new{area="sketch_geometry",id=seed.Id.ToString()});var constraints=inspect.RootElement.GetProperty("items")[0].GetProperty("constraints");
            var info=constraints.EnumerateArray().Single(c=>c.GetProperty("id").GetString()==relation.Id.ToString());Assert.Equal("LineLeft",info.GetProperty("branch").GetString());Assert.Equal("SKETCH.CONTACT_VERIFIED",info.GetProperty("diagnostic").GetString());
            using var stale=await Call("cad_edit",create);Assert.False(stale.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            using var wrong=await Call("cad_edit",Args("External",relation.Id.Value));Assert.False(wrong.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            using var deleted=await Call("cad_edit",new{action="delete_sketch_contact",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),id=seed.Id.ToString(),revision=before.Sketches[seed.Id].Revision,constraintId=relation.Id.ToString()});
            Assert.True(deleted.RootElement.GetProperty("success").GetBoolean());Assert.Empty(session.Snapshot.Sketches[seed.Id].Constraints.OfType<CircularContactConstraint>());await session.UndoAsync();Assert.Same(before,session.Snapshot);
        }
        finally{document.Detach();}
    }
    [Fact] public async Task TerminalUsesCapturedStateAndRejectsHiddenOrDuplicateFields()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var seed=Owned(Fixed(LineArc()),part);seed=seed with{Constraints=seed.Constraints.Where(c=>c is not CircularContactConstraint).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();var author=JsonSerializer.Serialize(new{action="set_sketch_contact",id=seed.Id.ToString(),revision=session.Snapshot.Sketches[seed.Id].Revision,
                curveA=seed.Arcs[0].Id.ToString(),curveB=seed.Lines[0].Id.ToString(),branch="LineLeft"});
            var oldContext=new CadCommandContext(document);var result=await service.ExecuteAsync("SKETCH_CONTACT "+author,new CadCommandContext(document));Assert.True(result.Success,result.Message);
            var before=session.Snapshot;Assert.Single(before.Sketches[seed.Id].Constraints.OfType<CircularContactConstraint>());
            Assert.False((await service.ExecuteAsync("SKETCH_CONTACT "+author,oldContext)).Success);Assert.Same(before,session.Snapshot);
            foreach(var bad in new[]{author[..^1]+",\"x\":1}",author[..^1]+",\"branch\":\"LineLeft\"}",author[..^1]+",\"constraintId\":null}",author+" trailing"})
            {Assert.False((await service.ExecuteAsync("SKETCH_CONTACT "+bad,new CadCommandContext(document))).Success);Assert.Same(before,session.Snapshot);}
            var page=await service.ExecuteAsync("SKETCH_GEOMETRY",new CadCommandContext(document));Assert.True(page.Success);Assert.Contains("SKETCH.CONTACT_VERIFIED",page.Message);
            await session.UndoAsync();Assert.Empty(session.Snapshot.Sketches[seed.Id].Constraints.OfType<CircularContactConstraint>());await session.RedoAsync();Assert.Same(before,session.Snapshot);
        }
        finally{document.Detach();}
    }
}
