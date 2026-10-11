using System.Text.Json;
using Cadoryx.AI.Contracts;
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
public sealed class DrawingActionFeedbackTests
{
    [Theory]
    [InlineData("inspect")][InlineData("inspect_sources")][InlineData("create")]
    [InlineData("edit")][InlineData("reselect")][InlineData("delete")]
    [InlineData("set_parts_table")][InlineData("set_zones")][InlineData("save_template")]
    [InlineData("apply_template")][InlineData("import_template")][InlineData("detach_coordinates")][InlineData("refresh")]
    public async Task MissingIdentityPairIsReportedTogetherForEveryNonContractAction(string action)
    {
        using var result=await Query(new{action});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(new[]{"documentId","stateId"},Strings(result,"missingOrInvalid"));
        Assert.Empty(Strings(result,"unexpectedArguments"));
        Assert.Contains("cad_status",result.RootElement.GetProperty("message").GetString());
    }

    [Theory][InlineData("documentId")][InlineData("stateId")][InlineData("sheetId")][InlineData("name")]
    public async Task ContractExplainsItsExactFieldSetWithoutRequiringDocumentIdentity(string field)
    {
        using var result=await Query(new Dictionary<string,object>{["action"]="contract",["type"]="view",[field]=Guid.NewGuid().ToString()});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(Strings(result,"missingOrInvalid"));Assert.Equal(new[]{field},Strings(result,"unexpectedArguments"));
        Assert.Equal(new[]{"action","type"},Strings(result,"allowedArguments"));
    }

    [Fact]
    public async Task MissingIdentitiesAndWrongOuterNameAreReturnedInOneReply()
    {
        using var result=await Query(new{action="create",type="view",name="Top",authorJson="{}"});
        Assert.Equal(new[]{"documentId","stateId"},Strings(result,"missingOrInvalid"));
        Assert.Equal(new[]{"name"},Strings(result,"unexpectedArguments"));
        Assert.Contains("authorJson",result.RootElement.GetProperty("message").GetString());
    }

    [Theory][InlineData("document-empty")][InlineData("state-empty")][InlineData("both-invalid")][InlineData("document-null")]
    public async Task InvalidGuidValuesRemainErrorsWithoutSubstitution(string fault)
    {
        object? documentId=fault=="document-empty"?Guid.Empty.ToString():fault=="both-invalid"?"wrong-document":fault=="document-null"?null:Guid.NewGuid().ToString();
        string stateId=fault=="state-empty"?Guid.Empty.ToString():fault=="both-invalid"?"wrong-state":Guid.NewGuid().ToString();
        using var result=await Query(new{action="refresh",documentId,stateId});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(fault=="both-invalid"?new[]{"documentId","stateId"}:new[]{fault=="state-empty"?"stateId":"documentId"},Strings(result,"missingOrInvalid"));
    }

    [Fact]
    public async Task DuplicateIdentityIsRejectedBeforeExecution()
    {
        string id=Guid.NewGuid().ToString();
        using var result=await QueryJson("{\"action\":\"refresh\",\"documentId\":\""+id+"\",\"documentId\":\""+id+"\",\"stateId\":\""+Guid.NewGuid()+"\"}");
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());Assert.Empty(Strings(result,"missingOrInvalid"));
        Assert.Equal(new[]{"documentId"},Strings(result,"unexpectedArguments"));
    }

    [Fact]
    public async Task SaveTemplateStillAcceptsOuterNameAndReachesTheExistingDocumentGuard()
    {
        using var result=await Query(new{action="save_template",name="Template",sheetId=Guid.NewGuid().ToString(),documentId=Guid.NewGuid().ToString(),stateId=Guid.NewGuid().ToString()});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("unexpectedArguments",out _));
        Assert.Contains("document",result.RootElement.GetProperty("message").GetString(),StringComparison.OrdinalIgnoreCase);
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task CompleteButForeignIdentityStillUsesTheStrictStateGuard(bool foreignDocument)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;
        using var result=await f.Query(new{action="create",type="sheet",documentId=foreignDocument?Guid.NewGuid().ToString():before.Id.ToString(),
            stateId=foreignDocument?before.StateId.ToString():Guid.NewGuid().ToString(),authorJson=Author});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());Assert.False(result.RootElement.TryGetProperty("missingOrInvalid",out _));
        Assert.Same(before,f.Session.Snapshot);Assert.False(f.Session.CanUndo);Assert.Equal(0,f.Assets.Count);
    }

    [Fact]
    public async Task ExactIdentityAndContractAuthorStillCommitOnceAndUndo()
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;
        using var result=await f.Query(new{action="create",type="sheet",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),authorJson=Author});
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());Assert.True(f.Session.CanUndo);
        Assert.Equal("Feedback sheet",f.Session.Snapshot.DrawingSheets.Values.Single().Name);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);Assert.Equal(0,f.Assets.Count);
    }

    [Fact]
    public async Task ExcessUnknownNamesProduceBoundedGuidance()
    {
        var request=new Dictionary<string,object>{["action"]="refresh"};
        for(int i=0;i<20;i++)request[new string('x',200)+i]=i;
        using var result=await Query(request);var unexpected=Strings(result,"unexpectedArguments");
        Assert.Equal(16,unexpected.Length);Assert.All(unexpected,name=>Assert.InRange(name.Length,1,83));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
    }

    private const string Author="{\"name\":\"Feedback sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Feedback sheet\",\"standard\":\"ISO\"}";
    private static string[] Strings(JsonDocument result,string field)=>result.RootElement.GetProperty(field).EnumerateArray().Select(v=>v.GetString()!).ToArray();
    private static Task<JsonDocument> Query(object args)=>QueryJson(JsonSerializer.Serialize(args));
    private static async Task<JsonDocument> QueryJson(string args)=>JsonDocument.Parse(await new CadAgentToolset(new CadCommandLineService(),null).ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",args),default));
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}private CadDocumentViewModel Document{get;}private CadAgentToolset Tools{get;}
        internal Fixture(){var kernel=new OcctGeometryKernel();Session=new(DocumentSnapshot.Create("Feedback guard"),Assets,kernel,new InlineSessionDispatcher());Document=new(Session,kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);}
        internal async Task<JsonDocument> Query(object args)=>JsonDocument.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_drawing",JsonSerializer.Serialize(args)),default));
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
