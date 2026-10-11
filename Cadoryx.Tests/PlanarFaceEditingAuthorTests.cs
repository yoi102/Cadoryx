using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels.Toolboxes;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task StructuredFaceAuthorCreatesAndInspectsReusableNoOpEdit(bool removal)
    {
        await using var f=await Fixture.Create(bores:removal?2:0);var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);var before=f.Session.Snapshot;
        object author=removal?new{type="defeature",input=CadModelJson.Input(f.Source),faces=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Select(CadDatumJson.Selection).ToArray()}:
            new{type="planar_push_pull",input=CadModelJson.Input(f.Source),face=CadDatumJson.Selection(f.Top()),distance=-2};
        string result=await tools.ExecuteAsync(new("create","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),partId=f.Source.PartId.ToString(),name="Tool face",authorJson=JsonSerializer.Serialize(author)})),default);
        using(var parsed=JsonDocument.Parse(result))Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);var after=f.Session.Snapshot;var feature=f.Edited;
        Assert.Equal(removal?1800:1560,feature.Result.VolumeMm3,4);if(!removal)Assert.Equal(Vector3d.UnitZ,((PlanarPushPullRecipe)feature.Recipe).OutwardNormal);
        result=await tools.ExecuteAsync(new("inspect","cad_inspect",JsonSerializer.Serialize(new{area="recipes",id=feature.Id.ToString()})),default);string edit;
        using(var parsed=JsonDocument.Parse(result)){var item=parsed.RootElement.GetProperty("items")[0];Assert.True(item.GetProperty("available").GetBoolean());edit=item.GetProperty("edit").GetRawText();}
        int count=f.Assets.Count;var history=f.Session.HistoryUsage;result=await tools.ExecuteAsync(new("noop","cad_model",edit),default);
        using(var parsed=JsonDocument.Parse(result)){Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.False(parsed.RootElement.GetProperty("changed").GetBoolean());}
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
    }
    [Theory][InlineData("normal")][InlineData("curved")][InlineData("foreign")][InlineData("companion")]
    public async Task StructuredFaceAuthorRejectsUnreviewedDirectionSourceAndHiddenCompanions(string mode)
    {
        await using var f=await Fixture.Create(bores:1);var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);var before=f.Session.Snapshot;int count=f.Assets.Count;
        var face=mode=="curved"?f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Single():f.Top();
        if(mode=="foreign")face=face with{Fingerprint=new string('0',64)};
        var author=new Dictionary<string,object>{["type"]="planar_push_pull",["input"]=CadModelJson.Input(f.Source),["face"]=CadDatumJson.Selection(face),["distance"]=2};
        if(mode=="normal")author["normal"]=new[]{1d,0,0};if(mode=="companion")author["faces"]=new[]{CadDatumJson.Selection(face)};
        string result=await tools.ExecuteAsync(new("bad","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),partId=f.Source.PartId.ToString(),name="Bad face",authorJson=JsonSerializer.Serialize(author)})),default);
        using(var parsed=JsonDocument.Parse(result))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task StructuredReselectionRequiresFreshExplicitFaceAndCurrentDocumentIdentity()
    {
        await using var f=await Fixture.Create();await f.Apply(await f.Push(2));var feature=f.Edited;var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));var before=f.Session.Snapshot;
        string author=JsonSerializer.Serialize(new{type="planar_push_pull",input=CadModelJson.Input(f.CurrentSource),face=CadDatumJson.Selection(f.Top()),distance=2});
        Task<string> Run(DocumentSnapshot doc,string action)=>tools.ExecuteAsync(new("reselect","cad_model",JsonSerializer.Serialize(new{action,documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),id=feature.Id.ToString(),revision=feature.Result.Revision.ToString(),authorJson=author})),default);
        string result=await Run(before,"edit");using(var parsed=JsonDocument.Parse(result))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Same(before,f.Session.Snapshot);
        result=await Run(before,"reselect");using(var parsed=JsonDocument.Parse(result))Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);var after=f.Session.Snapshot;
        Assert.False(after.Features[feature.Id].IsStale);Assert.Equal(2244,after.Features[feature.Id].Result.VolumeMm3,4);
        result=await Run(before,"reselect");using(var parsed=JsonDocument.Parse(result))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData("planar_push_pull")][InlineData("defeature")]
    public void StructuredFaceContractHasCompletePagedExactInputDefinitions(string type)
    {
        var definitions=new HashSet<string>();int offset=0;
        do
        {
            var json=CadModelContract.Read(type,offset,50);Assert.InRange(json.Length,1,5500);using var parsed=JsonDocument.Parse(json);var root=parsed.RootElement;
            foreach(var p in root.GetProperty("schema").GetProperty("$defs").EnumerateObject())definitions.Add(p.Name);
            if(!root.GetProperty("hasMore").GetBoolean())break;int next=root.GetProperty("nextOffset").GetInt32();Assert.True(next>offset);offset=next;
        }while(true);
        Assert.Contains("input",definitions);Assert.Contains("selection",definitions);Assert.Equal(16,CadModelContract.Types.Length);
    }
    [Fact]public async Task ForgedHistoricalNormalCannotReplacePreviouslyValidFile()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Apply(await f.Push(2));var feature=f.Edited;var path=files.PathFor("preserve.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);
        await f.Session.SaveAsync(storage,path);var bytes=File.ReadAllBytes(path);int count=f.Assets.Count;
        var forged=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{Recipe=((PlanarPushPullRecipe)feature.Recipe) with{OutwardNormal=new(1,0,0)}})};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(forged,f.Assets,path));Assert.Equal(bytes,File.ReadAllBytes(path));Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData("normal")][InlineData("distance")][InlineData("companion")]
    public async Task RemovalStorageForbidsPushPullAndModelingCompanions(string mode)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(bores:1);await f.Apply(new DefeatureRecipe(f.Source.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)));
        var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);var path=files.PathFor("remove.cadoryx");await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
            return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(feature=>feature.Recipe.FaceEdit is {} edit?
                feature with{Recipe=mode switch{"normal"=>feature.Recipe with{FaceEdit=edit with{Normal=[0,0,1]}},"distance"=>feature.Recipe with{FaceEdit=edit with{Distance=2}},_=>feature.Recipe with{Numbers=[2]}}}:feature).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
}
