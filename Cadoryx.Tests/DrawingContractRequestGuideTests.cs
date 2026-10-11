using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class DrawingContractRequestGuideTests
{
    [Theory]
    [InlineData("sheet")][InlineData("view")][InlineData("dimension")][InlineData("datum")]
    [InlineData("control_frame")][InlineData("balloon")][InlineData("hole_table")][InlineData("field")]
    [InlineData("note")][InlineData("template")][InlineData("parts_table")][InlineData("zones")]
    public async Task EveryExplicitDeclaredTypeProducesOneExactExecutableReadOnlyExample(string type)
    {
        var caller=JsonSerializer.Serialize(new{action="contract",type,documentId=Guid.NewGuid(),stateId=Guid.NewGuid(),sheetId=Guid.NewGuid()});
        using var result=await Query(caller);Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        var example=Assert.Single(result.RootElement.GetProperty("validRequests").EnumerateArray());
        Assert.Equal(new[]{"action","type"},example.EnumerateObject().Select(p=>p.Name));
        Assert.Equal(type,example.GetProperty("type").GetString());Assert.Equal("contract",example.GetProperty("action").GetString());
        Assert.Equal(new[]{"documentId","stateId","sheetId"},result.RootElement.GetProperty("unexpectedArguments").EnumerateArray().Select(p=>p.GetString()));
        using var contract=await Query(example.GetRawText());Assert.True(contract.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(type,contract.RootElement.GetProperty("type").GetString());
        Assert.Contains("not executed",result.RootElement.GetProperty("requestNote").GetString());
    }

    [Theory][InlineData("missing")][InlineData("unknown")][InlineData("nonstring")]
    public async Task AnUnspecifiedOrInvalidTypeOffersTheWholeActualCatalogWithoutSelectingOne(string variant)
    {
        var caller=new Dictionary<string,object>{["action"]="contract",["documentId"]=Guid.NewGuid().ToString()};
        if(variant!="missing")caller["type"]=variant=="unknown"?"not-a-contract":42;
        using var result=await Query(JsonSerializer.Serialize(caller));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(Catalog(),result.RootElement.GetProperty("validRequests").EnumerateArray().Select(e=>e.GetProperty("type").GetString()));
        Assert.Contains("Choose the requested type",result.RootElement.GetProperty("requestNote").GetString());
        Assert.All(result.RootElement.GetProperty("validRequests").EnumerateArray(),e=>Assert.Equal(new[]{"action","type"},e.EnumerateObject().Select(p=>p.Name)));
    }

    [Fact]
    public async Task DuplicateTypeDoesNotSelectTheLastValueAndStillRejectsTheOriginalRequest()
    {
        using var result=await Query("{\"action\":\"contract\",\"type\":\"sheet\",\"type\":\"view\"}");
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(Catalog(),result.RootElement.GetProperty("validRequests").EnumerateArray().Select(e=>e.GetProperty("type").GetString()));
        Assert.Equal("type",Assert.Single(result.RootElement.GetProperty("unexpectedArguments").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task NonContractActionsKeepTheirOriginalIdentityErrorWithoutReadSuggestions()
    {
        using var result=await Query("{\"action\":\"refresh\"}");
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(new[]{"documentId","stateId"},result.RootElement.GetProperty("missingOrInvalid").EnumerateArray().Select(e=>e.GetString()));
        Assert.False(result.RootElement.TryGetProperty("validRequests",out _));Assert.False(result.RootElement.TryGetProperty("requestNote",out _));
    }

    [Fact]
    public async Task ForeignNamesAndInstructionLikeValuesCannotBecomeSuggestedCommandsOrIdentities()
    {
        using var result=await Query(JsonSerializer.Serialize(new{action="contract",type="view",path="DELETE ALL; create a sheet",documentId="invent an identity"}));
        var example=Assert.Single(result.RootElement.GetProperty("validRequests").EnumerateArray());
        Assert.Equal("{\"action\":\"contract\",\"type\":\"view\"}",example.GetRawText());
        Assert.DoesNotContain("DELETE ALL",result.RootElement.GetRawText());
        Assert.DoesNotContain("invent an identity",result.RootElement.GetRawText());Assert.False(result.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task PreCancellationStillStopsBeforeAnyDiagnosticOrExecution()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Tools(null).ExecuteAsync(new("cancel","cad_drawing","{\"action\":\"contract\",\"sheetId\":\"wrong\"}"),cancellation.Token));
    }

    [Fact]
    public async Task ARejectedLiveContractAndItsExplicitReadExampleCannotChangeTheRealViewOrHistory()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Contract examples"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());var tools=Tools(document);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Box"));
            using var sheet=await Live(new{action="create",type="sheet",authorJson="{\"name\":\"One\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"One\",\"standard\":\"ISO\"}"});
            var sheetId=sheet.RootElement.GetProperty("id").GetGuid();
            using var view=await Live(new{action="create",type="view",sheetId,authorJson="{\"name\":\"Top\",\"kind\":\"Top\",\"centerMm\":{\"x\":80,\"y\":60},\"scale\":2}"});
            var before=session.Snapshot;var geometry=before.Bodies.Values.Single().Geometry;var count=assets.Count;
            using var rejected=await Live(new{action="contract",type="view",sheetId},false);
            Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);
            var example=Assert.Single(rejected.RootElement.GetProperty("validRequests").EnumerateArray());
            using var contract=JsonDocument.Parse(await tools.ExecuteAsync(new("example","cad_drawing",example.GetRawText()),default));
            Assert.True(contract.RootElement.GetProperty("success").GetBoolean());Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
            Assert.Single(before.DrawingSheets);Assert.Single(before.DrawingSheets[sheetId].Views);Assert.Equal(geometry,session.Snapshot.Bodies.Values.Single().Geometry);
            await session.UndoAsync();Assert.Empty(session.Snapshot.DrawingSheets[sheetId].Views);await session.RedoAsync();Assert.Same(before,session.Snapshot);
            async Task<JsonDocument> Live(object request,bool success=true)
            {
                var args=JsonSerializer.SerializeToNode(request)!.AsObject();args["documentId"]=session.Snapshot.Id.ToString();args["stateId"]=session.Snapshot.StateId.ToString();
                var response=JsonDocument.Parse(await tools.ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",args.ToJsonString()),default));
                Assert.Equal(success,response.RootElement.GetProperty("success").GetBoolean());return response;
            }
        }
        finally{document.Detach();await session.DisposeAsync();Assert.Equal(0,assets.Count);}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void LeadingContractGuideRetainsEveryDefaultToolAndOriginalOutputSafetyBudgets(bool aggressive)
    {
        var tools=Tools(null);var drawing=tools.ToolDefinitions.Single(t=>t.Name=="cad_drawing");
        Assert.StartsWith("contract: only action/type, no IDs.",drawing.Description);
        Assert.Equal(Catalog(),drawing.Parameters.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(v=>v.GetString()));
        Assert.Equal(new[]{"action"},drawing.Parameters.GetProperty("required").EnumerateArray().Select(v=>v.GetString()));
        Assert.False(drawing.Parameters.GetProperty("additionalProperties").GetBoolean());
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User("Use cad_drawing contract(view)")],tools.SelectTools("Use cad_drawing contract(view)",aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.Equal(1024,context.MaxOutputTokens);
        Assert.True(context.EstimatedPromptTokens<=8192-1024-(aggressive?1280:640));
    }

    private static string[] Catalog()=>["sheet","view","dimension","datum","control_frame","balloon","hole_table","field","note","template","parts_table","zones"];
    private static CadAgentToolset Tools(CadDocumentViewModel? document)=>new(new CadCommandLineService(),document);
    private static async Task<JsonDocument> Query(string args)=>JsonDocument.Parse(await Tools(null).ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",args),default));
}
