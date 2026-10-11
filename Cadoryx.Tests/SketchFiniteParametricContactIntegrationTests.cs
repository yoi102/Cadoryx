using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cadoryx.AI.Contracts;
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
using MessagePack;
using Xunit;
using static Cadoryx.Tests.SketchFiniteParametricContactSolveTests;

namespace Cadoryx.Tests;

public sealed class SketchFiniteParametricContactIntegrationTests
{
    private static ParameterBook Drive(CadSketch sketch,double value)
    {
        var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var parameter=new CadParameter(Guid.NewGuid(),"contact_parameter",ParameterDimension.Scalar,value.ToString("R",CultureInfo.InvariantCulture));
        return new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(sketch.Id,c.Id,"ParameterA"))]);
    }

    [Fact]
    public async Task ExplicitContactParameterRebuildsExactBodyAndDrawingInOneCommit()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new UpsertSketchCommand(Pair() with{PartId=part},new ManagedSketchConstraintSolver()));
            var seed=session.Snapshot.Sketches.Values.Single();var c=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            var source=SketchProfileReference.CreateBezierSegment(seed,c.B);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,seed.Plane)),"Contact driven body",part,sketchSource:source));
            double Expected(CadSketch s)
            {
                var b=s.Beziers.Single(b=>b.Id==c.B);Point2d P(SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
                var a=P(b.Start);var e=P(b.End);var control=P(b.Control);
                // Exact quadratic arch/chord signed area is one third of this cross product.
                return 2*Math.Abs((e.X-a.X)*(control.Y-a.Y)-(e.Y-a.Y)*(control.X-a.X))/3;
            }
            Assert.Equal(Expected(seed),session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,6);
            var sheet=TechnicalDrawingSheet.A4Landscape("Finite contact");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(seed,.41)));var after=session.Snapshot;var solved=after.Sketches[seed.Id];
            SketchFiniteParametricContact.RequireSolvedRelations(solved);RequireNativeGeometry(solved,c.A,.41);RequireNativeGeometry(solved,c.B,c.ParameterB);
            var feature=after.Features.Values.Single();var view=after.DrawingSheets[sheet.Id].Views.Single();
            Assert.Equal(Expected(solved),feature.Result.VolumeMm3,6);Assert.NotEqual(before.Features[feature.Id].Result.Revision,feature.Result.Revision);
            Assert.Equal(solved.Revision,feature.SketchSource!.Revision);Assert.False(feature.IsStale);Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Equal(feature.Result.Revision,view.Sources.Single().Revision);
            long generation=session.Generation;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(solved,1))));
            Assert.Same(after,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();string path=files.PathFor("contact-body.cadoryx");await session.SaveAsync(storage,path);
            using var reopened=await storage.LoadAsync(path,assets);reopened.Snapshot.Validate();SketchFiniteParametricContact.RequireSolvedRelations(reopened.Snapshot.Sketches[seed.Id]);
            Assert.Equal(view.Sources.ToArray(),reopened.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task IndependentPartKeepsLocalContactIdentitiesAndItsOwnParameterDriver()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(Pair("Nonuniform",true) with{PartId=part},new ManagedSketchConstraintSolver()));
        var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Copy",RigidTransform3d.Translate(20,0,0)));
        var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
        var original=session.Snapshot.Sketches.Values.Single(s=>s.PartId==part);var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
        Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Constraints.ToArray(),copied.Constraints.ToArray());
        await session.ExecuteAsync(new SetParametersCommand(Drive(copied,.41)));var changed=session.Snapshot.Sketches[copied.Id];
        SketchFiniteParametricContact.RequireSolvedRelations(changed);Assert.Same(original,session.Snapshot.Sketches[original.Id]);
        Assert.Equal(.41,changed.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);
    }

    [Theory][InlineData("Bezier")][InlineData("Nonuniform")]
    public async Task ReparameterizedTrimSplitOrExtensionDropsContactInsteadOfReinterpretingIt(string kind)
    {
        var report=await new ManagedSketchConstraintSolver().SolveAsync(Pair(kind));Assert.True(report.Succeeded);var s=report.Solution!;
        var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var trimmed=kind=="Bezier"?SketchExactBezierEditing.Keep(s,c.B,.1,.9):SketchExactSplineEditing.Keep(s,c.B,.1,.9);
        var split=kind=="Bezier"?SketchExactBezierEditing.Remove(s,c.B,.3,.7):SketchExactSplineEditing.Remove(s,c.B,.3,.7);
        var extended=kind=="Bezier"?SketchExactBezierEditing.Extend(s,c.B,false,.1):SketchExactSplineEditing.Extend(s,c.B,false,.1);
        foreach(var edited in new[]{trimmed,split,extended}){edited.Validate();Assert.Empty(edited.Constraints.OfType<FiniteParametricContactConstraint>());}
        var draft=new SketchDraft(s);draft.RemoveEntities([c.B]);Assert.Empty(draft.Value.Constraints.OfType<FiniteParametricContactConstraint>());
        draft.Undo();Assert.Equal(c,draft.Value.Constraints.OfType<FiniteParametricContactConstraint>().Single());
    }

    [Fact]
    public async Task TerminalRequiresBothParametersDirectionAndFreshCapturedState()
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var seed=Pair() with{PartId=part};seed=seed with{Constraints=seed.Constraints.Where(c=>c is not FiniteParametricContactConstraint).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();string json=JsonSerializer.Serialize(new{action="set_sketch_finite_contact",id=seed.Id.ToString(),revision=session.Snapshot.Sketches[seed.Id].Revision,
                curveA=seed.Beziers[0].Id.ToString(),curveB=seed.Beziers[1].Id.ToString(),parameterA=.37,parameterB=.61,direction="SameDirection"});
            var captured=new CadCommandContext(document);var result=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+json,new CadCommandContext(document));
            Assert.True(result.Success,result.Message);var before=session.Snapshot;
            Assert.False((await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+json,captured)).Success);Assert.Same(before,session.Snapshot);
            foreach(string bad in new[]{json[..^1]+",\"unknown\":1}",json[..^1]+",\"parameterA\":0.3}",json[..^1]+",\"constraintId\":null}",json+" trailing",json.Replace("\"parameterA\":0.37,",""),json.Replace("\"direction\":\"SameDirection\"","\"direction\":\"Automatic\"")})
            {Assert.False((await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+bad,new CadCommandContext(document))).Success);Assert.Same(before,session.Snapshot);}
            var page=await service.ExecuteAsync("SKETCH_GEOMETRY",new CadCommandContext(document));Assert.True(page.Success);Assert.Contains("finiteContact",page.Message);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactIdentityParametersAndBranchRoundtripButLegacyEighteenCannotSmuggleContact(bool enabled)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var solve=await new ManagedSketchConstraintSolver().SolveAsync(Pair("Nonuniform",true));Assert.True(solve.Succeeded);
        var sketch=solve.Solution! with{PartId=part};var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        sketch=sketch with{Constraints=sketch.Constraints.Select(v=>v.Id==c.Id?c with{IsEnabled=enabled}:v).ToImmutableArray()};
        var doc=initial with{Sketches=initial.Sketches.Add(sketch.Id,sketch)};string path=files.PathFor("finite-contact.cadoryx");
        await storage.SaveAsync(doc,assets,path);var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-finite-parametric-contact.1",manifest.RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,assets))
        {Assert.Equal(sketch.Constraints.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Constraints.ToArray());Assert.Equal(sketch.Revision,loaded.Snapshot.Sketches[sketch.Id].Revision);}
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=18}:s.Kind=="document"?s with{SchemaVersion=48}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        var legacy=sketch with{Constraints=sketch.Constraints.Where(v=>v.Id!=c.Id).ToImmutableArray()};
        await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(legacy.Id,legacy)},assets,path);
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=18}:s.Kind=="document"?s with{SchemaVersion=48}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);Assert.Equal(legacy.Constraints.ToArray(),migrated.Snapshot.Sketches[legacy.Id].Constraints.ToArray());
    }

    [Theory][InlineData("arity")][InlineData("fractional-branch")][InlineData("nan")][InlineData("endpoint")][InlineData("foreign")][InlineData("same-curve")]
    public async Task MalformedDisabledContactCannotEnterStorage(string mutation)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var sketch=Pair() with{PartId=part};
        sketch=sketch with{Constraints=sketch.Constraints.Select(c=>c is FiniteParametricContactConstraint?c with{IsEnabled=false}:c).ToImmutableArray()};
        string path=files.PathFor("malformed-contact.cadoryx");await storage.SaveAsync(initial with{Sketches=initial.Sketches.Add(sketch.Id,sketch)},assets,path);
        FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var constraints=pack.Sketches[0].Constraints;int index=Array.FindIndex(constraints,c=>c.Kind=="finite-parametric-contact");var c=constraints[index];
            constraints[index]=mutation switch
            {"arity"=>c with{Values=[.37,.61]},"fractional-branch"=>c with{Values=[.37,.61,1.5]},"nan"=>c with{Values=[double.NaN,.61,1]},
                "endpoint"=>c with{Values=[1,.61,1]},"same-curve"=>c with{Targets=[c.Targets[0],c.Targets[0]]},_=>c with{Targets=[Guid.NewGuid(),c.Targets[1]]}};
            return MessagePackSerializer.Serialize(pack);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task ScalarParameterEditsSolveAtomicallyAndInvalidEndpointKeepsStateAndHistory()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var seed=Pair() with{PartId=part};await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));
        var constraint=session.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var target=ParameterTarget.Sketch(seed.Id,constraint.Id,"ParameterA");Assert.Contains(target,ParameterTargets.All(session.Snapshot));
        Assert.Equal(ParameterQuantity.Of(.37,ParameterDimension.Scalar),ParameterTargets.Read(session.Snapshot,target));
        var parameter=new CadParameter(Guid.NewGuid(),"contact_t",ParameterDimension.Scalar,"0.41");var book=new ParameterBook([parameter],[new(Guid.NewGuid(),parameter.Id,target)]);
        await session.ExecuteAsync(new SetParametersCommand(book));var committed=session.Snapshot;var solved=committed.Sketches[seed.Id];
        Assert.Equal(.41,solved.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);SketchFiniteParametricContact.RequireSolvedRelations(solved);
        using var files=new TestFiles();var storage=new CadDocumentStorage();string path=files.PathFor("parameter-contact.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets)){SketchFiniteParametricContact.RequireSolvedRelations(loaded.Snapshot.Sketches[seed.Id]);Assert.Equal(.41,ParameterTargets.Read(loaded.Snapshot,target).Value);}
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[parameter with{Expression="1"}]})));
        Assert.Same(committed,session.Snapshot);await session.UndoAsync();await session.RedoAsync();Assert.Same(committed,session.Snapshot);
    }

    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task EditorRetainsSeparateParametersThroughApplyUndoRedoAndPreview(string language)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
            var seed=Pair() with{PartId=part};seed=seed with{Constraints=seed.Constraints.Where(c=>c is not FiniteParametricContactConstraint).ToImmutableArray()};
            await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(),session.Snapshot.Sketches[seed.Id],false);
                editor.ConstraintKind="FiniteParametricContact";editor.ContactParameterA=.37;editor.ContactParameterB=.61;
                editor.SelectedEntities.Add(seed.Beziers[0].Id);editor.SelectedEntities.Add(seed.Beziers[1].Id);editor.AddConstraintCommand.Execute(null);
                var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();Assert.True(editor.ShowFiniteContact);
                editor.SelectedConstraint=editor.Constraints.Single(v=>v.Id==c.Id);editor.ContactParameterA=.41;editor.ContactParameterB=.58;editor.ApplyFiniteContactCommand.Execute(null);
                Assert.Equal(.41,editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);
                editor.UndoCommand.Execute(null);Assert.Equal(.37,editor.ContactParameterA);Assert.Equal(.61,editor.ContactParameterB);
                editor.RedoCommand.Execute(null);Assert.Equal(.41,editor.ContactParameterA);Assert.Equal(.58,editor.ContactParameterB);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                Assert.Equal(Cadoryx.Lang.Strings.Strings.FiniteContactRelation,editor.ConstraintKinds.Single(v=>v.Key=="FiniteParametricContact").Label);
            }
            finally{document.Detach();}
        }
        finally{CultureInfo.CurrentUICulture=previous;}
    }

    [Fact]
    public async Task AgentUsesExactRevisionDoesNotInventParametersAndSupportsNoopDisableAndDelete()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var seed=Pair() with{PartId=part};seed=seed with{Constraints=seed.Constraints.Where(c=>c is not FiniteParametricContactConstraint).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(seed,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);
            Task<string> Call(object args)=>tools.ExecuteAsync(new AiToolCall(Guid.NewGuid().ToString("N"),"cad_edit",JsonSerializer.Serialize(args)),default);
            Dictionary<string,object> Args()=>new(){["action"]="set_sketch_finite_contact",["documentId"]=session.Snapshot.Id.ToString(),["stateId"]=session.Snapshot.StateId.ToString(),
                ["id"]=seed.Id.ToString(),["revision"]=session.Snapshot.Sketches[seed.Id].Revision,["curveA"]=seed.Beziers[0].Id.ToString(),["curveB"]=seed.Beziers[1].Id.ToString(),
                ["parameterA"]=.37,["parameterB"]=.61,["direction"]="SameDirection"};
            var createdArgs=Args();using(var created=JsonDocument.Parse(await Call(createdArgs)))Assert.True(created.RootElement.GetProperty("success").GetBoolean(),created.RootElement.ToString());
            var c=session.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single();var before=session.Snapshot;var edit=Args();edit["constraintId"]=c.Id.ToString();
            using(var noop=JsonDocument.Parse(await Call(edit)))Assert.False(noop.RootElement.GetProperty("changed").GetBoolean());Assert.Same(before,session.Snapshot);
            var bad=new Dictionary<string,object>(edit);bad.Remove("parameterA");using(var failed=JsonDocument.Parse(await Call(bad)))Assert.False(failed.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            using(var stale=JsonDocument.Parse(await Call(createdArgs)))Assert.False(stale.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            using(var inspected=JsonDocument.Parse(await tools.ExecuteAsync(new AiToolCall("read","cad_inspect",JsonSerializer.Serialize(new{area="sketch_geometry",id=seed.Id.ToString()})),default)))
                Assert.True(inspected.RootElement.GetProperty("items")[0].GetProperty("constraints").EnumerateArray().Single(v=>v.GetProperty("id").GetString()==c.Id.ToString()).GetProperty("finiteContact").GetProperty("verified").GetBoolean());
            edit["enabled"]=false;using(var disabled=JsonDocument.Parse(await Call(edit)))Assert.True(disabled.RootElement.GetProperty("success").GetBoolean());
            var current=session.Snapshot;using(var deleted=JsonDocument.Parse(await Call(new{action="delete_sketch_finite_contact",documentId=current.Id.ToString(),stateId=current.StateId.ToString(),id=seed.Id.ToString(),revision=current.Sketches[seed.Id].Revision,constraintId=c.Id.ToString()})))
                Assert.True(deleted.RootElement.GetProperty("success").GetBoolean());Assert.Empty(session.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>());
        }
        finally{document.Detach();}
    }
}
