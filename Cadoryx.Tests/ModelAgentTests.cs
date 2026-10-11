using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
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

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class ModelAgentTests
{
    [Theory][InlineData(true,"Right")][InlineData(false,"Left")]
    public async Task ThreadAuthorContractRoundTripsExplicitIntentAndRejectsUnprovedToothRepresentation(bool through,string hand)
    {
        await using var f=await Fixture.Create();using var schema=JsonDocument.Parse(CadModelContract.Read("hole"));
        Assert.True(schema.RootElement.GetProperty("schema").GetProperty("properties").TryGetProperty("thread",out _));
        var result=await f.CreateModel(new{type="hole",input=f.Input(f.Base),plane=Frame(RigidTransform3d.Translate(0,0,10)),position=new{x=5d,y=5d},
            diameter=5d,through,depth=through?0:6,thread=new{nominalMm=6,pitchMm=1d,hand,through,depthMm=through?0:5,representation="Symbolic"}});
        var id=new FeatureId(result.GetProperty("id").GetGuid());var recipe=Assert.IsType<HoleRecipe>(f.Session.Snapshot.Features[id].Recipe);
        Assert.Equal(Enum.Parse<ThreadHand>(hand),recipe.Thread!.Hand);Assert.Equal(1,recipe.Thread.PitchMm);
        var edit=await f.Edit(id);var author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();
        Assert.Equal(hand,author["thread"]!["hand"]!.GetValue<string>());var before=f.Session.Snapshot;
        Assert.False((await f.CallJson(edit.ToJsonString())).GetProperty("changed").GetBoolean());Assert.Same(before,f.Session.Snapshot);
        foreach(string field in new[]{"modeled","pitch","null","clearance"})
        {
            var invalid=author.DeepClone().AsObject();
            if(field=="modeled")invalid["thread"]!["representation"]="Modeled";
            else if(field=="pitch")invalid["thread"]!["pitchMm"]=.75;
            else if(field=="null")invalid["thread"]=null;
            else invalid["standard"]=JsonSerializer.SerializeToNode(new{nominalMm=6,fit="Normal"});
            edit["authorJson"]=invalid.ToJsonString();Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
        }
        author.Remove("thread");author["diameter"]=5.5;edit["authorJson"]=author.ToJsonString();await f.CallJson(edit.ToJsonString());
        Assert.Null(Assert.IsType<HoleRecipe>(f.Session.Snapshot.Features[id].Recipe).Thread);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task RepetitionAuthorReturnsExplicitStylePolicyAndPreservesItThroughEditing()
    {
        await using var f=await Fixture.Create();var created=await f.CreateModel(new{type="repetition",input=f.Input(f.Base),
            layout=new{kind="Linear",frame=Frame(RigidTransform3d.Identity),count=2,step=12,stylePolicy="WholeBody"}});
        var id=new FeatureId(created.GetProperty("id").GetGuid());
        Assert.Equal(RepetitionStylePolicy.WholeBody,Assert.IsType<RepetitionRecipe>(f.Session.Snapshot.Features[id].Recipe).StylePolicy);
        var edit=await f.Edit(id);var author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();
        Assert.Equal("WholeBody",author["layout"]!["stylePolicy"]!.GetValue<string>());
        author["layout"]!["stylePolicy"]="VerifiedInheritance";edit["authorJson"]=author.ToJsonString();await f.CallJson(edit.ToJsonString());
        Assert.Equal(RepetitionStylePolicy.VerifiedInheritance,Assert.IsType<RepetitionRecipe>(f.Session.Snapshot.Features[id].Recipe).StylePolicy);
        var inspected=(await f.Inspect("recipes",id.Value)).GetProperty("items")[0];Assert.Contains("WholeBody",inspected.GetProperty("copyHistoryContract").GetString());
        edit=await f.Edit(id);author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();author["layout"]!["stylePolicy"]="GuessNearest";
        edit["authorJson"]=author.ToJsonString();var before=f.Session.Snapshot;Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
    }

    [Fact]
    public async Task ExplicitCopyHistoryToolReplaysTheSelectedBranchWithoutCommittingOrGuessing()
    {
        await using var f=await Fixture.Create();var result=await f.CreateModel(await f.Author("repetition"));
        var feature=f.Session.Snapshot.Features[new(result.GetProperty("id").GetGuid())];var recipe=Assert.IsType<RepetitionRecipe>(feature.Recipe);
        var inspected=(await f.Inspect("recipes",feature.Id.Value)).GetProperty("items")[0];
        Assert.Equal(4,inspected.GetProperty("topologyHistory").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2,inspected.GetProperty("topologyHistory").GetProperty("copies").GetInt32());
        Assert.Contains("inspect_copy_history",inspected.GetProperty("copyHistoryContract").GetString());
        var sources=Parse(await f.Tools.ExecuteAsync(new("s","cad_datum",JsonSerializer.Serialize(new{action="inspect_sources",
            documentId=f.Session.Snapshot.Id.ToString(),stateId=f.Session.Snapshot.StateId.ToString(),featureId=f.Base.Id.ToString(),revision=f.Base.Result.Revision.ToString()})),default));
        var selection=sources.GetProperty("items").EnumerateArray().First(x=>x.TryGetProperty("selection",out _)).GetProperty("selection");
        var before=f.Session.Snapshot;int held=f.Assets.Count;long generation=f.Session.Generation;
        string Request(Guid copyId)=>JsonSerializer.Serialize(new{action="inspect_copy_history",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),
            id=feature.Id.ToString(),revision=feature.Result.Revision.ToString(),authorJson=JsonSerializer.Serialize(new{source=selection,repetitionId=feature.Id.ToString(),copyId})});
        var resolved=await f.CallJson(Request(recipe.CopyIds[1]));Assert.False(resolved.GetProperty("changed").GetBoolean());
        Assert.Equal("Resolved",resolved.GetProperty("status").GetString());Assert.Equal(recipe.CopyIds[1],resolved.GetProperty("copyId").GetGuid());
        Assert.Equal(feature.Result.AssetId.ToString(),resolved.GetProperty("target").GetProperty("asset").GetString());
        Assert.Equal(feature.Id.ToString(),resolved.GetProperty("selection").GetProperty("featureId").GetString());
        Assert.Equal(OcctGeometryKernel.HistoryAdapterVersion,resolved.GetProperty("selection").GetProperty("adapterVersion").GetString());
        var foreign=await f.CallJson(Request(Guid.NewGuid()));Assert.Equal("WrongContext",foreign.GetProperty("status").GetString());Assert.False(foreign.TryGetProperty("target",out _));
        var unknown=JsonNode.Parse(Request(recipe.CopyIds[1]))!.AsObject();var inner=JsonNode.Parse(unknown["authorJson"]!.GetValue<string>())!.AsObject();
        inner["targetIndex"]=12;unknown["authorJson"]=inner.ToJsonString();Assert.False(Success(await f.CallResultJson(unknown.ToJsonString())));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);Assert.Equal(generation,f.Session.Generation);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Base.Id,new BoxRecipe(11,10,10,RigidTransform3d.Identity)));
        Assert.False(Success(await f.CallResultJson(Request(recipe.CopyIds[1]))));
    }
    [Theory][InlineData("extrude")][InlineData("revolve")][InlineData("boolean")][InlineData("hole")]
    [InlineData("repetition")][InlineData("hole_pattern")][InlineData("extrusion_pattern")]
    [InlineData("sweep")][InlineData("loft")][InlineData("shell")][InlineData("draft")]
    public async Task NativeAuthorContractsCreateCopyNoOpEditUndoAndStorage(string type)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var author=await f.Author(type);
        var before=f.Session.Snapshot;var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());
        var after=f.Session.Snapshot;var feature=after.Features[id];after.Validate();Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal(id.ToString(),result.GetProperty("createdFeatureIds")[0].GetString());Assert.Equal(feature.OutputBodyId.ToString(),result.GetProperty("createdBodyIds")[0].GetString());
        Assert.True(feature.Result.VolumeMm3>0);Assert.Contains(type,(await f.Inspect("recipes",id.Value)).GetRawText());
        var input=(await f.Inspect("recipes",id.Value)).GetProperty("items")[0].GetProperty("edit");
        var count=f.Assets.Count;var generation=f.Session.Generation;
        var same=await f.CallJson(input.GetRawText());Assert.False(same.GetProperty("changed").GetBoolean());Assert.Same(after,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);Assert.Equal(count,f.Assets.Count);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var edit=JsonNode.Parse(input.GetRawText())!.AsObject();var changed=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();
        switch(type)
        {
            case "extrude":changed["distance"]=3;break;case "revolve":changed["angle"]=Math.PI;break;
            case "boolean":
                var invalid=changed.DeepClone().AsObject();invalid["operation"]="Cut";edit["authorJson"]=invalid.ToJsonString();Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(after,f.Session.Snapshot);break;
            case "hole":changed["diameter"]=1.5;break;
            case "repetition":case "hole_pattern":case "extrusion_pattern":changed["layout"]!["step"]=3;break;
            case "sweep":changed["path"]!["curves"]![0]!["end"]!["y"]=12;break;
            case "loft":changed["ruled"]=false;break;
            case "shell":changed["thickness"]=.7;break;case "draft":changed["programs"]![0]!["angle"]=.08;break;
        }
        edit["authorJson"]=changed.ToJsonString();await f.CallJson(edit.ToJsonString());f.Session.Snapshot.Validate();Assert.Equal(feature.Id,f.Session.Snapshot.Features[id].Id);
        if(type!="boolean")Assert.NotEqual(feature.Result.Revision,f.Session.Snapshot.Features[id].Result.Revision);
        var path=files.PathFor("model-"+type+".cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);loaded.Snapshot.Validate();Assert.Equal(f.Session.Snapshot.Features[id].Result,loaded.Snapshot.Features[id].Result);
        if(type is "repetition" or "hole_pattern" or "extrusion_pattern")Assert.True(result.GetProperty("createdCopyIds").GetArrayLength()>0);
        if(type=="loft")Assert.Single(result.GetProperty("createdChannelIds").EnumerateArray());
        if(type=="draft")Assert.Single(result.GetProperty("createdProgramIds").EnumerateArray());
    }

    [Theory][InlineData("extrude",24d)][InlineData("revolve",28.274333882308138)]
    [InlineData("sweep",31.41592653589793)][InlineData("loft",15.707963267948966)]
    public async Task IndependentRecipeGeometryMatchesNativeMeasurements(string type,double volume)
    {
        await using var f=await Fixture.Create();var result=await f.CreateModel(await f.Author(type));var id=new FeatureId(result.GetProperty("id").GetGuid());
        Assert.Equal(volume,f.Session.Snapshot.Features[id].Result.VolumeMm3,5);
    }

    [Fact]
    public async Task LinkedSketchDrivesParametersDrawingAndRequiresExplicitDetach()
    {
        await using var f=await Fixture.Create();var draft=new SketchDraft(CadSketch.Create(f.Part,"Linked",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=f.Session.Snapshot.Sketches[draft.Value.Id];
        var selected=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
        var author=new{type="extrude",section=new{source=CadModelJson.Write(selected)},distance=2};
        var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());var state=f.Session.Snapshot;Assert.Equal(24,state.Features[id].Result.VolumeMm3,6);
        var sheet=TechnicalDrawingSheet.A4Landscape("Model drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(100,100),1));
        var parameter=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"3 mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(id,"Distance"));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));Assert.Equal(36,f.Session.Snapshot.Features[id].Result.VolumeMm3,6);
        var edit=(await f.Edit(id));var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["distance"]=5;edit["authorJson"]=payload.ToJsonString();
        var before=f.Session.Snapshot;Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));
        edit=await f.Edit(id);payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["section"]=JsonSerializer.SerializeToNode(Section(Square(),RigidTransform3d.Identity));edit["authorJson"]=payload.ToJsonString();
        before=f.Session.Snapshot;Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
        await f.Detach(id,"detach_profile");Assert.Null(f.Session.Snapshot.Features[id].SketchSource);
        edit=await f.Edit(id);payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["distance"]=5;edit["authorJson"]=payload.ToJsonString();await f.CallJson(edit.ToJsonString());
        var after=f.Session.Snapshot;Assert.Equal(60,after.Features[id].Result.VolumeMm3,6);Assert.Equal(after.Features[id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single(x=>x.Feature==id).Revision);
        var geometry=await f.Inspect("sketch_geometry",sketch.Id.Value);Assert.Equal(sketch.Revision,geometry.GetProperty("items")[0].GetProperty("revision").GetGuid());
        Assert.Equal(sketch.Lines[0].Id.ToString(),geometry.GetProperty("items")[0].GetProperty("lines")[0].GetProperty("id").GetString());
    }

    [Theory][InlineData("extrude")][InlineData("hole")][InlineData("repetition")][InlineData("draft")]
    public async Task AssociatedPlaneCachesCannotBeForgedAndDetachRetainsEvaluatedGeometry(string type)
    {
        await using var f=await Fixture.Create();var datum=new CadDatumPlane(Guid.NewGuid(),f.Part,"Entry",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:10);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=f.Session.Snapshot.Datums.Planes[datum.Id];
        var author=JsonSerializer.SerializeToNode(await f.Author(type))!.AsObject();var plane=JsonSerializer.SerializeToNode(new{datumId=datum.Id,revision=datum.Revision});
        if(type=="hole")author["plane"]=plane;
        if(type=="extrude"){author["distance"]=0;author["termination"]="Plane";author["limit"]=plane;}
        if(type=="repetition"){author["layout"]!["kind"]="Mirror";author["layout"]!["count"]=2;author["layout"]!["step"]=0;author["layout"]!.AsObject().Remove("frame");author["layout"]!["datumId"]=datum.Id;author["layout"]!["datumRevision"]=datum.Revision;}
        if(type=="draft")author["programs"]![0]!["plane"]=plane;
        var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());var edit=await f.Edit(id);var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();
        if(type=="hole")payload["plane"]=JsonSerializer.SerializeToNode(Frame(RigidTransform3d.Translate(0,0,10)));
        if(type=="extrude")payload["limit"]=JsonSerializer.SerializeToNode(Frame(RigidTransform3d.Translate(0,0,10)));
        if(type=="repetition"){payload["layout"]!.AsObject().Remove("datumId");payload["layout"]!.AsObject().Remove("datumRevision");payload["layout"]!["frame"]=JsonSerializer.SerializeToNode(Frame(RigidTransform3d.Translate(0,0,10)));}
        if(type=="draft")payload["programs"]![0]!["plane"]=JsonSerializer.SerializeToNode(Frame(RigidTransform3d.Translate(0,0,10)));
        edit["authorJson"]=payload.ToJsonString();var before=f.Session.Snapshot;Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
        var volume=before.Features[id].Result.VolumeMm3;await f.Detach(id,"detach_datum");Assert.Equal(volume,f.Session.Snapshot.Features[id].Result.VolumeMm3,5);
        before=f.Session.Snapshot;var same=await f.Detach(id,"detach_datum");Assert.False(same.GetProperty("changed").GetBoolean());Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("state")][InlineData("revision")][InlineData("type")][InlineData("input")]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("cache")][InlineData("null-profile")]
    [InlineData("missing-coordinate")][InlineData("numeric-enum")][InlineData("budget")][InlineData("rollback")][InlineData("readonly")][InlineData("locked")]
    public async Task InvalidRequestsPreserveCompleteStateAndAssets(string failure)
    {
        await using var f=await Fixture.Create();var result=await f.CreateModel(await f.Author("extrude"));var id=new FeatureId(result.GetProperty("id").GetGuid());var edit=await f.Edit(id);
        var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();
        if(failure=="state")edit["stateId"]=Guid.NewGuid().ToString();if(failure=="revision")edit["revision"]=Guid.NewGuid().ToString();
        if(failure=="type")payload["type"]="revolve";if(failure=="input")payload["input"]=JsonSerializer.SerializeToNode(f.Input(f.Base));
        if(failure=="unknown")payload["force"]=true;if(failure=="cache")payload["section"]!["source"]=JsonSerializer.SerializeToNode(new{sketchId=Guid.NewGuid(),revision=Guid.NewGuid(),lines=Array.Empty<Guid>(),cachedProfile=Square()});
        if(failure=="null-profile")payload["section"]!["profile"]=null;
        if(failure=="missing-coordinate")payload["section"]!["profile"]!["points"]![0]!.AsObject().Remove("x");
        if(failure=="numeric-enum")payload["side"]=0;if(failure=="rollback")await f.Session.RollbackFeaturesAsync(f.Part,0);
        if(failure=="readonly")
        {using var asset=f.Assets.Stage(new byte[]{3,2,1});await f.Session.ExecuteAsync(new EditDocumentCommand("Future extension",doc=>doc with{Extensions=[new("vendor.future",1,asset.Id,"vendor")]}));edit["stateId"]=f.Session.Snapshot.StateId.ToString();}
        if(failure=="locked")
        {var layer=f.Session.Snapshot.Bodies[f.Session.Snapshot.Features[id].OutputBodyId].LayerId;await f.Session.ExecuteAsync(new EditDocumentCommand("Lock layer",doc=>doc with{Layers=doc.Layers.SetItem(layer,doc.Layers[layer] with{IsLocked=true})}));edit["stateId"]=f.Session.Snapshot.StateId.ToString();payload["distance"]=8;}
        var authorJson=payload.ToJsonString();if(failure=="duplicate")authorJson=authorJson[..^1]+",\"distance\":8}";
        edit["authorJson"]=failure=="budget"?new string(' ',32769):authorJson;
        var before=f.Session.Snapshot;var count=f.Assets.Count;var generation=f.Session.Generation;
        Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(generation,f.Session.Generation);
    }

    [Theory][InlineData("fingerprint")][InlineData("foreign")][InlineData("index")][InlineData("revision")]
    public async Task ExactFaceRequestsAreConfirmedAndWrongEvidenceNeverCommits(string failure)
    {
        await using var f=await Fixture.Create();var author=JsonSerializer.SerializeToNode(await f.Author("shell"))!.AsObject();var face=author["faces"]![0]!;
        if(failure=="fingerprint")face["fingerprint"]=new string('0',64);if(failure=="foreign")face["featureId"]=Guid.NewGuid().ToString();
        if(failure=="index")face["index"]=100000;if(failure=="revision")face["revision"]=Guid.NewGuid().ToString();
        var before=f.Session.Snapshot;var count=f.Assets.Count;Assert.False(Success(await f.CreateResult(author)));Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }

    [Fact]
    public async Task TerminalRawAuthorsCatalogContractsAndExactUndoUseTheSameImplementation()
    {
        await using var f=await Fixture.Create();var commands=new CadCommandLineService();
        async Task<CadCommandResult> Run(string text)=>await commands.ExecuteAsync(text,new CadCommandContext(f.Document));
        var contract=await Run("MODEL CONTRACT extrude");Assert.True(contract.Success,contract.Message);Assert.Contains("$defs",contract.Message);
        var text="MODEL "+JsonSerializer.Serialize(new{action="create",partId=f.Part.ToString(),name="Terminal with spaces",author=await f.Author("extrude")});
        var before=f.Session.Snapshot;var result=await Run(text);Assert.True(result.Success,result.Message);var after=f.Session.Snapshot;
        Assert.Equal("Terminal with spaces",after.Features.Values.Single(x=>x.Id!=f.Base.Id).Name);Assert.True((await Run("RECIPES 0 2")).Success);Assert.True((await Run("SKETCH_GEOMETRY")).Success);
        Assert.True((await Run("UNDO")).Success);Assert.Same(before,f.Session.Snapshot);Assert.True((await Run("REDO")).Success);Assert.Same(after,f.Session.Snapshot);
        Assert.False((await Run("MODEL {\"action\":\"create\",\"author\":{}} trailing")).Success);Assert.Same(after,f.Session.Snapshot);
    }

    [Fact]
    public async Task CompletedModelCreationStillReturnsRealIdsAfterCancellation()
    {
        await using var f=await Fixture.Create();using var cancel=new CancellationTokenSource();f.Session.CommandCommitted+=(_,_)=>cancel.Cancel();
        var text=await f.CreateResult(await f.Author("extrude"),cancel.Token);Assert.True(Success(text),text);Assert.True(cancel.IsCancellationRequested);
        var result=Parse(text);Assert.True(f.Session.Snapshot.Features.ContainsKey(new(result.GetProperty("id").GetGuid())));
    }

    [Theory][InlineData("cancel")][InlineData("state")][InlineData("close")]
    public async Task LateNativeModelResultsAreReclaimedWithoutStaleCommit(string boundary)
    {
        var kernel=new DelayedKernel();await using var f=await Fixture.Create(kernel);kernel.Delay=true;using var cancel=new CancellationTokenSource();
        var before=f.Session.Snapshot;var count=f.Assets.Count;var pending=f.CreateResult(await f.Author("extrude"),cancel.Token);
        await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(count+1,f.Assets.Count);Task? closing=null;
        if(boundary=="cancel")cancel.Cancel();if(boundary=="state"){await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));before=f.Session.Snapshot;}
        if(boundary=="close"){f.Document.Detach();closing=f.Session.DisposeAsync().AsTask();}
        var generation=f.Session.Generation;kernel.Release.TrySetResult();
        if(boundary=="state")Assert.False(Success(await pending));else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);if(closing is not null){await closing;Assert.Equal(0,f.Assets.Count);}else Assert.Equal(count,f.Assets.Count);
    }

    private static object Frame(RigidTransform3d frame)=>CadDatumJson.Frame(frame);
    [Fact]
    public async Task FailedNativeGateSetupCannotLeaveFixtureCleanupWaitingForRelease()
    {
        var kernel=new DelayedKernel();var f=await Fixture.Create(kernel);kernel.Delay=true;
        var pending=f.CreateResult(await f.Author("extrude"));
        try
        {
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await f.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.Equal(0,f.Assets.Count);
        }
        finally{kernel.Release.TrySetResult();await f.DisposeAsync();}
    }
    [Theory][InlineData("extrude","arc")][InlineData("extrude","bezier")][InlineData("extrude","spline")][InlineData("extrude","mixed")]
    [InlineData("revolve","arc")][InlineData("revolve","bezier")][InlineData("revolve","spline")][InlineData("revolve","circle")]
    public async Task CurvedAuthorProfilesRetainTheirExactShapeAcrossQueryAndEdit(string type,string kind)
    {
        await using var f=await Fixture.Create();SketchProfile profile=kind switch
        {
            "arc"=>new([]){Arc=new(new(1,0),new(2,1),new(1,2))},
            "bezier"=>new([]){Bezier=new(new(1,0),new(3,1),new(1,2))},
            "spline"=>new([]){Spline=new([new(1,0),new(3,.3),new(3,1.6),new(1,2)])},
            "mixed"=>new([]){BoundaryCurves=[new(new(1,0),new(3,0)),new(new(3,0),new(3,2),new(4,1)),new(new(3,2),new(1,2)),new(new(1,2),new(1,0))]},
            _=>SketchProfile.FromCircle(new(4,1),.75)
        };
        object author=type=="extrude"?new{type,section=Section(profile,RigidTransform3d.Identity),distance=2}:
            new{type,section=Section(profile,RigidTransform3d.Identity),angle=Math.Tau};
        var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());var feature=f.Session.Snapshot.Features[id];Assert.True(feature.Result.VolumeMm3>0);
        var actual=feature.Recipe is ExtrudeRecipe e?e.Profile:((RevolveRecipe)feature.Recipe).Profile;Assert.True(SketchProfileReference.SameProfile(profile,actual));
        var before=f.Session.Snapshot;Assert.False((await f.CallJson((await f.Edit(id)).ToJsonString())).GetProperty("changed").GetBoolean());Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("cancel")][InlineData("state")][InlineData("close")]
    public async Task LateNativeFaceConfirmationCannotPublishAfterInvalidation(string boundary)
    {
        var kernel=new DelayedKernel();await using var f=await Fixture.Create(kernel);var author=await f.Author("shell");kernel.DelayConfirm=true;using var cancel=new CancellationTokenSource();
        var before=f.Session.Snapshot;var count=f.Assets.Count;var pending=f.CreateResult(author,cancel.Token);await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(count,f.Assets.Count);Task? closing=null;
        if(boundary=="cancel")cancel.Cancel();if(boundary=="state"){await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));before=f.Session.Snapshot;}
        if(boundary=="close"){f.Document.Detach();closing=f.Session.DisposeAsync().AsTask();}var generation=f.Session.Generation;kernel.Release.TrySetResult();
        if(boundary=="state")Assert.False(Success(await pending));else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);if(closing is not null){await closing;Assert.Equal(0,f.Assets.Count);}else Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData("Linear",false)][InlineData("Circular",false)][InlineData("Circular",true)][InlineData("Mirror",false)]
    public async Task AssemblyToolsPreserveExactPathsSurvivingSlotsAndDerivedMirrorDefinitions(string kind,bool full)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var source=f.Session.Snapshot.EnumerateOccurrences().Single(x=>x.DefinitionId==f.Part);
        var author=new{type="assembly_pattern",kind,frame=Frame(RigidTransform3d.Translate(30,0,0)),count=kind=="Mirror"?2:3,step=kind=="Mirror"||full?0:kind=="Linear"?12:Math.PI/3,fullCircle=full};
        var before=f.Session.Snapshot;var args=new JsonObject{["action"]="set_assembly",["documentId"]=before.Id.ToString(),["stateId"]=before.StateId.ToString(),["path"]=source.Path.ToString(),["name"]="Instances",["authorJson"]=JsonSerializer.Serialize(author)};
        var result=await f.CallJson(args.ToJsonString());var id=result.GetProperty("id").GetGuid();var after=f.Session.Snapshot;after.Validate();var pattern=after.AssemblyPatterns[id];
        Assert.Single(result.GetProperty("createdPatternIds").EnumerateArray());Assert.Equal(pattern.CopySlots.Length,result.GetProperty("createdSlotIds").GetArrayLength());
        if(kind=="Mirror"){Assert.NotEmpty(pattern.Mirrors);Assert.NotEmpty(result.GetProperty("createdDefinitionIds").EnumerateArray());Assert.Single(result.GetProperty("createdBodyIds").EnumerateArray());}
        else{Assert.Empty(result.GetProperty("createdDefinitionIds").EnumerateArray());Assert.Empty(result.GetProperty("createdBodyIds").EnumerateArray());}
        var inspected=(await f.Inspect("assembly_patterns",id)).GetProperty("items")[0];Assert.Equal(source.Path.ToString(),inspected.GetProperty("path").GetString());
        var edit=JsonNode.Parse(inspected.GetProperty("edit").GetRawText())!.AsObject();var same=await f.CallJson(edit.ToJsonString());Assert.False(same.GetProperty("changed").GetBoolean());Assert.Same(after,f.Session.Snapshot);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        if(kind!="Mirror")
        {
            var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["count"]=4;payload.Remove("copySlots");edit["authorJson"]=payload.ToJsonString();
            var grown=await f.CallJson(edit.ToJsonString());Assert.Single(grown.GetProperty("createdSlotIds").EnumerateArray());Assert.Equal(pattern.CopySlots,f.Session.Snapshot.AssemblyPatterns[id].CopySlots.Take(pattern.CopySlots.Length));
        }
        var path=files.PathFor("assembly-tools.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(f.Session.Snapshot.AssemblyPatterns[id].CopySlots.AsEnumerable(),loaded.Snapshot.AssemblyPatterns[id].CopySlots.AsEnumerable());}
        var slots=f.Session.Snapshot.Definitions.Values.OfType<AssemblyDefinition>().SelectMany(a=>a.Children).Select(s=>s.Id).ToHashSet();
        await f.CallJson(JsonSerializer.Serialize(new{action="detach_assembly",documentId=f.Session.Snapshot.Id.ToString(),stateId=f.Session.Snapshot.StateId.ToString(),id}));
        Assert.Empty(f.Session.Snapshot.AssemblyPatterns);Assert.True(slots.SetEquals(f.Session.Snapshot.Definitions.Values.OfType<AssemblyDefinition>().SelectMany(a=>a.Children).Select(s=>s.Id)));
    }

    [Theory][InlineData("shell")][InlineData("draft")]
    public async Task StaleNativeFacesRequireExplicitReinspectionAndReselection(string type)
    {
        await using var f=await Fixture.Create();var result=await f.CreateModel(await f.Author(type));var id=new FeatureId(result.GetProperty("id").GetGuid());
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Base.Id,new BoxRecipe(11,10,10,RigidTransform3d.Identity)));Assert.True(f.Session.Snapshot.Features[id].IsStale);
        var item=(await f.Inspect("recipes",id.Value)).GetProperty("items")[0];Assert.False(item.GetProperty("available").GetBoolean());Assert.False(item.TryGetProperty("edit",out _));
        var args=JsonNode.Parse(item.GetProperty("reselectTemplate").GetRawText())!.AsObject();var payload=JsonNode.Parse(args["authorJson"]!.GetValue<string>())!.AsObject();
        f.Base=f.Session.Snapshot.Features[f.Base.Id];var current=JsonSerializer.SerializeToNode(await f.Author(type))!.AsObject();
        if(type=="shell")payload["faces"]=current["faces"]!.DeepClone();else payload["programs"]![0]!["face"]=current["programs"]![0]!["face"]!.DeepClone();
        args["authorJson"]=payload.ToJsonString();await f.CallJson(args.ToJsonString());Assert.False(f.Session.Snapshot.Features[id].IsStale);f.Session.Snapshot.Validate();
    }

    [Fact]
    public async Task AssemblyNoOpCannotUseAnotherOccurrenceAndDrivenLayoutEditsRemainAtomic()
    {
        await using var f=await Fixture.Create();var first=f.Session.Snapshot.EnumerateOccurrences().Single(x=>x.DefinitionId==f.Part);var secondPart=DefinitionId.New();await f.Session.ExecuteAsync(ResourceCommands.AddPart(secondPart,"Other"));
        var second=f.Session.Snapshot.EnumerateOccurrences().Single(x=>x.DefinitionId==secondPart);
        var result=await f.CallJson(JsonSerializer.Serialize(new{action="set_assembly",documentId=f.Session.Snapshot.Id.ToString(),stateId=f.Session.Snapshot.StateId.ToString(),path=first.Path.ToString(),name="Array",
            authorJson=JsonSerializer.Serialize(new{type="assembly_pattern",kind="Linear",frame=Frame(RigidTransform3d.Identity),count=3,step=12})}));var id=result.GetProperty("id").GetGuid();
        var edit=JsonNode.Parse((await f.Inspect("assembly_patterns",id)).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();edit["path"]=second.Path.ToString();var before=f.Session.Snapshot;
        Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
        var p=new CadParameter(Guid.NewGuid(),"spacing",ParameterDimension.Length,"15 mm");await f.Session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.AssemblyPattern(id,"Spacing"))])));
        Assert.Equal(15,f.Session.Snapshot.AssemblyPatterns[id].Step);
        edit=JsonNode.Parse((await f.Inspect("assembly_patterns",id)).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["step"]=20;edit["authorJson"]=payload.ToJsonString();before=f.Session.Snapshot;
        Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
    }
    [Theory][InlineData("Positive")][InlineData("Negative")][InlineData("Symmetric")][InlineData("TwoSides")]
    public async Task ExtrusionSidesPreserveTheirAuthoredExtent(string side)
    {
        await using var f=await Fixture.Create();var result=await f.CreateModel(new{type="extrude",section=Section(Square(),RigidTransform3d.Identity),distance=2,side,backward=side=="TwoSides"?3:0});
        var shape=f.Session.Snapshot.Features[new(result.GetProperty("id").GetGuid())].Result;
        Assert.Equal(12*(side=="TwoSides"?5:2),shape.VolumeMm3,5);
        Assert.Equal(side=="Positive"?0:side=="Negative"?-2:side=="Symmetric"?-1:-3,shape.Bounds.Min.Z,5);
        Assert.Equal(side=="Negative"?0:side=="Symmetric"?1:2,shape.Bounds.Max.Z,5);
    }

    [Theory][InlineData("Through")][InlineData("PlanarFace")][InlineData("Plane")]
    public async Task ExactExtrusionTerminationNeverConsumesTheReferenceBody(string termination)
    {
        await using var f=await Fixture.Create();await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,1,RigidTransform3d.Translate(0,0,5)),"Limit",f.Part));
        var limit=f.Session.Snapshot.Features.Values.Single(x=>x.Id!=f.Base.Id);
        var author=JsonSerializer.SerializeToNode(new{type="extrude",section=Section(SketchProfile.FromCircle(new(3,3),.5),RigidTransform3d.Identity),operation="Cut",input=f.Input(f.Base),termination})!.AsObject();
        if(termination=="Plane")author["limit"]=JsonSerializer.SerializeToNode(Frame(RigidTransform3d.Translate(0,0,5)));
        if(termination=="PlanarFace")
        {
            var page=Parse(await f.Tools.ExecuteAsync(new("s","cad_datum",JsonSerializer.Serialize(new{action="inspect_sources",documentId=f.Session.Snapshot.Id.ToString(),stateId=f.Session.Snapshot.StateId.ToString(),featureId=limit.Id.ToString(),revision=limit.Result.Revision.ToString()})),default));
            author["limitFace"]=JsonNode.Parse(page.GetProperty("items").EnumerateArray().First(x=>x.TryGetProperty("geometry",out var g)&&g.GetProperty("kind").GetString()=="Plane"&&g.GetProperty("point")[2].GetDouble()==5).GetProperty("selection").GetRawText());
        }
        var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());
        Assert.True(f.Session.Snapshot.Bodies.ContainsKey(limit.OutputBodyId));Assert.False(f.Session.Snapshot.Bodies.ContainsKey(f.Base.OutputBodyId));
        Assert.Equal(1000-Math.PI*.25*(termination=="Through"?10:5),f.Session.Snapshot.Features[id].Result.VolumeMm3,4);
    }

    [Theory][InlineData("Plain",true)][InlineData("Plain",false)][InlineData("Counterbore",true)][InlineData("Countersink",false)]
    public async Task HoleHeadsAndCatalogFieldsReachNativeDrilling(string head,bool through)
    {
        await using var f=await Fixture.Create();var author=new{type="hole",input=f.Input(f.Base),plane=Frame(RigidTransform3d.Translate(0,0,10)),position=new{x=5d,y=5d},diameter=3.4,
            standard=new{nominalMm=3,fit="Normal"},head,through,depth=through?0:6,headDiameter=head=="Plain"?0:6,headDepth=head=="Counterbore"?2:0,headAngle=head=="Countersink"?Math.PI/2:0};
        var result=await f.CreateModel(author);var id=new FeatureId(result.GetProperty("id").GetGuid());var h=Assert.IsType<HoleRecipe>(f.Session.Snapshot.Features[id].Recipe);
        Assert.Equal(MetricClearanceHoles.Diameter(h.Standard!),h.Diameter);Assert.True(f.Session.Snapshot.Features[id].Result.VolumeMm3<1000);
        var edit=await f.Edit(id);var payload=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();payload["diameter"]=3.5;edit["authorJson"]=payload.ToJsonString();var before=f.Session.Snapshot;
        Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("Mirror",false)][InlineData("Mirror",true)][InlineData("Linear",false)][InlineData("Circular",false)][InlineData("Circular",true)]
    public async Task RepetitionEditsRetainSurvivingCopiesAndReturnOnlyNewIds(string kind,bool flag)
    {
        await using var f=await Fixture.Create();var count=kind=="Mirror"?(flag?2:1):3;
        var result=await f.CreateModel(new{type="repetition",input=f.Input(f.Base),layout=new{kind,frame=Frame(RigidTransform3d.Translate(30,0,0)),count,
            step=kind=="Mirror"||kind=="Circular"&&flag?0:kind=="Linear"?12:Math.PI/3,includeSource=kind=="Mirror"?flag:true,fullCircle=kind=="Circular"&&flag}});
        var id=new FeatureId(result.GetProperty("id").GetGuid());var old=Assert.IsType<RepetitionRecipe>(f.Session.Snapshot.Features[id].Recipe);
        if(kind=="Mirror"){Assert.Equal(count*1000,f.Session.Snapshot.Features[id].Result.VolumeMm3,5);return;}
        var edit=await f.Edit(id);var author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();author["layout"]!["count"]=4;author["layout"]!.AsObject().Remove("copyIds");edit["authorJson"]=author.ToJsonString();
        var resized=await f.CallJson(edit.ToJsonString());var actual=Assert.IsType<RepetitionRecipe>(f.Session.Snapshot.Features[id].Recipe);Assert.Equal(old.CopyIds,actual.CopyIds.Take(old.Count));
        Assert.Single(resized.GetProperty("createdCopyIds").EnumerateArray());Assert.Equal(actual.CopyIds[^1],resized.GetProperty("createdCopyIds")[0].GetGuid());
        edit=await f.Edit(id);author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();author["layout"]!["copyIds"]![0]=Guid.NewGuid().ToString();edit["authorJson"]=author.ToJsonString();
        var before=f.Session.Snapshot;Assert.False(Success(await f.CallResultJson(edit.ToJsonString())));Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("extrude")][InlineData("sweep")][InlineData("loft")]
    public async Task NestedHolesAndIslandsKeepExplicitCorrespondenceAndNativeVolume(string type)
    {
        await using var f=await Fixture.Create();var profile=SketchProfile.FromCircle(new(0,0),3) with{Holes=[new(new(0,0),2)],Islands=[new([],new(new(0,0),1))]};
        object author;
        if(type=="extrude")author=new{type,section=Section(profile,RigidTransform3d.Identity),distance=10};
        else if(type=="sweep")author=new{type,section=Section(profile,RigidTransform3d.Identity),path=new{curves=CadModelJson.Write(ImmutableArray.Create(new SketchBoundaryCurve(new(0,0),new(0,10)))),frame=Frame(new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)))}};
        else
        {
            var root=Guid.NewGuid();var hole=Guid.NewGuid();var island=Guid.NewGuid();var frames=new[]{RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,10)};
            object Channel(Guid id,Guid? parent,double radius)=>new{id,parent,sections=frames.Select(frame=>Section(SketchProfile.FromCircle(new(0,0),radius),frame)).ToArray()};
            author=new{type,stations=frames.Select(Frame).ToArray(),channels=new[]{Channel(island,hole,1),Channel(root,null,3),Channel(hole,root,2)},ruled=true};
        }
        var result=await f.CreateModel(author);var feature=f.Session.Snapshot.Features[new(result.GetProperty("id").GetGuid())];Assert.Equal(60*Math.PI,feature.Result.VolumeMm3,4);Assert.Equal(BodyKind.Compound,feature.Result.Kind);
        var edit=await f.Edit(feature.Id);Assert.False((await f.CallJson(edit.ToJsonString())).GetProperty("changed").GetBoolean());
    }

    [Theory][InlineData("Solid")][InlineData("Sheet")]
    public async Task LinkedDirectedSweepPathIsResolvedByActualSketchRevision(string output)
    {
        await using var f=await Fixture.Create();var section=new SketchDraft(CadSketch.Create(f.Part,"Section",RigidTransform3d.Identity));section.AddCircle(new(0,0),1);
        await f.Session.ExecuteAsync(new UpsertSketchCommand(section.Value,new ManagedSketchConstraintSolver()));var s=f.Session.Snapshot.Sketches[section.Value.Id];
        var path=new SketchDraft(CadSketch.Create(f.Part,"Directed path",new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));path.AddLine(new(0,10),new(0,0));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(path.Value,new ManagedSketchConstraintSolver()));var p=f.Session.Snapshot.Sketches[path.Value.Id];
        var author=new{type="sweep",output,section=new{source=CadModelJson.Write(SketchProfileReference.CreateCircle(s,s.Circles.Single().Id))},
            path=new{source=CadModelJson.Write(new SketchPathReference(p.Id,p.Revision,[new(p.Lines.Single().Id,true)]))}};
        var result=await f.CreateModel(author);var feature=f.Session.Snapshot.Features[new(result.GetProperty("id").GetGuid())];var recipe=Assert.IsType<SweepRecipe>(feature.Recipe);
        Assert.Equal(new Point2d(0,0),recipe.Path.Curves[0].Start);Assert.Equal(10,feature.Result.Bounds.Max.Z,5);Assert.Equal(output=="Solid"?BodyKind.Solid:BodyKind.Sheet,feature.Result.Kind);
        var edit=await f.Edit(feature.Id);Assert.False((await f.CallJson(edit.ToJsonString())).GetProperty("changed").GetBoolean());
        await f.Detach(feature.Id,"detach_profile");recipe=Assert.IsType<SweepRecipe>(f.Session.Snapshot.Features[feature.Id].Recipe);Assert.Null(recipe.Path.Source);Assert.Null(recipe.Section.Source);
    }

    [Fact]
    public async Task ModelContractDiscoveryPublishesEverySupportedTypeAndResolvableDefinitions()
    {
        await using var f=await Fixture.Create();foreach(var type in CadModelContract.Types)
        {
            var combined=new JsonObject();JsonElement schema=default;var offset=0;bool more;
            do
            {
                var json=await f.Tools.ExecuteAsync(new("c","cad_inspect",JsonSerializer.Serialize(new{area="model_contract",id=type,offset,limit=50})),default);Assert.True(json.Length<=5500,json.Length.ToString());
                var contract=Parse(json);Assert.True(contract.GetProperty("success").GetBoolean(),json);Assert.Equal(type,contract.GetProperty("type").GetString());schema=contract.GetProperty("schema");
                foreach(var definition in schema.GetProperty("$defs").EnumerateObject()){Assert.False(combined.ContainsKey(definition.Name));combined[definition.Name]=JsonNode.Parse(definition.Value.GetRawText());}
                more=contract.GetProperty("hasMore").GetBoolean();var next=contract.GetProperty("nextOffset").GetInt32();Assert.True(!more||next>offset);offset=next;
                if(!more)Assert.Equal(contract.GetProperty("total").GetInt32(),combined.Count);
            }while(more);
            var definitions=JsonSerializer.SerializeToElement(combined);
            void Verify(JsonElement node)
            {
                if(node.ValueKind==JsonValueKind.Object)foreach(var property in node.EnumerateObject())if(property.Name=="$ref")Assert.True(definitions.TryGetProperty(property.Value.GetString()!["#/$defs/".Length..],out _));else Verify(property.Value);
                else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Verify(child);
            }
            Verify(schema);
            Verify(definitions);
        }
    }
    private static object Section(SketchProfile profile,RigidTransform3d frame)=>new{profile=CadModelJson.Write(profile),frame=Frame(frame)};
    private static SketchProfile Square()=>new([new(0,0),new(4,0),new(4,3),new(0,3)]);
    private static bool Success(string json)=>Parse(json).GetProperty("success").GetBoolean();
    private static JsonElement Parse(string json){using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly IGeometryKernel kernel;
        internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal CadAgentToolset Tools{get;}
        internal FeatureDefinition Base{get;set;}=null!;internal DefinitionId Part=>Base.PartId;
        private Fixture(IGeometryKernel kernel){this.kernel=kernel;Session=new(DocumentSnapshot.Create("Model tools"),Assets,kernel,new InlineSessionDispatcher());Document=new(Session,kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);}
        internal static async Task<Fixture> Create(IGeometryKernel? kernel=null){var f=new Fixture(kernel??new OcctGeometryKernel());await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Base"));f.Base=f.Session.Snapshot.Features.Values.Single();return f;}
        internal object Input(FeatureDefinition feature)=>new{featureId=feature.Id.ToString(),revision=Session.Snapshot.Features[feature.Id].Result.Revision.ToString()};
        internal async Task<object> Author(string type)
        {
            var circle=SketchProfile.FromCircle(new(0,0),1);var frame=RigidTransform3d.Identity;
            object Layout(string output="Compound")=>new{kind="Linear",frame=Frame(frame),count=2,step=2,output};
            object Hole()=>new{type="hole",input=Input(Base),plane=Frame(RigidTransform3d.Translate(0,0,10)),position=new{x=3d,y=3d},diameter=1d};
            switch(type)
            {
                case "extrude":return new{type,section=Section(Square(),frame),distance=2};
                case "revolve":return new{type,section=Section(new SketchProfile([new(1,0),new(2,0),new(2,3),new(1,3)]),frame),angle=Math.Tau};
                case "boolean":await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,4,4,RigidTransform3d.Translate(8,0,0)),"Second",Part));var other=Session.Snapshot.Features.Values.Single(f=>f.Id!=Base.Id);return new{type,operation="Fuse",inputs=new[]{Input(Base),Input(other)}};
                case "hole":return Hole();case "repetition":return new{type,input=Input(Base),layout=Layout()};
                case "hole_pattern":var h=await CreateModel(Hole());return new{type,input=Input(Session.Snapshot.Features[new(h.GetProperty("id").GetGuid())]),layout=Layout("Fused")};
                case "extrusion_pattern":
                    var ex=await CreateModel(new{type="extrude",section=Section(SketchProfile.FromCircle(new(3,3),.5),RigidTransform3d.Translate(0,0,10)),distance=2,operation="Cut",side="Negative",input=Input(Base)});
                    return new{type,input=Input(Session.Snapshot.Features[new(ex.GetProperty("id").GetGuid())]),layout=Layout("Fused")};
                case "sweep":return new{type,section=Section(circle,frame),path=new{curves=CadModelJson.Write(ImmutableArray.Create(new SketchBoundaryCurve(new(0,0),new(0,10)))),frame=Frame(new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)))}};
                case "loft":return new{type,stations=new[]{Frame(frame),Frame(RigidTransform3d.Translate(0,0,5))},channels=new[]{new{sections=new[]{Section(circle,frame),Section(circle,RigidTransform3d.Translate(0,0,5))}}},ruled=true};
                case "shell":case "draft":
                    var sources=Parse(await Tools.ExecuteAsync(new("s","cad_datum",JsonSerializer.Serialize(new{action="inspect_sources",documentId=Session.Snapshot.Id.ToString(),stateId=Session.Snapshot.StateId.ToString(),featureId=Base.Id.ToString(),revision=Base.Result.Revision.ToString()})),default));Assert.True(sources.GetProperty("success").GetBoolean());
                    var planes=sources.GetProperty("items").EnumerateArray().Where(x=>x.TryGetProperty("geometry",out var g)&&g.GetProperty("kind").GetString()=="Plane").ToArray();
                    var top=planes.First(x=>x.GetProperty("geometry").GetProperty("point")[2].GetDouble()==10);
                    var left=planes.First(x=>Math.Abs(x.GetProperty("geometry").GetProperty("direction")[0].GetDouble())>.9);
                    if(type=="shell")return new{type,input=Input(Base),faces=new[]{top.GetProperty("selection")},thickness=1};
                    return new{type,input=Input(Base),programs=new[]{new{face=left.GetProperty("selection"),angle=.05,plane=Frame(frame),pull=new[]{0d,0d,1d}}}};
                default:throw new ArgumentException(type);
            }
        }
        internal Task<string> CreateResult(object author,CancellationToken token=default)=>CallResultJson(JsonSerializer.Serialize(new{action="create",documentId=Session.Snapshot.Id.ToString(),stateId=Session.Snapshot.StateId.ToString(),partId=Part.ToString(),name="Model",authorJson=JsonSerializer.Serialize(author)}),token);
        internal async Task<JsonElement> CreateModel(object author)=>Checked(await CreateResult(author));
        internal Task<string> CallResultJson(string json,CancellationToken token=default)=>Tools.ExecuteAsync(new("1","cad_model",json),token);
        internal async Task<JsonElement> CallJson(string json)=>Checked(await CallResultJson(json));
        internal async Task<JsonElement> Inspect(string area,Guid? id=null)=>Checked(await Tools.ExecuteAsync(new("i","cad_inspect",JsonSerializer.Serialize(new{area,id=id?.ToString()},new JsonSerializerOptions{DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull})),default));
        internal async Task<JsonObject> Edit(FeatureId id)=>JsonNode.Parse((await Inspect("recipes",id.Value)).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();
        internal async Task<JsonElement> Detach(FeatureId id,string action)=>await CallJson(JsonSerializer.Serialize(new{action,documentId=Session.Snapshot.Id.ToString(),stateId=Session.Snapshot.StateId.ToString(),id=id.ToString(),revision=Session.Snapshot.Features[id].Result.Revision.ToString()}));
        private static JsonElement Checked(string json){Assert.True(Success(json),json);return Parse(json);}
        public async ValueTask DisposeAsync()
        {
            // A failed readiness/asset assertion must not leave the deliberately held
            // native result blocking session teardown and hide the original failure.
            Document.Detach();var closing=Session.DisposeAsync();
            if(kernel is DelayedKernel delayed)delayed.Release.TrySetResult();
            await closing;Assert.Equal(0,Assets.Count);
        }
    }
    private sealed class DelayedKernel:IGeometryKernel,IExactTopologyResolver,IDatumGeometryResolver
    {
        private readonly OcctGeometryKernel native=new();internal bool Delay{get;set;}internal bool DelayConfirm{get;set;}
        internal TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {var result=await native.EvaluateAsync(recipe,assets,CancellationToken.None);if(Delay){Delay=false;Entered.TrySetResult();await Release.Task;}return result;}
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public async Task ConfirmAsync(DocumentSnapshot doc,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)
        {await native.ConfirmAsync(doc,selection,assets,CancellationToken.None);if(DelayConfirm){DelayConfirm=false;Entered.TrySetResult();await Release.Task;}}
        public Task<ExactDatumGeometry> ResolveDatumGeometryAsync(DocumentSnapshot doc,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)=>native.ResolveDatumGeometryAsync(doc,selection,assets,token);
        public Task ConfirmDatumGeometryAsync(ExactDatumGeometry evidence,IAssetStore assets,CancellationToken token=default)=>native.ConfirmDatumGeometryAsync(evidence,assets,token);
        public Task<DatumSourcePage> InspectDatumSourcesAsync(DocumentSnapshot doc,FeatureId feature,GeometryRevisionId revision,IAssetStore assets,int offset,int limit,CancellationToken token=default)=>native.InspectDatumSourcesAsync(doc,feature,revision,assets,offset,limit,token);
    }
}

// These tests deliberately hold native results across invalidation. Running the
// setup against other collections' global native queue makes the readiness guard
// measure unrelated geometry work rather than entry into the held operation.
[CollectionDefinition("ModelAgentNativeGates",DisableParallelization=true)]
public sealed class ModelAgentNativeGateCollection;
