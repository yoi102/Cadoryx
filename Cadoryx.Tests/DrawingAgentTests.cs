using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class DrawingAgentTests
{
    [Fact]
    public async Task DiscoverableStrictContractsAndDefaultContextKeepAllTwelveTools()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var contract=Parse(await tools.ExecuteAsync(new("c","cad_drawing","{\"action\":\"contract\"}"),default));Success(contract);
        foreach(var type in contract["types"]!.AsArray())
        {
            var result=Parse(await tools.ExecuteAsync(new("c","cad_drawing",new JsonObject{["action"]="contract",["type"]=type!.GetValue<string>()}.ToJsonString()),default));Success(result);
            if(type!.GetValue<string>()!="template")Assert.False(result["$defs"]![result["authorSchema"]!["$ref"]!.GetValue<string>().Split('/')[^1]]!["additionalProperties"]!.GetValue<bool>());
        }
        foreach(var aggressive in new[]{false,true})
        {
            var context=AgentRequestContextBuilder.Build("CAD assistant",[Cadoryx.AI.Contracts.AiChatMessage.User("Create a drawing, dimensions and vector PDF")],tools.SelectTools("Create a drawing, dimensions and vector PDF",aggressive),8192,aggressive);
            Assert.True(context.Tools.Count==12,$"aggressive={aggressive}; selected={string.Join(',',context.Tools.Select(t=>t.Name))}; costs={string.Join(',',tools.ToolDefinitions.Select(t=>$"{t.Name}:{AgentRequestContextBuilder.EstimateToolTokens(t)}"))}");Assert.Contains(context.Tools,t=>t.Name=="cad_drawing");
            Assert.True(context.EstimatedPromptTokens<=8192-context.MaxOutputTokens-(aggressive?1280:640));
            var contact=context.Tools.Single(t=>t.Name=="cad_edit").Parameters;
            Assert.Contains(contact.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="set_sketch_contact");
            Assert.Contains(contact.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="set_sketch_curvature");
            Assert.Contains(contact.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="delete_sketch_curvature");
            Assert.Contains(contact.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="set_sketch_finite_contact");
            Assert.Contains(contact.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="delete_sketch_finite_contact");
            Assert.Equal("number",contact.GetProperty("properties").GetProperty("parameterA").GetProperty("type").GetString());
            Assert.Equal("number",contact.GetProperty("properties").GetProperty("parameterB").GetProperty("type").GetString());
            Assert.Equal(2,contact.GetProperty("properties").GetProperty("direction").GetProperty("enum").GetArrayLength());
            Assert.Equal(5,contact.GetProperty("properties").GetProperty("branch").GetProperty("enum").GetArrayLength());
            var longContext=AgentRequestContextBuilder.Build("CAD assistant",[Cadoryx.AI.Contracts.AiChatMessage.User(new string('x',20000)),Cadoryx.AI.Contracts.AiChatMessage.User("Create an engineering drawing")],tools.SelectTools("Create an engineering drawing",aggressive),8192,aggressive);
            Assert.Contains(longContext.Tools,t=>t.Name=="cad_drawing");
        }
    }
    [Theory][InlineData("Front")][InlineData("Top")][InlineData("Right")][InlineData("Isometric")][InlineData("Section")][InlineData("Detail")]
    public async Task CompleteViewAuthorsPreserveSourcesCopyNoOpUndoStorageAndNativeProjection(string kind)
    {
        await using var f=await Fixture.Create();var author=new JsonObject{["name"]=kind,["kind"]=kind,["centerMm"]=Point(80,60),["scale"]=2};
        if(kind=="Section"){author["sectionNormal"]=Vector(0,0,1);author["sectionOffsetMm"]=5;}
        if(kind=="Detail"){author["parentView"]=f.View.ToString();author["detailCenter"]=Point(5,5);author["detailRadius"]=3;}
        var result=await f.Create("view",author);var id=result["id"]!.GetValue<Guid>();var snapshot=f.Session.Snapshot;
        Assert.NotEmpty(snapshot.DrawingSheets[f.Sheet].Views.Single(v=>v.Id==id).Strokes);
        var edit=await f.Edit("view",id);var same=await f.Call(edit);Success(same);Assert.False(same["changed"]!.GetValue<bool>());Assert.Same(snapshot,f.Session.Snapshot);
        var next=Author(edit);next["name"]="Changed";next["centerMm"]=Point(95,75);next["scale"]=3;
        if(kind=="Section")next["sectionOffsetMm"]=6;if(kind=="Detail")next["detailRadius"]=2;
        edit["authorJson"]=next.ToJsonString();Success(await f.Call(edit));var changed=f.Session.Snapshot;var view=changed.DrawingSheets[f.Sheet].Views.Single(v=>v.Id==id);
        Assert.Equal("Changed",view.Name);Assert.Null(view.StaleReason);Assert.Equal(id,view.Id);
        await f.Session.UndoAsync();Assert.Same(snapshot,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(changed,f.Session.Snapshot);
        await f.Session.SaveAsync(f.Storage,f.PathFor("view.cadoryx"));using var loaded=await f.Storage.LoadAsync(f.PathFor("view.cadoryx"),f.Assets);
        var reopened=loaded.Snapshot.DrawingSheets[f.Sheet].Views.Single(v=>v.Id==id);
        Assert.Equal(CadModelJson.Write(view).GetRawText(),CadModelJson.Write(reopened).GetRawText());
    }
    [Theory][InlineData("Length")][InlineData("Angle")][InlineData("Radius")][InlineData("Diameter")]
    public async Task DimensionsUseActualAnalyticEvidenceAndCannotChangeTheirCachedValue(string kind)
    {
        await using var f=await Fixture.Create(true);var sources=await f.Sources();var planes=sources.Where(s=>s["geometry"]!.GetValue<string>()=="PlaneFace").ToArray();
        var first=kind is "Radius" or "Diameter"?sources.First(s=>s["geometry"]!.GetValue<string>()=="CylinderFace"):planes[0];
        var author=new JsonObject{["viewId"]=f.View.ToString(),["kind"]=kind,["first"]=first["selection"]!.DeepClone(),["textPositionMm"]=Point(80,40),["argb"]=0xff102030u,["upperTolerance"]=.1,["lowerTolerance"]=.2};
        if(kind is "Length" or "Angle")author["second"]=planes[1]["selection"]!.DeepClone();
        var result=await f.Create("dimension",author);var id=result["id"]!.GetValue<Guid>();var before=f.Session.Snapshot;var dimension=before.DrawingSheets[f.Sheet].Dimensions.Single();
        Assert.Null(dimension.StaleReason);Assert.True(dimension.Value>=0);if(kind=="Diameter")Assert.Equal(2,dimension.Value,6);if(kind=="Radius")Assert.Equal(1,dimension.Value,6);
        var edit=await f.Edit("dimension",id);Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        var changed=Author(edit);changed["value"]=999;edit["authorJson"]=changed.ToJsonString();Failure(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        changed.Remove("value");changed["upperTolerance"]=.4;edit["authorJson"]=changed.ToJsonString();Success(await f.Call(edit));Assert.Equal(.4,f.Session.Snapshot.DrawingSheets[f.Sheet].Dimensions.Single().UpperTolerance);
    }
    [Theory][InlineData("datum","Surface")][InlineData("datum","CylinderSizeAxis")]
    [InlineData("control_frame","Surface")][InlineData("control_frame","Axis")][InlineData("balloon","Surface")]
    public async Task ManufacturingAuthorsRebuildNativeProofsAndKeepOriginalSources(string type,string target)
    {
        await using var f=await Fixture.Create(true);var sources=await f.Sources();var source=sources.First(s=>s["geometry"]!.GetValue<string>()==(target is "Axis" or "CylinderSizeAxis"?"CylinderFace":"PlaneFace"));
        var author=new JsonObject{["viewId"]=f.View.ToString(),["source"]=source["selection"]!.DeepClone(),["positionMm"]=Point(110,50)};
        if(type=="datum"){author["name"]="A";author["featureType"]=target;}
        if(type=="control_frame"){author["characteristic"]=target=="Axis"?"Straightness":"Flatness";author["toleranceMm"]=.1;author["target"]=target;author["diameterZone"]=target=="Axis";}
        if(type=="balloon")author["itemNumber"]=1;
        var result=await f.Create(type,author);var id=result["id"]!.GetValue<Guid>();var before=f.Session.Snapshot;var edit=await f.Edit(type,id);
        Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        var next=Author(edit);next["positionMm"]=Point(120,55);edit["authorJson"]=next.ToJsonString();Success(await f.Call(edit));
        var page=await f.Inspect(type,id);Assert.Null(page["items"]![0]!["staleReason"]);
        next["source"]!["index"]=sources.First(s=>s["index"]!.GetValue<int>()!=source["index"]!.GetValue<int>())["index"]!.DeepClone();
        edit=f.Refresh(edit);edit["authorJson"]=next.ToJsonString();Failure(await f.Call(edit));
        if(type=="datum"&&target=="CylinderSizeAxis")Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().SizeEvidence);
        if(type=="balloon")Assert.Equal(1,f.Session.Snapshot.DrawingSheets[f.Sheet].Balloons.Single().Quantity);
    }
    [Theory][InlineData("datum")][InlineData("control_frame")]
    public async Task MedianPlaneAuthorRequiresIndependentOppositeFaceAndActualFiniteDomainProof(string type)
    {
        await using var f=await Fixture.Create();var sources=await f.Sources();JsonNode? first=null,second=null;
        foreach(var a in sources.Where(s=>s["geometry"]!.GetValue<string>()=="PlaneFace"))
        foreach(var b in sources.Where(s=>s["geometry"]!.GetValue<string>()=="PlaneFace"&&s["index"]!.GetValue<int>()!=a["index"]!.GetValue<int>()))
        {
            try{await f.Kernel.ResolveDrawingPlanarSizeAsync(f.Session.Snapshot,await f.Resolve(a["selection"]!),await f.Resolve(b["selection"]!),false,f.Assets);first=a["selection"];second=b["selection"];break;}catch(CadValidationException){}
        }
        Assert.NotNull(first);Assert.NotNull(second);var author=new JsonObject{["viewId"]=f.View.ToString(),["source"]=first!.DeepClone(),["opposite"]=second!.DeepClone(),["positionMm"]=Point(110,50)};
        if(type=="datum"){author["name"]="B";author["featureType"]="PlanarSizeMedianPlane";}
        else{author["characteristic"]="Flatness";author["toleranceMm"]=.1;author["target"]="MedianPlane";}
        var result=await f.Create(type,author);var id=result["id"]!.GetValue<Guid>();var before=f.Session.Snapshot;var edit=await f.Edit(type,id);Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        var copy=Author(edit);copy["opposite"]=copy["source"]!.DeepClone();edit["authorJson"]=copy.ToJsonString();edit["action"]="reselect";Failure(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
    }
    [Fact]
    public async Task SheetContentLibraryImportExportNativeSaveAndVectorPdfShareCapturedReceipts()
    {
        await using var f=await Fixture.Create();var field=await f.Create("field",new{caption="Process",value="CNC",positionMm=new{x=25,y=90},widthMm=70});
        var note=await f.Create("note",new{text="Deburr edges",positionMm=new{x=25,y=110},widthMm=70});
        Success(await f.Call(f.Request("set_zones",("sheetId",f.Sheet.ToString()),("authorJson","{\"zones\":{\"columns\":8,\"rows\":6}}"))));
        var sheetEdit=await f.Edit("sheet",f.Sheet);var sheetAuthor=Author(sheetEdit);sheetAuthor["title"]="Manufacturing";sheetAuthor["standard"]="GB";sheetEdit["authorJson"]=sheetAuthor.ToJsonString();Success(await f.Call(sheetEdit));
        var saved=await f.Call(f.Request("save_template",("sheetId",f.Sheet.ToString()),("name","Shop")));Success(saved);var template=saved["id"]!.GetValue<Guid>();
        var templatePath=f.PathFor("template.json");var exported=await f.File("template_export",templatePath,template);Success(exported);
        var duplicate=await f.Call(f.Request("import_template",("path",templatePath)));Failure(duplicate);
        var detached=DrawingTemplateFile.Read(templatePath);DrawingTemplateFile.Write(detached with{Name="Imported shop"},templatePath);
        var imported=await f.Call(f.Request("import_template",("path",templatePath)));Success(imported);Assert.NotEqual(template,imported["id"]!.GetValue<Guid>());
        var before=f.Session.Snapshot;var applied=await f.Call(f.Request("apply_template",("sheetId",f.Sheet.ToString()),("id",template.ToString())));Success(applied);
        Assert.DoesNotContain(before.DrawingSheets[f.Sheet].Template!.CustomFields[0].Id,f.Session.Snapshot.DrawingSheets[f.Sheet].Template!.CustomFields.Select(x=>x.Id));
        Assert.Equal(CadModelJson.Write(before.DrawingSheets[f.Sheet].Views).GetRawText(),CadModelJson.Write(f.Session.Snapshot.DrawingSheets[f.Sheet].Views).GetRawText());
        var pdfPath=f.PathFor("drawing.pdf");var snapshot=f.Session.Snapshot;var generation=f.Session.Generation;var pdf=await f.File("pdf",pdfPath);Success(pdf);
        Assert.Equal(snapshot.StateId.ToString(),pdf["receipt"]!["stateId"]!.GetValue<string>());Assert.Equal(new FileInfo(pdfPath).Length,pdf["receipt"]!["bytes"]!.GetValue<long>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(pdfPath))),pdf["receipt"]!["sha256"]!.GetValue<string>());Assert.Same(snapshot,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);
        Assert.StartsWith("%PDF",await File.ReadAllTextAsync(pdfPath));
        var nativePath=f.PathFor("drawing.cadoryx");Success(await f.File("save",nativePath));using var loaded=await f.Storage.LoadAsync(nativePath,f.Assets);loaded.Snapshot.Validate();Assert.Equal(2,loaded.Snapshot.DrawingTemplates.Count);
        var delete=f.Request("delete",("type","field"),("sheetId",f.Sheet.ToString()),("id",field["id"]!.GetValue<Guid>().ToString()));Failure(await f.Call(delete));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task HoleTablesRebuildEveryRowAndRequireExplicitCoordinateDetach(bool associated)
    {
        await using var f=await Fixture.Create(true);var shaft=(await f.Sources()).First(s=>s["geometry"]!.GetValue<string>()=="CylinderFace");
        var author=new JsonObject{["viewId"]=f.View.ToString(),["name"]="Locations",["coordinatePath"]=f.Path.ToString(),["frame"]=Frame(0,0,15),["positionMm"]=Point(25,90),
            ["rows"]=new JsonArray(new JsonObject{["number"]=1,["source"]=shaft["selection"]!.DeepClone()})};
        if(associated)
        {
            var plane=new CadDatumPlane(Guid.NewGuid(),f.Part,"Coordinates",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:15);
            await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(plane.Id,plane)}));plane=f.Session.Snapshot.Datums.Planes[plane.Id];
            author["frame"]=null;author["coordinates"]=new JsonObject{["binding"]=new JsonObject{["plane"]=plane.Id.ToString()},["revisions"]=new JsonArray(plane.Revision.ToString())};
        }
        var created=await f.Create("hole_table",author);var id=created["id"]!.GetValue<Guid>();var before=f.Session.Snapshot;var table=before.DrawingSheets[f.Sheet].HoleTables.Single();
        Assert.Equal(2,table.Rows[0].Evidence.DiameterMm,6);Assert.Equal(new Point2d(5,6),table.Rows[0].CoordinatesMm);
        var edit=await f.Edit("hole_table",id);Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        var copy=Author(edit);copy["positionMm"]=Point(30,95);edit["authorJson"]=copy.ToJsonString();Success(await f.Call(edit));
        if(associated){Success(await f.Call(f.Request("detach_coordinates",("sheetId",f.Sheet.ToString()),("id",id.ToString()))));Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables.Single().CoordinateBinding);}
        Success(await f.File("pdf",f.PathFor("holes.pdf")));Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables.Single().Rows[0].StaleReason);
    }
    [Theory][InlineData("state")][InlineData("document")][InlineData("owner")][InlineData("kind")][InlineData("source")][InlineData("unknown")]
    [InlineData("duplicate")][InlineData("numeric-enum")][InlineData("cache")][InlineData("null-name")][InlineData("scale")][InlineData("budget")]
    [InlineData("readonly")][InlineData("rollback")][InlineData("closing")]
    public async Task InvalidDrawingRequestsPreserveAuthorsHistoryAndAssets(string failure)
    {
        await using var f=await Fixture.Create();var edit=await f.Edit("view",f.View);var author=Author(edit);
        if(failure=="state")edit["stateId"]=Guid.NewGuid().ToString();if(failure=="document")edit["documentId"]=Guid.NewGuid().ToString();
        if(failure=="owner")edit["sheetId"]=Guid.NewGuid().ToString();if(failure=="kind")author["kind"]="Section";if(failure=="source")author["sourcePath"]=f.Path.ToString();
        if(failure=="unknown")author["force"]=true;if(failure=="numeric-enum")author["kind"]=0;if(failure=="cache")author["strokes"]=new JsonArray();
        if(failure=="null-name")author["name"]=null;if(failure=="scale")author["scale"]=0;
        edit["authorJson"]=failure=="duplicate"?"{\"name\":\"Top\",\"name\":\"Top\"}":failure=="budget"?new string('x',65537):author.ToJsonString();
        if(failure=="rollback")await f.Session.RollbackFeaturesAsync(f.Part,0);
        if(failure=="readonly")
        {using var asset=f.Assets.Stage(new byte[]{3,2,1});await f.Session.ExecuteAsync(new EditDocumentCommand("Extension",doc=>doc with{Extensions=[new("vendor.future",1,asset.Id,"vendor")]}));}
        if(failure=="closing")f.Document.IsClosingRequested=true;
        if(failure is "readonly" or "rollback")edit=f.Refresh(edit);
        var before=f.Session.Snapshot;var generation=f.Session.Generation;var assets=f.Assets.Count;Failure(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);Assert.Equal(assets,f.Assets.Count);
    }
    [Fact]
    public async Task TerminalRawAuthorsQuotesPaginationAndBadOwnersUseTheProductionContract()
    {
        await using var f=await Fixture.Create();var commands=new CadCommandLineService();var context=new CadCommandContext(f.Document);
        Assert.True((await commands.ExecuteAsync("DRAWING CONTRACT",context)).Success);Assert.True((await commands.ExecuteAsync("DRAWING CONTRACT view",context)).Success);
        var result=await commands.ExecuteAsync("DRAWING "+new JsonObject{["action"]="create",["type"]="note",["sheetId"]=f.Sheet.ToString(),["author"]=new JsonObject{["text"]="Quoted \"author\"",["positionMm"]=Point(25,90),["widthMm"]=70}}.ToJsonString(),context);
        Assert.True(result.Success,result.Message);Success(Parse(result.Message));Assert.Equal("Quoted \"author\"",f.Session.Snapshot.DrawingSheets[f.Sheet].Template!.Notes.Single().Text);
        var page=await f.Call(f.Request("inspect",("type","view"),("sheetId",f.Sheet.ToString()),("offset",1),("limit",1)));Success(page);Assert.Empty(page["items"]!.AsArray());Assert.False(page["hasMore"]!.GetValue<bool>());
        Failure(await f.Call(f.Request("delete",("type","dimension"),("sheetId",f.Sheet.ToString()),("id",f.View.ToString()))));
    }
    [Fact]
    public async Task StaleReferencesAndWrongPdfInputsCannotReplaceExistingFiles()
    {
        await using var f=await Fixture.Create();var output=f.PathFor("drawing.pdf");await File.WriteAllTextAsync(output,"OLD");
        var oldView=f.Session.Snapshot.DrawingSheets[f.Sheet].Views.Single();
        await f.Session.ExecuteAsync(new EditDocumentCommand("Missing source",doc=>doc with{DrawingSheets=doc.DrawingSheets.SetItem(f.Sheet,doc.DrawingSheets[f.Sheet] with{Views=[oldView with{Sources=[oldView.Sources[0] with{Body=BodyId.New()}]}]})}));
        var request=f.Request("pdf",("path",output),("overwrite",true));Failure(Parse(await f.Tools.ExecuteAsync(new("p","cad_file",request.ToJsonString()),default)));Assert.Equal("OLD",await File.ReadAllTextAsync(output));
        request["optionsJson"]="{}";Failure(Parse(await f.Tools.ExecuteAsync(new("p","cad_file",request.ToJsonString()),default)));Assert.Empty(Directory.GetFiles(f.Directory,".cadoryx-file-*"));
    }
    [Fact]
    public async Task SectionHatchAuthorEditsReprojectDetailsWithIndependentPaperSpacingAndNoOpCopies()
    {
        await using var f=await Fixture.Create();var section=await f.Create("view",new{name="Section",kind="Section",centerMm=new{x=120,y=60},scale=2,sectionNormal=new{x=0,y=0,z=1},sectionOffsetMm=5,
            hatching=new{@default=new{angleDegrees=30,spacingMm=2.5},alternateSources=false}});var id=section["id"]!.GetValue<Guid>();
        var detail=await f.Create("view",new{name="Detail",kind="Detail",centerMm=new{x=190,y=60},scale=3,parentView=id,detailCenter=new{x=5,y=5},detailRadius=3});
        var edit=await f.Edit("view",id);var before=f.Session.Snapshot;Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        var author=Author(edit);author["hatching"]!["default"]!["spacingMm"]=1;author["scale"]=4;edit["authorJson"]=author.ToJsonString();Success(await f.Call(edit));
        var views=f.Session.Snapshot.DrawingSheets[f.Sheet].Views;Assert.All(views,v=>Assert.Null(v.StaleReason));Assert.Equal(6,views.Single(v=>v.Id==detail["id"]!.GetValue<Guid>()).Scale);
        Assert.Contains(views.Single(v=>v.Id==id).Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);Assert.Contains(views.Single(v=>v.Id==detail["id"]!.GetValue<Guid>()).Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
    }
    [Fact]
    public async Task OrderedDatumSystemsKeepStableBindingsAndDependencyDeletionGuards()
    {
        await using var f=await Fixture.Create(true);var plane=(await f.Sources()).First(s=>s["geometry"]!.GetValue<string>()=="PlaneFace");var cylinder=(await f.Sources()).First(s=>s["geometry"]!.GetValue<string>()=="CylinderFace");
        var datum=await f.Create("datum",new JsonObject{["viewId"]=f.View.ToString(),["name"]="A",["source"]=plane["selection"]!.DeepClone(),["positionMm"]=Point(105,50)});var datumId=datum["id"]!.GetValue<Guid>();
        var frame=await f.Create("control_frame",new JsonObject{["viewId"]=f.View.ToString(),["source"]=cylinder["selection"]!.DeepClone(),["positionMm"]=Point(125,50),["characteristic"]="Position",["toleranceMm"]=.1,["target"]="Axis",["diameterZone"]=true,
            ["datumSystem"]=new JsonArray(new JsonObject{["references"]=new JsonArray(new JsonObject{["id"]=datumId.ToString()})})});
        Assert.Equal(datumId,f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().DatumBindings.Single().Id);
        var before=f.Session.Snapshot;Failure(await f.Call(f.Request("delete",("type","datum"),("sheetId",f.Sheet.ToString()),("id",datumId.ToString()))));Assert.Same(before,f.Session.Snapshot);
        var edit=await f.Edit("control_frame",frame["id"]!.GetValue<Guid>());Success(await f.Call(edit));Assert.Same(before,f.Session.Snapshot);
        Success(await f.Call(f.Request("delete",("type","control_frame"),("sheetId",f.Sheet.ToString()),("id",frame["id"]!.GetValue<Guid>().ToString()))));
        Success(await f.Call(f.Request("delete",("type","datum"),("sheetId",f.Sheet.ToString()),("id",datumId.ToString()))));Assert.Empty(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols);
    }
    [Theory][InlineData("parts")][InlineData("zones")]
    public async Task ExplicitNullContentOperationsAreIdempotentAndDoNotAddHistory(string type)
    {
        await using var f=await Fixture.Create();var request=f.Request(type=="parts"?"set_parts_table":"set_zones",("sheetId",f.Sheet.ToString()),("authorJson",type=="parts"?"{\"positionMm\":null}":"{\"zones\":null}"));
        var before=f.Session.Snapshot;var generation=f.Session.Generation;var result=await f.Call(request);Success(result);Assert.False(result["changed"]!.GetValue<bool>());Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);
    }
    [Theory][InlineData("revision")][InlineData("asset")][InlineData("fingerprint")][InlineData("index")][InlineData("body")][InlineData("path")][InlineData("anchor")]
    public async Task SelectionProofCannotBeReplacedByClientCachesOrForeignNativeIdentity(string failure)
    {
        await using var f=await Fixture.Create();var source=(await f.Sources()).First(s=>s["geometry"]!.GetValue<string>()=="PlaneFace")["selection"]!.DeepClone();
        if(failure=="revision")source["revision"]=Guid.NewGuid().ToString();if(failure=="asset")source["asset"]=new string('0',64);
        if(failure=="fingerprint")source["fingerprint"]=new string('0',64);if(failure=="index")source["index"]=100000;
        if(failure=="body")source["bodyId"]=Guid.NewGuid().ToString();if(failure=="path")source["path"]=Guid.NewGuid().ToString()+"/"+Guid.NewGuid();
        if(failure=="anchor")source["localPoint"]=Vector(0,0,0);
        var before=f.Session.Snapshot;var count=f.Assets.Count;var request=f.Request("create",("type","datum"),("sheetId",f.Sheet.ToString()),("authorJson",new JsonObject{["viewId"]=f.View.ToString(),["name"]="A",["source"]=source,["positionMm"]=Point(35,30)}.ToJsonString()));
        Failure(await f.Call(request));Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
    [Fact]
    public async Task ImportedBodyWithoutProducerHasToolSelectableOriginalTopology()
    {
        await using var f=await Fixture.Create();var path=f.PathFor("import.step");await f.Kernel.ExportAsync(f.Session.Snapshot,f.Assets,path);
        using var loaded=await f.Kernel.ImportAsync(path,f.Assets);
        // Imported assets can also be attached without an author feature (legacy/asset-only
        // snapshots). Keep the native imported BRep and explicitly construct that supported state.
        var assetOnly=loaded.Snapshot with{Features=ImmutableDictionary<FeatureId,FeatureDefinition>.Empty,
            Bodies=loaded.Snapshot.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Producer=null}),
            Definitions=loaded.Snapshot.Definitions.ToImmutableDictionary(p=>p.Key,p=>p.Value is PartDefinition part?(CadDefinition)(part with{Features=[]}):p.Value)};
        assetOnly.Validate();Assert.All(assetOnly.Bodies.Values,b=>Assert.Null(b.Producer));
        await using var session=new CadDocumentSession(assetOnly,f.Assets,f.Kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,f.Kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);var body=session.Snapshot.Bodies.Values.Single();var occurrence=session.Snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==body.PartId);
            var request=new{action="inspect_sources",documentId=session.Snapshot.Id.ToString(),stateId=session.Snapshot.StateId.ToString(),path=occurrence.Path.ToString(),bodyId=body.Id.ToString(),revision=body.Geometry.Revision.ToString(),limit=50};
            var result=Parse(await tools.ExecuteAsync(new("i","cad_drawing",JsonSerializer.Serialize(request)),default));Success(result);Assert.Contains(result["items"]!.AsArray(),s=>s!["selection"] is not null&&s["geometry"]!.GetValue<string>()=="PlaneFace");
        }
        finally{document.Detach();}
    }
    [Fact]
    public async Task ExactHistoryContinuationSurvivesCopiedEditAndNativeSaveWhileChangedOriginFreezes()
    {
        await using var f=await Fixture.Create();var source=(await f.Sources()).First(s=>s["geometry"]!.GetValue<string>()=="PlaneFace")["selection"]!;
        var datum=await f.Create("datum",new JsonObject{["viewId"]=f.View.ToString(),["name"]="A",["source"]=source.DeepClone(),["positionMm"]=Point(35,30)});var id=datum["id"]!.GetValue<Guid>();
        var original=f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source;var body=f.Session.Snapshot.Bodies.Values.Single();var box=f.Session.Snapshot.Features.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,true,0),"Hole"));
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].Views.Single().StaleReason);
        var viewEdit=await f.Edit("view",f.View);viewEdit["action"]="reselect";Success(await f.Call(viewEdit));
        var current=f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single();Assert.Null(current.StaleReason);Assert.Equal(original,current.Source.HistoryOrigin);
        var edit=await f.Edit("datum",id);var author=Author(edit);author["positionMm"]=Point(40,35);edit["authorJson"]=author.ToJsonString();Success(await f.Call(edit));
        Assert.Equal(original,f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source.HistoryOrigin);
        var path=f.PathFor("history.cadoryx");Success(await f.File("save",path));using var reopened=await f.Storage.LoadAsync(path,f.Assets);Assert.Equal(original,reopened.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source.HistoryOrigin);
        await f.Session.ExecuteAsync(new RecomputeCommand(box.Id,((BoxRecipe)box.Recipe) with{X=13}));var stale=f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single();Assert.NotNull(stale.StaleReason);Assert.Equal(original,stale.Source.HistoryOrigin);
        Failure(await f.File("pdf",f.PathFor("stale-history.pdf")));Assert.False(File.Exists(f.PathFor("stale-history.pdf")));
    }
    [Theory][InlineData("cancel")][InlineData("close")][InlineData("state")]
    public Task LateNativeProjectionCannotCommitAfterCancellationCloseOrOldState(string failure)=>RunHeldNativeProjection(failure);

    [Fact]
    public async Task FailedProjectionGateSetupCannotBlockCleanup()
    {
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>RunHeldNativeProjection("setup"));
        Assert.Equal("Simulated readiness assertion failure",error.Message);
    }

    private static async Task RunHeldNativeProjection(string failure)
    {
        var assets=new MemoryAssetStore();var kernel=new DelayedDrawingKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Delayed drawing"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Box"));var sheet=DrawingSheetTemplate.Create("Sheet");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());var tools=new CadAgentToolset(new CadCommandLineService(),document);using var cancellation=new CancellationTokenSource();
        try
        {
            var before=session.Snapshot;var count=assets.Count;kernel.Delay=true;
            var pending=tools.ExecuteAsync(new("p","cad_drawing",JsonSerializer.Serialize(new{action="create",type="view",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),sheetId=sheet.Id.ToString(),authorJson="{\"name\":\"Top\",\"kind\":\"Top\",\"centerMm\":{\"x\":50,\"y\":50},\"scale\":1}"})),cancellation.Token);
            await kernel.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if(failure=="setup")throw new InvalidOperationException("Simulated readiness assertion failure");
            if(failure=="cancel")cancellation.Cancel();
            Task? closing=failure=="close"?session.DisposeAsync().AsTask():null;
            if(failure=="state")await session.ExecuteAsync(new EditDocumentCommand("Rename",doc=>doc with{Name="Concurrent author"}));
            var expected=session.Snapshot;kernel.Release.SetResult();
            if(failure is "cancel" or "close")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);else Failure(Parse(await pending));
            if(closing is not null)await closing;
            Assert.Same(expected,session.Snapshot);Assert.Empty(session.Snapshot.DrawingSheets[sheet.Id].Views);Assert.Equal(failure=="close"?0:count,assets.Count);
        }
        finally
        {
            document.Detach();var closing=session.DisposeAsync();kernel.Release.TrySetResult();
            await closing.AsTask().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,assets.Count);
        }
    }
    private sealed class DelayedDrawingKernel:IGeometryKernel,ITechnicalDrawingKernel
    {
        private readonly OcctGeometryKernel native=new();internal bool Delay;
        internal TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public async Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> inputs,DrawingViewKind kind,Vector3d? normal,double offset,IAssetStore assets,CancellationToken token=default,double spacing=2.5,IReadOnlyList<SectionHatchPattern>? patterns=null)
        {var projection=await native.ProjectAsync(inputs,kind,normal,offset,assets,CancellationToken.None,spacing,patterns);if(Delay){Delay=false;Started.SetResult();await Release.Task;}return projection;}
    }
    private static JsonObject Parse(string json)=>JsonNode.Parse(json)!.AsObject();
    private static void Success(JsonObject value)=>Assert.True(value["success"]!.GetValue<bool>(),value.ToJsonString());
    private static void Failure(JsonObject value)=>Assert.False(value["success"]!.GetValue<bool>(),value.ToJsonString());
    private static JsonObject Author(JsonObject request)=>Parse(request["authorJson"]!.GetValue<string>());
    private static JsonObject Point(double x,double y)=>new(){["x"]=x,["y"]=y};
    private static JsonObject Vector(double x,double y,double z)=>new(){["x"]=x,["y"]=y,["z"]=z};
    private static JsonObject Frame(double x,double y,double z)=>new(){["origin"]=Vector(x,y,z),["normal"]=Vector(0,0,1),["u"]=Vector(1,0,0)};
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();internal CadDocumentStorage Storage {get;}
        internal CadDocumentSession Session {get;}internal CadDocumentViewModel Document {get;}internal CadAgentToolset Tools {get;}
        internal Guid Sheet {get;private set;}internal Guid View {get;private set;}internal DefinitionId Part=>Session.Snapshot.Bodies.Values.Single().PartId;
        internal OccurrencePath Path=>Session.Snapshot.EnumerateOccurrences().Single().Path;
        internal string Directory {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx-drawing-agent-"+Guid.NewGuid().ToString("N"));
        private Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);Storage=new(datumResolver:Kernel);Session=new(DocumentSnapshot.Create("Drawing tools"),Assets,Kernel,new InlineSessionDispatcher());
            var files=new DocumentFileService(Storage,Kernel,new DocumentDeliveryService(Storage,Kernel));Document=new(Session,Kernel,new CadMessageLog(),Storage,fileService:files);Tools=new(new CadCommandLineService(),Document);
        }
        internal static async Task<Fixture> Create(bool hole=false)
        {
            var f=new Fixture();await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Part"));
            if(hole){var body=f.Session.Snapshot.Bodies.Values.Single();await f.Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,false,6),"Hole"));}
            var sheet=DrawingSheetTemplate.Create("Sheet");f.Sheet=sheet.Id;f.View=Guid.NewGuid();await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(f.Sheet,f.View,"Top",DrawingViewKind.Top,new(80,60),2));return f;
        }
        internal string PathFor(string name)=>System.IO.Path.Combine(Directory,name);
        internal JsonObject Request(string action,params (string Name,JsonNode? Value)[] fields)
        {
            var request=new JsonObject{["action"]=action,["documentId"]=Session.Snapshot.Id.ToString(),["stateId"]=Session.Snapshot.StateId.ToString()};
            foreach(var (name,value) in fields)request[name]=value?.DeepClone();return request;
        }
        internal JsonObject Refresh(JsonObject request){request=request.DeepClone().AsObject();request["documentId"]=Session.Snapshot.Id.ToString();request["stateId"]=Session.Snapshot.StateId.ToString();return request;}
        internal async Task<JsonObject> Call(JsonObject request,CancellationToken token=default)=>Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",request.ToJsonString()),token));
        internal async Task<JsonObject> Create(string type,object author)
        {
            var request=Request("create",("type",type),("authorJson",author is JsonNode node?node.ToJsonString():JsonSerializer.Serialize(author)));
            if(type!="sheet")request["sheetId"]=Sheet.ToString();var result=await Call(request);Success(result);return result;
        }
        internal async Task<JsonObject> Inspect(string type,Guid? id=null)
        {var request=Request("inspect",("type",type));if(type is not ("sheet" or "template"))request["sheetId"]=Sheet.ToString();if(id is {} i)request["id"]=i.ToString();var result=await Call(request);Success(result);return result;}
        internal async Task<JsonObject> Edit(string type,Guid id)=>(await Inspect(type,id))["items"]![0]!["edit"]!.DeepClone().AsObject();
        internal async Task<JsonObject> File(string action,string path,Guid? id=null)
        {var request=Request(action,("path",path));if(id is {} i)request["id"]=i.ToString();return Parse(await Tools.ExecuteAsync(new("f","cad_file",request.ToJsonString()),default));}
        internal async Task<JsonNode[]> Sources()
        {
            var body=Session.Snapshot.Bodies.Values.Single();var request=Request("inspect_sources",("path",Path.ToString()),("bodyId",body.Id.ToString()),("revision",body.Geometry.Revision.ToString()),("limit",50));
            var result=await Call(request);Success(result);return result["items"]!.AsArray().Where(s=>s!["selection"] is not null).Select(s=>s!).ToArray();
        }
        internal Task<DrawingTopologyReference> Resolve(JsonNode selection)=>Kernel.ResolveDrawingTopologyAsync(Session.Snapshot,Path,new(selection["bodyId"]!.GetValue<Guid>()),selection["index"]!.GetValue<int>(),selection["fingerprint"]!.GetValue<string>(),Assets);
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);System.IO.Directory.Delete(Directory,true);}
    }
}
