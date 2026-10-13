using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.AI.Contracts;
using Cadoryx.Agent;
using Cadoryx.CommandLine;
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
public sealed class DrawingInspectionTypeFeedbackTests
{
    private static readonly string[] AllowedTypes=["sheet","template","view","dimension","datum","control_frame","balloon","hole_table","field","note"];
    private static readonly string[] Faults=["omitted","null","empty","unknown","parts_table","zones"];
    public static IEnumerable<object[]> InvalidTypes()=>from fault in Faults from sheet in new[]{false,true} select new object[]{fault,sheet};
    [Theory,MemberData(nameof(InvalidTypes))]
    public async Task InvalidInspectionTypeIsRejectedForEmptyAndPopulatedDocumentsWithoutChangingHistory(string fault,bool hasSheet)
    {
        await using var f=new Fixture();if(hasSheet)await f.CreateSheet();var before=f.Session.Snapshot;bool undo=f.Session.CanUndo,redo=f.Session.CanRedo;
        using var result=await f.Query(Request(before,fault));var r=result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.Equal(new[]{"type"},Strings(r,"missingOrInvalid"));
        Assert.Equal(AllowedTypes,Strings(r,"allowedTypes"));Assert.Equal(new[]{"action","type","documentId","stateId"},Strings(r,"requiredArguments"));
        Assert.Equal("contract",r.GetProperty("contractRequest").GetProperty("action").GetString());
        Assert.False(r.GetProperty("contractRequest").TryGetProperty("documentId",out _));Assert.False(r.TryGetProperty("items",out _));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(undo,f.Session.CanUndo);Assert.Equal(redo,f.Session.CanRedo);Assert.Equal(0,f.Assets.Count);
    }
    public static IEnumerable<object[]> ForeignCases()=>from fault in Faults from document in new[]{false,true} select new object[]{fault,document};
    [Theory,MemberData(nameof(ForeignCases))]
    public async Task ForeignDocumentOrStateIsRejectedBeforeTypeGuidanceWithoutDisclosure(string fault,bool foreignDocument)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=Request(before,fault);
        request[foreignDocument?"documentId":"stateId"]=Guid.NewGuid().ToString();
        using var result=await f.Query(request);var r=result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.False(r.TryGetProperty("allowedTypes",out _));
        Assert.False(r.TryGetProperty("requiredArguments",out _));Assert.False(r.TryGetProperty("missingOrInvalid",out _));
        Assert.False(r.TryGetProperty("items",out _));Assert.Same(before,f.Session.Snapshot);Assert.False(f.Session.CanUndo);Assert.Equal(0,f.Assets.Count);
    }
    [Theory,InlineData(false),InlineData(true)]
    public async Task PreCancelledInspectionDoesNotReturnGuidanceOrReadAuthors(bool providedType)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;using var cancel=new CancellationTokenSource();cancel.Cancel();
        var request=Request(before,providedType?"unknown":"omitted");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Query(request,cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.False(f.Session.CanUndo);Assert.Equal(0,f.Assets.Count);
    }
    [Fact]
    public async Task CallerExplicitlyChoosesReturnedTypeAndRetriesWithItsExactIdentityPair()
    {
        await using var f=new Fixture();await f.CreateSheet();var before=f.Session.Snapshot;var request=Request(before,"omitted");
        using(var rejected=await f.Query(request))Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.False(request.ContainsKey("type"));request["type"]="sheet";
        using var accepted=await f.Query(request);var r=accepted.RootElement;Assert.True(r.GetProperty("success").GetBoolean());
        Assert.Equal("sheet",r.GetProperty("type").GetString());Assert.Equal(before.Id.ToString(),r.GetProperty("documentId").GetString());
        Assert.Equal(before.StateId.ToString(),r.GetProperty("stateId").GetString());Assert.Single(r.GetProperty("items").EnumerateArray());
        Assert.Same(before,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Empty(f.Session.Snapshot.DrawingSheets);Assert.Equal(0,f.Assets.Count);
    }
    [Theory,InlineData(false),InlineData(true)]
    public void DefaultTwelveToolsAnd8192ContextBudgetKeepTheExplicitInspectionRequirement(bool aggressive)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);var definition=tools.ToolDefinitions.Single(t=>t.Name=="cad_drawing");
        Assert.StartsWith("contract: only action/type, no IDs.",definition.Description);Assert.Contains("type+documentId+stateId",definition.Description);
        Assert.Contains("Required for inspect",definition.Parameters.GetProperty("properties").GetProperty("type").GetProperty("description").GetString());
        Assert.Equal(new[]{"action"},Strings(definition.Parameters,"required"));Assert.False(definition.Parameters.GetProperty("additionalProperties").GetBoolean());
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User("Use cad_drawing inspect sheet")],tools.SelectTools("Use cad_drawing inspect sheet",aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.Equal(1024,context.MaxOutputTokens);Assert.True(context.EstimatedPromptTokens<=8192-1024-(aggressive?1280:640));
    }
    [Fact]
    public async Task UnknownOuterFieldKeepsItsOriginalPreExecutionRejectionWithoutTypeSubstitution()
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=Request(before,"omitted");request["name"]="not an inspect field";
        using var result=await f.Query(request);var r=result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.Equal(new[]{"name"},Strings(r,"unexpectedArguments"));
        Assert.Empty(Strings(r,"missingOrInvalid"));Assert.False(r.TryGetProperty("allowedTypes",out _));Assert.False(request.ContainsKey("type"));
        Assert.Same(before,f.Session.Snapshot);Assert.False(f.Session.CanUndo);Assert.Equal(0,f.Assets.Count);
    }
    private static JsonObject Request(DocumentSnapshot snapshot,string fault)
    {
        var request=new JsonObject{["action"]="inspect",["documentId"]=snapshot.Id.ToString(),["stateId"]=snapshot.StateId.ToString()};
        if(fault!="omitted")request["type"]=fault switch{"null"=>null,"empty"=>"","unknown"=>"not-a-drawing-type",_=>fault};
        return request;
    }
    private static string[] Strings(JsonElement result,string field)=>result.GetProperty(field).EnumerateArray().Select(v=>v.GetString()!).ToArray();
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}private CadDocumentViewModel Document{get;}private CadAgentToolset Tools{get;}
        internal Fixture(){var kernel=new OcctGeometryKernel();Session=new(DocumentSnapshot.Create("Typed drawing feedback"),Assets,kernel,new InlineSessionDispatcher());Document=new(Session,kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);}
        internal async Task<JsonDocument> Query(JsonObject args,CancellationToken token=default)=>JsonDocument.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",args.ToJsonString()),token));
        internal async Task CreateSheet()
        {
            var before=Session.Snapshot;using var result=await Query(new JsonObject{["action"]="create",["type"]="sheet",["documentId"]=before.Id.ToString(),["stateId"]=before.StateId.ToString(),
                ["authorJson"]="{\"name\":\"Typed sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Typed sheet\",\"standard\":\"ISO\"}"});
            Assert.True(result.RootElement.GetProperty("success").GetBoolean());Assert.Single(Session.Snapshot.DrawingSheets);
        }
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);Assert.Equal(0,Assets.SizeBytes);}
    }
}
