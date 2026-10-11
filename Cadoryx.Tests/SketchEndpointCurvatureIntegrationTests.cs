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
using static Cadoryx.Tests.SketchEndpointCurvatureTests;

namespace Cadoryx.Tests;

public sealed class SketchEndpointCurvatureIntegrationTests
{
    private static CadSketch Owned(DefinitionId part,bool solved=false)=>Pair(solved:solved) with{PartId=part};
    private static ParameterBook Drive(CadSketch s,double x)
    {
        var c=s.Constraints.OfType<OffsetXConstraint>().Single(c=>c.B==s.Splines[0].Controls[1]);
        var p=new CadParameter(Guid.NewGuid(),"tangent_handle",ParameterDimension.Length,x.ToString("R",CultureInfo.InvariantCulture)+" mm");
        return new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Sketch(s.Id,c.Id))]);
    }
    internal static CadSketch Closed(DefinitionId part)
    {
        var d=new SketchDraft(Owned(part));var a=d.Value.Arcs[0];var b=d.Value.Splines[0];
        Point2d P(SketchEntityId id)=>d.Value.Points.Single(p=>p.Id==id).Position;
        d.AddLine(P(a.End),P(b.Controls[^1]),start:a.End,end:b.Controls[^1]);return d.Value;
    }
    // Analytic integral of y dx for the authored cubic plus the quarter-circle.
    // Independent of tessellation and of the production profile/volume code.
    internal static double ExpectedVolume(double handle,double distance)
    {
        double y=3*handle*handle/8;
        double[] x=[0,3*handle,-12-6*handle,6+3*handle],v=[0,0,3*y,4-3*y];double integral=0;
        for(int i=0;i<v.Length;i++)for(int j=1;j<x.Length;j++)integral+=v[i]*j*x[j]/(i+j);
        return (40+integral-(16-4*Math.PI))*distance;
    }

    [Fact] public async Task DriverRebuildsExactMixedBodyAndDrawingAtomicallyAndReopens()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new UpsertSketchCommand(Closed(part),new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches.Values.Single();
            var source=SketchProfileReference.CreateMixed(s,SketchMixedLoops.Find(s).Single());
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,s.Plane)),"G2 body",part,sketchSource:source));
            Assert.Equal(ExpectedVolume(-2,2),session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);
            var sheet=TechnicalDrawingSheet.A4Landscape("G2 continuity");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(s,-3)));var after=session.Snapshot;var updated=after.Sketches[s.Id];RequireG2(updated);
            var c=updated.Constraints.OfType<EndpointCurvatureConstraint>().Single();foreach(var id in new[]{c.A,c.B})RequireNativeEndpoint(updated,id,c.Joint);
            Assert.Equal(ExpectedVolume(-3,2),after.Bodies.Values.Single().Geometry.VolumeMm3,5);
            var feature=after.Features.Values.Single();Assert.False(feature.IsStale);Assert.Equal(updated.Revision,feature.SketchSource!.Revision);
            var drawing=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(drawing.StaleReason);Assert.NotEmpty(drawing.Strokes);
            Assert.Equal(feature.Result.Revision,drawing.Sources.Single().Revision);
            long generation=session.Generation;int count=assets.Count;
            await Assert.ThrowsAsync<SketchSolveException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(updated,3))));
            Assert.Same(after,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("g2-linked.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);loaded.Snapshot.Validate();RequireG2(loaded.Snapshot.Sketches[s.Id]);
            Assert.Equal(updated.Constraints.ToArray(),loaded.Snapshot.Sketches[s.Id].Constraints.ToArray());
            Assert.Equal(after.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray(),loaded.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ProductionEditorCreatesTogglesDeletesPreviewsCancelsAndConfirms()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var seed=Owned(part);
        seed=seed with{Constraints=seed.Constraints.Where(c=>c is not EndpointCurvatureConstraint).ToImmutableArray()};await session.ExecuteAsync(new UpsertSketchCommand(seed,solver));
        var before=session.Snapshot;var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[seed.Id],false))
            {
                editor.Select(seed.Arcs[0].Id);editor.Select(seed.Splines[0].Id,true);editor.ConstraintKind="EndpointCurvature";editor.AddConstraintCommand.Execute(null);
                var relation=editor.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single();Assert.Equal(seed.Arcs[0].Id,relation.A);
                editor.UndoCommand.Execute(null);Assert.Empty(editor.Sketch.Constraints.OfType<EndpointCurvatureConstraint>());editor.RedoCommand.Execute(null);
                editor.SelectedConstraint=editor.Constraints.Single(c=>c.Id==relation.Id);editor.ToggleConstraintCommand.Execute(null);
                Assert.False(editor.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single().IsEnabled);editor.UndoCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);RequireG2(editor.Sketch);Assert.Same(before,session.Snapshot);
                editor.CancelCommand.Execute(null);
            }
            Assert.Same(before,session.Snapshot);Assert.Null(document.PreviewScene);
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,before.Sketches[seed.Id],false))
            {
                editor.Select(seed.Arcs[0].Id);editor.Select(seed.Splines[0].Id,true);editor.ConstraintKind="EndpointCurvature";editor.AddConstraintCommand.Execute(null);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
            }
            var accepted=session.Snapshot;RequireG2(accepted.Sketches[seed.Id]);await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
            await using var edit=new SketchEditorViewModel(document,kernel,solver,accepted.Sketches[seed.Id],false);
            var id=edit.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single().Id;edit.SelectedConstraint=edit.Constraints.Single(c=>c.Id==id);edit.DeleteConstraintCommand.Execute(null);
            Assert.Empty(edit.Sketch.Constraints.OfType<EndpointCurvatureConstraint>());edit.UndoCommand.Execute(null);Assert.Equal(id,edit.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single().Id);
        }
        finally{document.Detach();}
    }

    [Fact] public async Task InjectedSolverCannotPublishUnverifiedG2OrIgnoreADriver()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(Owned(part),new UnverifiedSolver())));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new UpsertSketchCommand(Owned(part),new ManagedSketchConstraintSolver()));before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(before.Sketches.Values.Single(),-3),new UnverifiedSolver())));Assert.Same(before,session.Snapshot);
    }
    private sealed class UnverifiedSolver:ISketchConstraintSolver
    {
        public string Version=>"test-unverified";
        public Task<SketchSolveReport> SolveAsync(CadSketch s,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)=>
            Task.FromResult(new SketchSolveReport(SketchSolveStatus.UnderConstrained,s,1,1,1,1,0,0,[],[],[],"FAKE.SUCCESS"));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LegacySeventeenCannotSmuggleEnabledOrDisabledG2(bool enabled)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var s=Owned(part,true);
        s=s with{Constraints=s.Constraints.Select(c=>c is EndpointCurvatureConstraint?c with{IsEnabled=enabled}:c).ToImmutableArray()};
        string path=files.PathFor("g2-schema.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(s.Id,s)},assets,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["sketches"].Version,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-endpoint-curvature.1",manifest.RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Equal(s.Constraints.ToArray(),loaded.Snapshot.Sketches[s.Id].Constraints.ToArray());
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=17}:s.Kind=="document"?s with{SchemaVersion=45}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        var legacy=s with{Constraints=s.Constraints.Where(c=>c is not EndpointCurvatureConstraint).ToImmutableArray()};
        await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(s.Id,legacy)},assets,path);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=17}:s.Kind=="document"?s with{SchemaVersion=45}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);Assert.Equal(legacy.Constraints.ToArray(),migrated.Snapshot.Sketches[s.Id].Constraints.ToArray());Assert.Equal(s.Revision,migrated.Snapshot.Sketches[s.Id].Revision);
    }

    [Theory][InlineData("fractional")][InlineData("unknown")][InlineData("nan")][InlineData("arity")][InlineData("joint")][InlineData("foreign")]
    public async Task StorageRejectsMalformedG2EvenWhenDisabled(string mutation)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var s=Owned(part,true);
        s=s with{Constraints=s.Constraints.Select(c=>c is EndpointCurvatureConstraint?c with{IsEnabled=false}:c).ToImmutableArray()};string path=files.PathFor("malformed-g2.cadoryx");
        await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(s.Id,s)},assets,path);
        FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var constraints=pack.Sketches[0].Constraints;int i=Array.FindIndex(constraints,c=>c.Kind=="endpoint-curvature-curves");
            constraints[i]=mutation switch{"fractional"=>constraints[i] with{Values=[1.5]},"unknown"=>constraints[i] with{Values=[2]},"nan"=>constraints[i] with{Values=[double.NaN]},
                "arity"=>constraints[i] with{Targets=[s.Arcs[0].Id.Value]},"joint"=>constraints[i] with{Targets=[..constraints[i].Targets.Take(2),s.Splines[0].Controls[1].Value]},
                _=>constraints[i] with{Targets=[Guid.NewGuid(),..constraints[i].Targets.Skip(1)]}};
            return MessagePackSerializer.Serialize(pack);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task IndependentPartRetainsLocalRelationIdentityAndIsolatesItsDriver()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());await session.ExecuteAsync(new UpsertSketchCommand(Owned(part),new ManagedSketchConstraintSolver()));
        var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Copy",RigidTransform3d.Translate(20,0,0)));
        var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
        var original=session.Snapshot.Sketches.Values.Single(s=>s.PartId==part);var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
        Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Constraints.ToArray(),copied.Constraints.ToArray());
        await session.ExecuteAsync(new SetParametersCommand(Drive(copied,-3)));RequireG2(session.Snapshot.Sketches[copied.Id]);Assert.Same(original,session.Snapshot.Sketches[original.Id]);
    }

    [Theory][InlineData("Bezier")][InlineData("Spline")]
    public void TrimPreservesOnlyRelationsWhoseActualJointSurvives(string kind)
    {
        var s=Pair(kind,solved:true);var c=s.Constraints.OfType<EndpointCurvatureConstraint>().Single();
        CadSketch Trim(double first,double last)=>kind=="Bezier"?SketchExactBezierEditing.Keep(s,c.B,first,last):SketchExactSplineEditing.Keep(s,c.B,first,last);
        var retained=Trim(0,.8);retained.Validate();Assert.Equal(c,retained.Constraints.OfType<EndpointCurvatureConstraint>().Single());
        var removed=Trim(.2,1);removed.Validate();Assert.Empty(removed.Constraints.OfType<EndpointCurvatureConstraint>());
        var draft=new SketchDraft(s);draft.RemoveEntities([c.A]);Assert.Empty(draft.Value.Constraints.OfType<EndpointCurvatureConstraint>());draft.Undo();Assert.Equal(c,draft.Value.Constraints.OfType<EndpointCurvatureConstraint>().Single());
    }

    [Fact] public async Task StructuredAuthorCreatesInspectsNoopsTogglesRejectsStaleAndDeletes()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var seed=Owned(part);seed=seed with{Constraints=seed.Constraints.Where(c=>c is not EndpointCurvatureConstraint).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);
            async Task<JsonDocument> Call(string name,object args)=>JsonDocument.Parse(await tools.ExecuteAsync(new AiToolCall(Guid.NewGuid().ToString("N"),name,JsonSerializer.Serialize(args)),default));
            Dictionary<string,object> Args(bool enabled=true)=>new(){["action"]="set_sketch_curvature",["documentId"]=session.Snapshot.Id.ToString(),["stateId"]=session.Snapshot.StateId.ToString(),
                ["id"]=seed.Id.ToString(),["revision"]=session.Snapshot.Sketches[seed.Id].Revision.ToString("D"),["curveA"]=seed.Arcs[0].Id.ToString(),["curveB"]=seed.Splines[0].Id.ToString(),["enabled"]=enabled};
            var create=Args();using var created=await Call("cad_edit",create);Assert.True(created.RootElement.GetProperty("success").GetBoolean(),created.RootElement.ToString());RequireG2(session.Snapshot.Sketches[seed.Id]);
            var relation=session.Snapshot.Sketches[seed.Id].Constraints.OfType<EndpointCurvatureConstraint>().Single();var before=session.Snapshot;var edit=Args();edit["constraintId"]=relation.Id.ToString();
            using var noop=await Call("cad_edit",edit);Assert.False(noop.RootElement.GetProperty("changed").GetBoolean());Assert.Same(before,session.Snapshot);
            using var inspect=await Call("cad_inspect",new{area="sketch_geometry",id=seed.Id.ToString()});
            var info=inspect.RootElement.GetProperty("items")[0].GetProperty("constraints").EnumerateArray().Single(c=>c.GetProperty("id").GetString()==relation.Id.ToString()).GetProperty("curvature");
            Assert.True(info.GetProperty("verified").GetBoolean());Assert.Equal(.25,info.GetProperty("first").GetDouble(),7);Assert.Equal(-.25,info.GetProperty("second").GetDouble(),7);
            using var stale=await Call("cad_edit",create);Assert.False(stale.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            var bad=new Dictionary<string,object>(edit){["enabled"]="false"};using var wrong=await Call("cad_edit",bad);Assert.False(wrong.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            edit["enabled"]=false;using var disabled=await Call("cad_edit",edit);Assert.True(disabled.RootElement.GetProperty("success").GetBoolean());Assert.False(session.Snapshot.Sketches[seed.Id].Constraints.OfType<EndpointCurvatureConstraint>().Single().IsEnabled);
            before=session.Snapshot;using var deleted=await Call("cad_edit",new{action="delete_sketch_curvature",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),id=seed.Id.ToString(),revision=before.Sketches[seed.Id].Revision,constraintId=relation.Id.ToString()});
            Assert.True(deleted.RootElement.GetProperty("success").GetBoolean());Assert.Empty(session.Snapshot.Sketches[seed.Id].Constraints.OfType<EndpointCurvatureConstraint>());await session.UndoAsync();Assert.Same(before,session.Snapshot);
        }
        finally{document.Detach();}
    }

    [Fact] public async Task TerminalCapturesStateAndRejectsUnknownDuplicateAndNullFields()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var seed=Owned(part);seed=seed with{Constraints=seed.Constraints.Where(c=>c is not EndpointCurvatureConstraint).ToImmutableArray()};await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();string json=JsonSerializer.Serialize(new{action="set_sketch_curvature",id=seed.Id.ToString(),revision=session.Snapshot.Sketches[seed.Id].Revision,curveA=seed.Arcs[0].Id.ToString(),curveB=seed.Splines[0].Id.ToString()});
            var captured=new CadCommandContext(document);var result=await service.ExecuteAsync("SKETCH_CURVATURE "+json,new CadCommandContext(document));Assert.True(result.Success,result.Message);var before=session.Snapshot;
            Assert.False((await service.ExecuteAsync("SKETCH_CURVATURE "+json,captured)).Success);Assert.Same(before,session.Snapshot);
            foreach(string bad in new[]{json[..^1]+",\"x\":1}",json[..^1]+",\"curveA\":\"bad\"}",json[..^1]+",\"constraintId\":null}",json+" trailing"})
            {Assert.False((await service.ExecuteAsync("SKETCH_CURVATURE "+bad,new CadCommandContext(document))).Success);Assert.Same(before,session.Snapshot);}
            var page=await service.ExecuteAsync("SKETCH_GEOMETRY",new CadCommandContext(document));Assert.True(page.Success);Assert.Contains("curvatureResidual",page.Message);
        }
        finally{document.Detach();}
    }
}
