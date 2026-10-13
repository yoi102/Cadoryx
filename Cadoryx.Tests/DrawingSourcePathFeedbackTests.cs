using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DrawingSourcePathFeedbackTests
{
    private static readonly string[] SourceAreas=["occurrences","bodies"];
    [Theory]
    [InlineData("leaf")][InlineData("document")][InlineData("foreign")][InlineData("empty-slot")]
    [InlineData("zero-slot")][InlineData("malformed-slot")][InlineData("missing-slot")][InlineData("too-deep")]
    public async Task InvalidPathsReturnDiscoveryOnlyWithoutNativeCallsOrDocumentChanges(string fault)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=Request(before,PathFor(before,fault));var original=request.ToJsonString();
        using var rejected=await f.Query(request);var r=rejected.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.Equal("drawing_source_path_invalid",r.GetProperty("rejection").GetString());
        Assert.Equal("inspect_sources",r.GetProperty("action").GetString());Assert.NotEmpty(r.GetProperty("message").GetString()!);
        var reads=r.GetProperty("sourceReads").EnumerateArray().ToArray();Assert.Equal(2,reads.Length);
        Assert.Equal(SourceAreas,reads.Select(q=>q.GetProperty("arguments").GetProperty("area").GetString()));
        foreach(var read in reads)
        {
            Assert.Equal("cad_inspect",read.GetProperty("toolName").GetString());var args=read.GetProperty("arguments");
            Assert.Equal(0,args.GetProperty("offset").GetInt32());Assert.Equal(8,args.GetProperty("limit").GetInt32());Assert.Equal(3,args.EnumerateObject().Count());
        }
        Assert.Contains("complete",r.GetProperty("pathRole").GetString());Assert.Contains("revision",r.GetProperty("bodyRole").GetString());
        Assert.Contains("explicitly",r.GetProperty("nextStep").GetString());Assert.False(r.TryGetProperty("items",out _));Assert.False(r.TryGetProperty("selection",out _));
        Assert.False(r.TryGetProperty("documentId",out _));Assert.False(r.TryGetProperty("path",out _));Assert.Equal(original,request.ToJsonString());
        // The caller may issue the suggested read; returning it never executes it.
        using var discovered=JsonDocument.Parse(await f.Tools.ExecuteAsync(new("read","cad_inspect",reads[0].GetProperty("arguments").GetRawText()),default));
        Assert.True(discovered.RootElement.GetProperty("success").GetBoolean());Assert.Empty(discovered.RootElement.GetProperty("items").EnumerateArray());
        f.Unchanged(before);
    }
    [Theory][InlineData("documentId","leaf")][InlineData("stateId","leaf")][InlineData("documentId","foreign")][InlineData("stateId","foreign")]
    public async Task ForeignIdentityRejectsBeforePathDiscoveryWithoutDisclosure(string identity,string fault)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=Request(before,PathFor(before,fault));request[identity]=Guid.NewGuid().ToString("D");
        using var rejected=await f.Query(request);var r=rejected.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.False(r.TryGetProperty("sourceReads",out _));Assert.False(r.TryGetProperty("rejection",out _));f.Unchanged(before);
    }
    [Theory][InlineData("leaf")][InlineData("foreign")]
    public async Task PreCancelledRequestDoesNotParseOrProvideSourceDiscovery(string fault)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Query(Request(before,PathFor(before,fault)),cancel.Token));f.Unchanged(before);
    }
    [Fact]
    public async Task DetachedDocumentRetainsOriginalGuardWithoutSourceDiscovery()
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;f.Document.Detach();
        using var rejected=await f.Query(Request(before,PathFor(before,"leaf")));Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.False(rejected.RootElement.TryGetProperty("sourceReads",out _));f.Unchanged(before);
    }
    [Theory][InlineData("omitted")][InlineData("null")][InlineData("empty")][InlineData("oversize")]
    public async Task MissingOrInvalidStringRetainsInputGuardWithoutSourceDiscovery(string fault)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=Request(before,PathFor(before,"leaf"));
        if(fault=="omitted")request.Remove("path");else request["path"]=fault switch{"null"=>null,"empty"=>"",_=>new string('x',8193)};
        using var rejected=await f.Query(request);Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.False(rejected.RootElement.TryGetProperty("sourceReads",out _));f.Unchanged(before);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ExplicitPathSourceFitsOriginalTwelveToolsAndContextBudgets(bool aggressive)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);var definition=tools.ToolDefinitions.Single(t=>t.Name=="cad_drawing");
        Assert.Equal(8192,definition.Parameters.GetProperty("properties").GetProperty("path").GetProperty("maxLength").GetInt32());
        Assert.Contains("occurrences.items[].path",definition.Parameters.GetProperty("properties").GetProperty("path").GetProperty("description").GetString());
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User("Use cad_drawing inspect_sources")],tools.SelectTools("Use cad_drawing inspect_sources",aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.Equal(1024,context.MaxOutputTokens);Assert.True(context.EstimatedPromptTokens<=8192-1024-(aggressive?1280:640));
    }
    private static string PathFor(DocumentSnapshot doc,string fault)=>fault switch
    {
        "leaf"=>Guid.NewGuid().ToString("D"),"document"=>doc.Id.ToString(),"foreign"=>Guid.NewGuid()+"/"+Guid.NewGuid(),
        "empty-slot"=>doc.Id+"/","zero-slot"=>doc.Id+"/"+Guid.Empty,"malformed-slot"=>doc.Id+"/not-a-guid",
        "missing-slot"=>doc.Id+"/"+Guid.NewGuid(),"too-deep"=>doc.Id+"/"+string.Join('/',Enumerable.Repeat(Guid.NewGuid().ToString("D"),130)),_=>throw new ArgumentException(fault)
    };
    private static JsonObject Request(DocumentSnapshot snapshot,string path)=>new(){["action"]="inspect_sources",["documentId"]=snapshot.Id.ToString(),["stateId"]=snapshot.StateId.ToString(),["path"]=path,["bodyId"]=Guid.NewGuid().ToString("D"),["revision"]=Guid.NewGuid().ToString("D")};
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal CadAgentToolset Tools{get;}private NeverGeometryKernel Kernel{get;}=new();
        internal Fixture(){Session=new(DocumentSnapshot.Create("Source path guide"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);}
        internal async Task<JsonDocument> Query(JsonObject args,CancellationToken token=default)=>JsonDocument.Parse(await Tools.ExecuteAsync(new("query","cad_drawing",args.ToJsonString()),token));
        internal void Unchanged(DocumentSnapshot before){Assert.Same(before,Session.Snapshot);Assert.False(Session.CanUndo);Assert.False(Session.CanRedo);Assert.Equal(0,Assets.Count);Assert.Equal(0,Assets.SizeBytes);Assert.Equal(0,Kernel.Calls);}
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);Assert.Equal(0,Kernel.Calls);}
    }
    private sealed class NeverGeometryKernel:IGeometryKernel
    {
        public int Calls{get;private set;}public string Version=>"Managed rejection fixture";
        private Exception Unexpected(){Calls++;return new InvalidOperationException("Rejected path must never evaluate, import or export geometry.");}
        public bool Supports(GeometryRecipe recipe)=>throw Unexpected();
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>throw Unexpected();
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>throw Unexpected();
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>throw Unexpected();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>throw Unexpected();
    }
}
