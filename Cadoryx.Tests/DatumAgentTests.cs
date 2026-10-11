using System.Text.Json;
using System.Text.Json.Nodes;
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
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class DatumAgentTests
{
    [Theory][InlineData(33)][InlineData(-75)][InlineData(157)]
    public async Task CopyingAnIndependentPlaneFrameDoesNotCreateRoundoffOnlyHistory(double degrees)
    {
        await using var f=await Fixture.Create();var frame=new RigidTransform3d(new(7,11,13),Quaterniond.FromAxisAngle(new Vector3d(1,2,3).Normalized(),degrees*Math.PI/180));
        var id=await f.CreateDatum("plane","Rotated",new{kind="Offset",primary=new{kind="Independent",frame=CadDatumJson.Frame(frame)}});
        var input=(await f.Inspect(new{area="datums",id})).GetProperty("items")[0].GetProperty("edit");var before=f.Session.Snapshot;
        Assert.False((await f.CallJson(input.GetRawText())).GetProperty("changed").GetBoolean());Assert.Same(before,f.Session.Snapshot);
    }
    [Fact]
    public async Task AllAuthoredConstructionsUseExactCrossTypeDependenciesAndReusableInput()
    {
        await using var f=await Fixture.Create();
        var xy=await f.CreateDatum("plane","XY3",new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=3});
        var yz=await f.CreateDatum("plane","YZ5",new{kind="Offset",primary=new{kind="Basis",basis="YZ"},value=5});
        var xz=await f.CreateDatum("plane","XZ7",new{kind="Offset",primary=new{kind="Basis",basis="XZ"},value=7});
        var top=await f.CreateDatum("plane","Top",new{kind="Offset",primary=new{kind="BoxFace",feature=f.Base.Id.ToString(),boundary="ZMax"},value=2});
        Assert.Equal(6,f.Session.Snapshot.Datums.Planes[top].Frame.Translation.Z,8);
        var independent=await f.CreateDatum("plane","Independent",new{kind="Offset",primary=new{kind="Independent",frame=Frame(0,0,30)}});
        var mid=await f.CreateDatum("plane","Midplane",new{kind="Midplane",primary=new{kind="Datum",datum=xy},secondary=new{kind="Datum",datum=independent}});
        Assert.Equal(16.5,f.Session.Snapshot.Datums.Planes[mid].Frame.Translation.Z,8);
        var first=await f.CreateDatum("point","Corner",new{kind="ThreePlanes",first=xy,second=yz,third=xz});
        var second=await f.CreateDatum("point","Offset",new{kind="Offset",first,value=new[]{2d,4d,6d}});
        var middle=await f.CreateDatum("point","Middle",new{kind="Midpoint",first,second});
        var origin=await f.CreateDatum("point","Origin",new{kind="Independent",value=new[]{0d,0d,0d}});
        Assert.True((f.Session.Snapshot.Datums.Points[first].Position-new Vector3d(5,7,3)).Length<1e-8);
        Assert.True((f.Session.Snapshot.Datums.Points[middle].Position-new Vector3d(6,9,6)).Length<1e-8);
        var basis=await f.CreateDatum("axis","X basis",new{kind="Basis",basis="X",origin=new[]{0d,0d,3d},offset=1,reverse=true});
        var two=await f.CreateDatum("axis","Two points",new{kind="TwoPoints",first,second});
        var intersection=await f.CreateDatum("axis","Intersection",new{kind="PlaneIntersection",first=xy,second=yz});
        var normal=await f.CreateDatum("axis","Normal",new{kind="PlaneNormal",first=xy});
        await f.CreateDatum("axis","Independent axis",new{kind="Independent",origin=new[]{1d,2d,3d},direction=new[]{0d,0d,2d}});
        var hit=await f.CreateDatum("point","Axis-plane",new{kind="AxisPlane",first=intersection,second=xz});
        Assert.True((f.Session.Snapshot.Datums.Points[hit].Position-new Vector3d(5,7,3)).Length<1e-8);
        var perpendicular=await f.CreateDatum("plane","Axis normal",new{kind="Offset",primary=new{kind="AxisNormal",datum=two,point=origin}});
        Assert.True((f.Session.Snapshot.Datums.Planes[perpendicular].Frame.Translation-Vector3d.Zero).Length<1e-8);
        var angled=await f.CreateDatum("plane","Angle",new{kind="Angle",primary=new{kind="Datum",datum=xy},value=Math.PI/2,hingeAxis=basis});
        Assert.True(Math.Abs(f.Session.Snapshot.Datums.Planes[angled].Frame.Rotation.Rotate(Vector3d.UnitZ).Y)>.99);
        foreach(var id in f.Session.Snapshot.Datums.Planes.Keys.Concat(f.Session.Snapshot.Datums.Axes.Keys).Concat(f.Session.Snapshot.Datums.Points.Keys).ToArray())
        {
            var page=await f.Inspect(new{area="datums",id});var copied=page.GetProperty("items")[0].GetProperty("edit");
            var before=f.Session.Snapshot;var result=await f.CallJson(copied.GetRawText());
            Assert.False(result.GetProperty("changed").GetBoolean());Assert.Empty(result.GetProperty("createdIds").EnumerateArray());Assert.Same(before,f.Session.Snapshot);
        }
        f.Session.Snapshot.Validate();
        // Removing a named source is rejected by the same complete production dependency graph.
        var state=f.Session.Snapshot;Assert.False(Success(await f.CallResult("delete",new{id=xy,revision=state.Datums.Planes[xy].Revision})));Assert.Same(state,f.Session.Snapshot);
        await f.Call("detach",new{id=two,revision=state.Datums.Axes[two].Revision});
        Assert.Equal(DatumAxisKind.Independent,f.Session.Snapshot.Datums.Axes[two].Kind);
        Assert.True(DatumPlanes.SameFrame(state.Datums.Axes[two].Frame,f.Session.Snapshot.Datums.Axes[two].Frame));
        await f.Call("detach",new{id=middle,revision=f.Session.Snapshot.Datums.Points[middle].Revision});
        Assert.Equal(state.Datums.Points[middle].Position,f.Session.Snapshot.Datums.Points[middle].Position);
        await f.Call("detach",new{id=top,revision=f.Session.Snapshot.Datums.Planes[top].Revision});
        Assert.True(DatumPlanes.SameFrame(state.Datums.Planes[top].Frame,f.Session.Snapshot.Datums.Planes[top].Frame));
    }

    [Theory][InlineData("Plane")][InlineData("Cylinder")][InlineData("Circle")][InlineData("Cone")]
    public async Task OriginalSourceInspectionCanBeCopiedButNotItsCachedAnalyticGeometry(string kind)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();
        if(kind=="Cone")
        {
            using var shape=OcctSharp.ShapeFactory.CreateCone(3,1,5);using var stored=OcctGeometryBridge.StoreShape(shape,f.Assets);
            await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Analytic",f.Part));
        }
        else if(kind is "Cylinder" or "Circle")await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,5,RigidTransform3d.Identity),"Analytic",f.Part));
        var source=f.Session.Snapshot.Features.Values.Last(x=>x.Name==(kind=="Plane"?"Base":"Analytic"));
        var all=new List<JsonElement>();var offset=0;int total;
        do
        {
            var page=await f.Call("inspect_sources",new{featureId=source.Id.ToString(),revision=source.Result.Revision.ToString(),offset,limit=3});
            Assert.Equal(source.PartId.ToString(),page.GetProperty("partId").GetString());total=page.GetProperty("total").GetInt32();
            all.AddRange(page.GetProperty("items").EnumerateArray().Select(x=>x.Clone()));offset+=3;
        }while(offset<total);
        Assert.Equal(total,all.Count);Assert.Equal(Enumerable.Range(0,total),all.Select(x=>x.GetProperty("index").GetInt32()));
        var slot=all.First(x=>x.TryGetProperty("geometry",out var g)&&g.GetProperty("kind").GetString()==kind&&
            (kind!="Plane"||g.GetProperty("direction")[2].GetDouble()>.99));
        var selection=slot.GetProperty("selection");Assert.Equal(slot.GetProperty("index").GetInt32(),selection.GetProperty("index").GetInt32());
        var type=kind=="Plane"?"plane":kind=="Circle"?"point":"axis";
        object author=kind=="Plane"?new{kind="Offset",primary=new{kind="ExactFace",selection,frame=Frame(0,0,0)}}:
            kind=="Circle"?new{kind="CircleCenter",selection}:(object)new{kind="Analytic",selection};
        var id=await f.CreateDatum(type,"Native "+kind,author);
        var evidence=kind=="Plane"?f.Session.Snapshot.Datums.Planes[id].Primary.Exact:kind=="Circle"?f.Session.Snapshot.Datums.Points[id].Exact:f.Session.Snapshot.Datums.Axes[id].Exact;
        Assert.Equal(source.Result,evidence!.Source);Assert.Equal(source.Result.AssetId,evidence.Selection.Asset);
        Assert.Equal(kind,evidence.Kind.ToString());
        var inspected=await f.Inspect(new{area="datums",id});var input=JsonNode.Parse(inspected.GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();
        var unchanged=f.Session.Snapshot;Assert.False((await f.CallJson(input.ToJsonString())).GetProperty("changed").GetBoolean());Assert.Same(unchanged,f.Session.Snapshot);
        // Client values cannot replace copied native geometry or bypass a fingerprint/revision.
        var inputAuthor=input["author"]!.AsObject();inputAuthor["radius"]=99;
        Assert.False(Success(await f.Tools.ExecuteAsync(new("3","cad_datum",input.ToJsonString()),default)));Assert.Same(unchanged,f.Session.Snapshot);inputAuthor.Remove("radius");
        var selected=type=="plane"?inputAuthor["primary"]!["selection"]!.AsObject():inputAuthor["selection"]!.AsObject();
        selected["fingerprint"]=new string('0',64);
        Assert.False(Success(await f.Tools.ExecuteAsync(new("3","cad_datum",input.ToJsonString()),default)));Assert.Same(unchanged,f.Session.Snapshot);
        var storage=new CadDocumentStorage(datumResolver:new OcctGeometryKernel());
        var path=files.PathFor("native-datum.cadoryx");await f.Session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,f.Assets);loaded.Snapshot.Validate();
        Assert.Equal(evidence,DatumConstructions.ExactSources(loaded.Snapshot.Datums).Single());
    }

    [Fact]
    public async Task DatumToolsDriveSketchNativeSuccessorDrawingAndWorldWorkplaneAsOneTransaction()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();
        var plane=await f.CreateDatum("plane","Top + clearance",new{kind="Offset",primary=new{kind="BoxFace",feature=f.Base.Id.ToString(),boundary="ZMax"},value=2});
        var draft=new SketchDraft(CadSketch.Create(f.Part,"Bracket",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var sketch=f.Session.Snapshot.Sketches[draft.Value.Id];
        await f.Call("bind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=plane,datumRevision=f.Revision(plane)});
        sketch=f.Session.Snapshot.Sketches[sketch.Id];Assert.Equal(6,sketch.Plane.Translation.Z,8);
        var profile=SketchProfileReference.Create(sketch,sketch.Lines.Select(x=>x.Id));
        await f.Session.ExecuteAsync(new AddBodyCommand(profile.Resolve(f.Session.Snapshot,f.Part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Bracket",f.Part,sketchSource:profile));
        var extrusion=f.Session.Snapshot.Features.Values.Single(x=>x.Id!=f.Base.Id);
        var sheet=TechnicalDrawingSheet.A4Landscape("Datum drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(100,100),1));
        var occurrence=f.Session.Snapshot.EnumerateOccurrences().Single(x=>x.DefinitionId==f.Part);
        var placement=new RigidTransform3d(new(10,20,30),Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2));
        await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(occurrence.Path,placement));
        occurrence=f.Session.Snapshot.EnumerateOccurrences().Single(x=>x.Path.Equals(occurrence.Path));
        await f.Call("bind_workplane",new{datumId=plane,datumRevision=f.Revision(plane),path=occurrence.Path.ToString()});
        var expectedWorld=occurrence.WorldTransform*f.Session.Snapshot.Datums.Planes[plane].Frame;
        Assert.True(DatumPlanes.SameFrame(expectedWorld,new(f.Session.Snapshot.Settings.WorkPlane.Origin,f.Session.Snapshot.Settings.WorkPlane.Rotation)));
        var before=f.Session.Snapshot;var count=f.Assets.Count;
        var input=JsonNode.Parse((await f.Inspect(new{area="datums",id=plane})).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();input["author"]!["value"]=5;
        var modified=await f.CallJson(input.ToJsonString());Assert.True(modified.GetProperty("changed").GetBoolean());
        var after=f.Session.Snapshot;Assert.Equal(9,after.Sketches[sketch.Id].Plane.Translation.Z,8);
        Assert.Equal(9,Assert.IsType<ExtrudeRecipe>(after.Features[extrusion.Id].Recipe).Placement.Translation.Z,8);
        Assert.Equal(after.Features[extrusion.Id].Result.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single(x=>x.Feature==extrusion.Id).Revision);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var p=await f.OtherTool("cad_parameter",new{action="set_expression",name="clearance",dimension="Length",expression="8 mm"});
        await f.OtherTool("cad_parameter",new{action="bind",parameterId=p.GetProperty("id").GetGuid(),target=new{kind="Datum",owner=plane,property="Offset"}});
        Assert.Equal(12,f.Session.Snapshot.Sketches[sketch.Id].Plane.Translation.Z,8);
        var driven=f.Session.Snapshot;
        var forbidden=JsonNode.Parse((await f.Inspect(new{area="datums",id=plane})).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();forbidden["author"]!["value"]=1;
        Assert.False(Success(await f.Tools.ExecuteAsync(new("1","cad_datum",forbidden.ToJsonString()),default)));Assert.Same(driven,f.Session.Snapshot);
        Assert.False(Success(await f.CallResult("delete",new{id=plane,revision=f.Revision(plane)})));
        var associations=await f.Inspect(new{area="datum_associations",id=plane,limit=1});Assert.Equal(2,associations.GetProperty("total").GetInt32());Assert.True(associations.GetProperty("hasMore").GetBoolean());
        Assert.Equal("part_local",associations.GetProperty("items")[0].GetProperty("coordinateSpace").GetString());
        var world=await f.Inspect(new{area="datum_associations",id=plane,offset=1,limit=1});Assert.Equal("world",world.GetProperty("items")[0].GetProperty("coordinateSpace").GetString());
        Assert.Equal(occurrence.Path.ToString(),world.GetProperty("items")[0].GetProperty("path").GetString());
        var path=files.PathFor("datum-tools.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(driven.Datums.WorkPlane,loaded.Snapshot.Datums.WorkPlane);}
        sketch=f.Session.Snapshot.Sketches[sketch.Id];var workplane=f.Session.Snapshot.Settings.WorkPlane;
        await f.Call("unbind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=plane,datumRevision=f.Revision(plane)});
        await f.Call("unbind_workplane",new{datumId=plane,datumRevision=f.Revision(plane),path=occurrence.Path.ToString()});
        Assert.Equal(sketch.Plane,f.Session.Snapshot.Sketches[sketch.Id].Plane);Assert.Equal(workplane,f.Session.Snapshot.Settings.WorkPlane);
        Assert.Empty(f.Session.Snapshot.Datums.SketchBindings);Assert.Null(f.Session.Snapshot.Datums.WorkPlane);Assert.True(f.Assets.Count>=count);
        f.Session.Snapshot.Validate();
    }

    [Theory][InlineData("stale-state")][InlineData("stale-revision")][InlineData("unknown")][InlineData("duplicate")]
    [InlineData("empty-id")][InlineData("numeric-enum")][InlineData("hidden-field")][InlineData("bad-vector")]
    [InlineData("bad-number")][InlineData("missing-reference")][InlineData("type-change")][InlineData("new-revision")]
    [InlineData("owner-change")][InlineData("cycle")][InlineData("rollback")][InlineData("readonly")]
    public async Task InvalidOrStaleDatumRequestsKeepTheCompleteAuthorState(string mutation)
    {
        await using var f=await Fixture.Create();var id=await f.CreateDatum("plane","Plane",new{kind="Offset",primary=new{kind="Basis",basis="XY"}});
        var args=JsonNode.Parse((await f.Inspect(new{area="datums",id})).GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();
        if(mutation=="stale-state")args["stateId"]=Guid.NewGuid().ToString();
        if(mutation=="stale-revision")args["revision"]=Guid.NewGuid().ToString();
        if(mutation=="unknown")args["force"]=true;
        if(mutation=="empty-id")args["id"]=Guid.Empty.ToString();
        if(mutation=="numeric-enum")args["author"]!["kind"]=0;
        if(mutation=="hidden-field")args["author"]!["secondary"]=new JsonObject{["kind"]="Basis",["basis"]="YZ"};
        if(mutation=="bad-vector")args["author"]!["primary"]=JsonSerializer.SerializeToNode(new{kind="Independent",frame=new{origin=new[]{1d,2d},normal=new[]{0d,0d,1d},u=new[]{1d,0d,0d}}});
        if(mutation=="bad-number")args["author"]!["value"]="4";
        if(mutation=="missing-reference")args["author"]!["primary"]=JsonSerializer.SerializeToNode(new{kind="Datum",datum=Guid.NewGuid()});
        if(mutation=="type-change"){args["action"]="set_axis";args["author"]=JsonSerializer.SerializeToNode(new{kind="Basis"});}
        if(mutation=="new-revision")args.Remove("id");
        if(mutation=="owner-change")
        {var other=DefinitionId.New();await f.Session.ExecuteAsync(ResourceCommands.AddPart(other,"Other"));args["partId"]=other.ToString();args["stateId"]=f.Session.Snapshot.StateId.ToString();}
        if(mutation=="cycle")args["author"]!["primary"]=JsonSerializer.SerializeToNode(new{kind="Datum",datum=id});
        if(mutation=="rollback")await f.Session.RollbackFeaturesAsync(f.Part,0);
        if(mutation=="readonly")
        {
            using var payload=f.Assets.Stage(new byte[]{1,2,3});
            await f.Session.ExecuteAsync(new EditDocumentCommand("Preserve future data",doc=>doc with{Extensions=[new("vendor.future",1,payload.Id,"vendor")]}));
            args["stateId"]=f.Session.Snapshot.StateId.ToString();Assert.True(f.Document.IsReadOnly);
        }
        var json=args.ToJsonString();if(mutation=="duplicate")json=json[..^1]+",\"name\":\"Duplicate\"}";
        var before=f.Session.Snapshot;var generation=f.Session.Generation;var count=f.Assets.Count;
        Assert.False(Success(await f.Tools.ExecuteAsync(new("2","cad_datum",json),default)));Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);Assert.Equal(count,f.Assets.Count);
    }

    [Fact]
    public async Task TerminalPreservesRawJsonAndAssociationRevisionChecksAndUndo()
    {
        await using var f=await Fixture.Create();var service=new CadCommandLineService();
        async Task<CadCommandResult> Run(string command)=>await service.ExecuteAsync(command,new CadCommandContext(f.Document));
        var created=await Run("DATUM "+JsonSerializer.Serialize(new{action="set_point",partId=f.Part.ToString(),name="Point with spaces",author=new{kind="Independent",value=new[]{1d,2d,3d}}}));
        Assert.True(created.Success,created.Message);using var parsed=JsonDocument.Parse(created.Message);var id=parsed.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(new Vector3d(1,2,3),f.Session.Snapshot.Datums.Points[id].Position);
        var before=f.Session.Snapshot;Assert.True((await Run($"DATUM DELETE {id} {f.Revision(id)}")).Success);Assert.Empty(f.Session.Snapshot.Datums.Points);
        Assert.True((await Run("UNDO")).Success);Assert.Same(before,f.Session.Snapshot);
        Assert.True((await Run($"DATUM SOURCES {f.Base.Id} {f.Base.Result.Revision} 0 2")).Success);
        foreach(var command in new[]{"DATUM DELETE by-name",$"DATUM DELETE {id} {Guid.NewGuid()}","DATUM {}","DATUM {\"action\":\"detach\"} extra","DATUM SOURCES invalid"})
            Assert.False((await Run(command)).Success,command);
        Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("cancel")][InlineData("state")][InlineData("close")]
    public async Task LateExactDatumResolutionCannotCommitAfterInvalidation(string boundary)
    {
        var kernel=new DelayedKernel();await using var f=await Fixture.Create(kernel);
        var sources=await f.Call("inspect_sources",new{featureId=f.Base.Id.ToString(),revision=f.Base.Result.Revision.ToString()});
        var selection=sources.GetProperty("items").EnumerateArray().First(x=>x.TryGetProperty("geometry",out var g)&&g.GetProperty("kind").GetString()=="Plane").GetProperty("selection");
        kernel.DelayResolve=true;using var cancel=new CancellationTokenSource();var before=f.Session.Snapshot;var count=f.Assets.Count;
        var pending=f.CallResult("set_plane",new{partId=f.Part.ToString(),name="Late plane",author=new{kind="Offset",primary=new{kind="ExactFace",selection,frame=Frame(0,0,0)}}},cancel.Token);
        await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));Task? closing=null;
        if(boundary=="cancel")cancel.Cancel();
        if(boundary=="state"){await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));before=f.Session.Snapshot;}
        if(boundary=="close"){f.Document.Detach();closing=f.Session.DisposeAsync().AsTask();}
        kernel.Release.TrySetResult();
        if(boundary=="state")Assert.False(Success(await pending));else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Same(before,f.Session.Snapshot);Assert.Empty(f.Session.Snapshot.Datums.Planes);
        if(closing is not null){await closing;Assert.Equal(0,f.Assets.Count);}else Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData("cancel")][InlineData("state")][InlineData("close")]
    public async Task LateNativeDatumRecomputeReleasesNewAssetsAndKeepsTheOldSuccessors(string boundary)
    {
        var kernel=new DelayedKernel();await using var f=await Fixture.Create(kernel);
        var id=await f.CreateDatum("plane","Driven plane",new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=2});
        var draft=new SketchDraft(CadSketch.Create(f.Part,"Profile",f.Session.Snapshot.Datums.Planes[id].Frame));draft.AddRectangle(new(0,0),new(4,3));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver(),datum:id));
        var sketch=f.Session.Snapshot.Sketches[draft.Value.Id];var profile=SketchProfileReference.Create(sketch,sketch.Lines.Select(x=>x.Id));
        await f.Session.ExecuteAsync(new AddBodyCommand(profile.Resolve(f.Session.Snapshot,f.Part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Extruded",f.Part,sketchSource:profile));
        var before=f.Session.Snapshot;var count=f.Assets.Count;var generation=f.Session.Generation;using var cancel=new CancellationTokenSource();kernel.DelayEvaluate=true;
        var pending=f.CallResult("set_plane",new{id,revision=f.Revision(id),partId=f.Part.ToString(),name="Driven plane",author=new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=6}},cancel.Token);
        await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(count+1,f.Assets.Count);Task? closing=null;
        if(boundary=="cancel")cancel.Cancel();
        if(boundary=="state"){await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));before=f.Session.Snapshot;generation=f.Session.Generation;}
        if(boundary=="close"){f.Document.Detach();closing=f.Session.DisposeAsync().AsTask();generation=f.Session.Generation;}
        kernel.Release.TrySetResult();
        if(boundary=="state")Assert.False(Success(await pending));else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);
        if(closing is not null){await closing;Assert.Equal(0,f.Assets.Count);}else Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData("cancel")][InlineData("state")][InlineData("close")]
    public async Task LateReadOnlySourcePagesHoldTheirOriginalAssetsAndRejectInvalidatedIdentity(string boundary)
    {
        var kernel=new DelayedKernel();await using var f=await Fixture.Create(kernel);using var cancel=new CancellationTokenSource();
        var count=f.Assets.Count;kernel.DelayInspection=true;
        var pending=f.CallResult("inspect_sources",new{featureId=f.Base.Id.ToString(),revision=f.Base.Result.Revision.ToString()},cancel.Token);
        await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if(boundary=="cancel")cancel.Cancel();
        if(boundary=="state")await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with{SpacingMm=17}));
        if(boundary=="close"){f.Document.Detach();await f.Session.DisposeAsync();Assert.True(f.Assets.Count>0);}
        var before=f.Session.Snapshot;kernel.Release.TrySetResult();
        if(boundary=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);else Assert.False(Success(await pending));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(boundary=="close"?0:count,f.Assets.Count);
    }

    [Fact]
    public async Task CurrentAssociationGuardsRefuseImplicitReplacementAndWrongSketchRevision()
    {
        await using var f=await Fixture.Create();var first=await f.CreateDatum("plane","First",new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=2});
        var second=await f.CreateDatum("plane","Second",new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=5});
        var draft=new SketchDraft(CadSketch.Create(f.Part,"Sketch",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
        var sketch=f.Session.Snapshot.Sketches[draft.Value.Id];
        await f.Call("bind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=first,datumRevision=f.Revision(first)});
        sketch=f.Session.Snapshot.Sketches[sketch.Id];var state=f.Session.Snapshot;
        Assert.False((await f.Call("bind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=first,datumRevision=f.Revision(first)})).GetProperty("changed").GetBoolean());Assert.Same(state,f.Session.Snapshot);
        Assert.False(Success(await f.CallResult("bind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=second,datumRevision=f.Revision(second)})));
        Assert.False(Success(await f.CallResult("unbind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=Guid.NewGuid(),datumId=first,datumRevision=f.Revision(first)})));
        Assert.False(Success(await f.CallResult("unbind_sketch",new{sketchId=sketch.Id.ToString(),sketchRevision=sketch.Revision,datumId=second,datumRevision=f.Revision(second)})));
        var path=state.EnumerateOccurrences().Single(x=>x.DefinitionId==f.Part).Path.ToString();
        await f.Call("bind_workplane",new{datumId=first,datumRevision=f.Revision(first),path});state=f.Session.Snapshot;
        Assert.False((await f.Call("bind_workplane",new{datumId=first,datumRevision=f.Revision(first),path})).GetProperty("changed").GetBoolean());Assert.Same(state,f.Session.Snapshot);
        Assert.False(Success(await f.CallResult("bind_workplane",new{datumId=second,datumRevision=f.Revision(second),path})));
        Assert.False(Success(await f.CallResult("unbind_workplane",new{datumId=first,datumRevision=f.Revision(first),path=path+"/invalid"})));Assert.Same(state,f.Session.Snapshot);
    }

    [Fact]
    public async Task CancellationAfterCommittedDatumEditReturnsItsActualCreatedIdentity()
    {
        await using var f=await Fixture.Create();using var cancel=new CancellationTokenSource();f.Session.CommandCommitted+=(_,_)=>cancel.Cancel();
        var json=await f.CallResult("set_point",new{partId=f.Part.ToString(),name="Committed point",author=new{kind="Independent",value=new[]{1d,2d,3d}}},cancel.Token);
        Assert.True(Success(json),json);using var parsed=JsonDocument.Parse(json);var result=parsed.RootElement;
        Assert.True(cancel.IsCancellationRequested);Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal(f.Session.Snapshot.Datums.Points.Keys.Single(),result.GetProperty("createdIds")[0].GetGuid());
        Assert.Equal(f.Session.Snapshot.StateId.ToString(),result.GetProperty("stateId").GetString());
    }

    [Fact]
    public async Task FailedNativeGateSetupStillReclaimsHeldDatumAssets()
    {
        var kernel=new DelayedKernel();var f=await Fixture.Create(kernel);kernel.DelayEvaluate=true;
        var before=f.Session.Snapshot;
        var pending=f.Session.ExecuteAsync(new RecomputeCommand(f.Base.Id,((BoxRecipe)f.Base.Recipe) with{X=5}));
        try
        {
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2,f.Assets.Count);
            await f.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(0,f.Assets.Count);
        }
        finally{kernel.Release.TrySetResult();await f.DisposeAsync();}
    }

    private static object Frame(double x,double y,double z)=>new{origin=new[]{x,y,z},normal=new[]{0d,0d,1d},u=new[]{1d,0d,0d}};
    private static bool Success(string json){using var parsed=JsonDocument.Parse(json);return parsed.RootElement.GetProperty("success").GetBoolean();}
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly IGeometryKernel kernel;
        internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal CadAgentToolset Tools{get;}
        internal FeatureDefinition Base{get;private set;}=null!;internal DefinitionId Part=>Base.PartId;
        private Fixture(IGeometryKernel kernel){this.kernel=kernel;Session=new(DocumentSnapshot.Create("Datum tools"),Assets,kernel,new InlineSessionDispatcher());Document=new(Session,kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);}
        internal static async Task<Fixture> Create(IGeometryKernel? kernel=null){var f=new Fixture(kernel??new OcctGeometryKernel());await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Base"));f.Base=f.Session.Snapshot.Features.Values.Single();return f;}
        internal Guid Revision(Guid id)=>DatumConstructions.Revision(Session.Snapshot.Datums,id);
        private JsonObject Arguments(string action,object? fields=null){var args=fields is null?new JsonObject():JsonSerializer.SerializeToNode(fields)!.AsObject();args["action"]=action;args["documentId"]=Session.Snapshot.Id.ToString();args["stateId"]=Session.Snapshot.StateId.ToString();return args;}
        internal Task<string> CallResult(string action,object? fields=null,CancellationToken token=default)=>Tools.ExecuteAsync(new("1","cad_datum",Arguments(action,fields).ToJsonString()),token);
        internal async Task<JsonElement> Call(string action,object? fields=null){var json=await CallResult(action,fields);Assert.True(Success(json),json);using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
        internal async Task<Guid> CreateDatum(string type,string name,object author)=> (await Call("set_"+type,new{partId=Part.ToString(),name,author})).GetProperty("id").GetGuid();
        internal async Task<JsonElement> CallJson(string json){var result=await Tools.ExecuteAsync(new("1","cad_datum",json),default);Assert.True(Success(result),result);using var parsed=JsonDocument.Parse(result);return parsed.RootElement.Clone();}
        internal async Task<JsonElement> Inspect(object fields){var json=await Tools.ExecuteAsync(new("2","cad_inspect",JsonSerializer.Serialize(fields)),default);Assert.True(Success(json),json);using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
        internal async Task<JsonElement> OtherTool(string tool,object fields){var args=JsonSerializer.SerializeToNode(fields)!.AsObject();args["documentId"]=Session.Snapshot.Id.ToString();args["stateId"]=Session.Snapshot.StateId.ToString();var json=await Tools.ExecuteAsync(new("3",tool,args.ToJsonString()),default);Assert.True(Success(json),json);using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
        public async ValueTask DisposeAsync()
        {
            Document.Detach();var closing=Session.DisposeAsync();
            // Cleanup also runs when a readiness or asset assertion failed before Release.
            if(kernel is DelayedKernel delayed)delayed.Release.TrySetResult();
            await closing;Assert.Equal(0,Assets.Count);
        }
    }
    private sealed class DelayedKernel:IGeometryKernel,IDatumGeometryResolver
    {
        private readonly OcctGeometryKernel native=new();internal bool DelayResolve{get;set;}internal bool DelayEvaluate{get;set;}internal bool DelayInspection{get;set;}
        internal TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {var result=await native.EvaluateAsync(recipe,assets,CancellationToken.None);if(DelayEvaluate){DelayEvaluate=false;Entered.TrySetResult();await Release.Task;}return result;}
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public Task ConfirmDatumGeometryAsync(ExactDatumGeometry e,IAssetStore assets,CancellationToken token=default)=>native.ConfirmDatumGeometryAsync(e,assets,token);
        public async Task<DatumSourcePage> InspectDatumSourcesAsync(DocumentSnapshot doc,FeatureId feature,GeometryRevisionId revision,IAssetStore assets,int offset,int limit,CancellationToken token=default)
        {var result=await native.InspectDatumSourcesAsync(doc,feature,revision,assets,offset,limit,CancellationToken.None);if(DelayInspection){DelayInspection=false;Entered.TrySetResult();await Release.Task;}return result;}
        public async Task<ExactDatumGeometry> ResolveDatumGeometryAsync(DocumentSnapshot doc,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default)
        {var result=await native.ResolveDatumGeometryAsync(doc,selection,assets,CancellationToken.None);if(DelayResolve){DelayResolve=false;Entered.TrySetResult();await Release.Task;}return result;}
    }
}
