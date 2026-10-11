using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class FileAgentTests
{
    [Theory][InlineData("save",".cadoryx",null)][InlineData("export",".step",null)]
    [InlineData("export",".iges",null)][InlineData("export",".stl",null)]
    [InlineData("report",".csv","BomCsv")][InlineData("report",".html","ReviewHtml")]
    [InlineData("report",".zip","Package")][InlineData("report",".zip","ExchangeAudit")]
    public async Task NativeFilesHaveActualHashesSourceIdentitiesAndPreserveHistory(string action,string extension,string? output)
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;var generation=f.Session.Generation;
        var request=f.Request(action,"out"+extension,output);var result=await f.Call(request);Success(result);
        var receipt=result.GetProperty("receipt");var path=receipt.GetProperty("path").GetString()!;
        Assert.Equal(before.Id.ToString(),receipt.GetProperty("documentId").GetString());Assert.Equal(before.StateId.ToString(),receipt.GetProperty("stateId").GetString());
        Assert.Equal(new FileInfo(path).Length,receipt.GetProperty("bytes").GetInt64());Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),receipt.GetProperty("sha256").GetString());
        Assert.True(result.GetProperty("published").GetBoolean());Assert.False(result.GetProperty("changed").GetBoolean());
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(generation,f.Session.Generation);
        if(action=="save")
        {Assert.False(f.Session.IsDirty);Assert.True(result.GetProperty("savedCurrentState").GetBoolean());using var loaded=await f.Storage.LoadAsync(path,f.Assets);Assert.Equal(before.StateId,loaded.Snapshot.StateId);Assert.Equal(24,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,7);}
        if(action=="export")
        {using var loaded=await f.Kernel.ImportAsync(path,f.Assets);loaded.Snapshot.Validate();Assert.Single(loaded.Snapshot.Bodies);}
        if(output=="Package")Assert.Equal(before.StateId.ToString(),(await DocumentDeliveryService.VerifyAsync(path)).StateId);
        if(output=="ExchangeAudit")Assert.Equal(before.StateId.ToString(),(await ExchangeMeasurementService.VerifyAsync(path)).StateId);
        await f.Session.UndoAsync();Assert.Empty(f.Session.Snapshot.Bodies);await f.Session.RedoAsync();Assert.Same(before,f.Session.Snapshot);
    }

    [Fact]
    public async Task TerminalOptionsKeepQuotesAndUseTheSameReceipt()
    {
        await using var f=await Fixture.Create();var command=new CadCommandLineService();var request=new{action="report",path=f.PathFor("with space.html"),options=new{output="ReviewHtml"}};
        var input="FILE "+JsonSerializer.Serialize(request);Assert.Equal(2,CadCommandLineService.Tokenize(input).Length);
        var result=await command.ExecuteAsync(input,new CadCommandContext(f.Document));Assert.True(result.Success,result.Message);Success(Parse(result.Message));
        var contract=await command.ExecuteAsync("FILE CONTRACT",new CadCommandContext(f.Document));Assert.True(contract.Success);Assert.Contains("ExchangeAudit",contract.Message);
        var duplicate=await command.ExecuteAsync("FILE {\"action\":\"save\",\"action\":\"save\"}",new CadCommandContext(f.Document));Assert.False(duplicate.Success);
        var both=await command.ExecuteAsync("FILE {\"action\":\"export\",\"options\":{},\"optionsJson\":\"{}\"}",new CadCommandContext(f.Document));Assert.False(both.Success);
    }

    [Theory][InlineData("documentId")][InlineData("stateId")]
    public async Task WrongIdentityCannotWrite(string field)
    {
        await using var f=await Fixture.Create();var request=f.Request("save","stale.cadoryx");request[field]=Guid.NewGuid().ToString();
        Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());Assert.False(File.Exists(f.PathFor("stale.cadoryx")));Assert.True(f.Session.IsDirty);
    }

    [Theory][InlineData("{\"unknown\":true}")][InlineData("{\"visibleOnly\":1}")]
    [InlineData("{\"binaryStl\":null}")][InlineData("{\"linearDeflectionMm\":0}")]
    [InlineData("{\"visibleOnly\":true,\"visibleOnly\":false}")][InlineData("{\"angularDeflectionRad\":4}")]
    public async Task InvalidExportOptionsAreRejected(string options)
    {
        await using var f=await Fixture.Create();var request=f.Request("export","bad.step");request["optionsJson"]=options;
        Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());Assert.False(File.Exists(f.PathFor("bad.step")));
    }

    [Theory][InlineData("{\"output\":0}")][InlineData("{\"output\":\"0\"}")]
    [InlineData("{\"output\":\"bomcsv\"}")][InlineData("{\"output\":\"ExchangeAudit\",\"includeStep\":false}")]
    public async Task ReportOutputUsesExactEnumsAndRequiresMeasuredFormats(string options)
    {
        await using var f=await Fixture.Create();var request=f.Request("report","bad.zip");request["optionsJson"]=options;
        Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());Assert.False(File.Exists(f.PathFor("bad.zip")));
    }

    [Fact]
    public async Task ReplacementIsExplicitAndWrongExtensionsOrRelativePathsCannotWrite()
    {
        await using var f=await Fixture.Create();var target=f.PathFor("out.cadoryx");await File.WriteAllTextAsync(target,"OLD");var request=f.Request("save","out.cadoryx");
        Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());Assert.Equal("OLD",await File.ReadAllTextAsync(target));
        request["overwrite"]=true;Success(await f.Call(request));Assert.NotEqual("OLD",await File.ReadAllTextAsync(target));
        request=f.Request("save","out.exe");Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());Assert.False(File.Exists(f.PathFor("out.exe")));
        request=f.Request("save","relative.cadoryx");request["path"]="relative.cadoryx";Assert.False((await f.Call(request)).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task QueuedOldStateCannotWriteAndCapturedSaveRemainsDirtyAfterEdit()
    {
        await using var f=await Fixture.Create(hold:true);var before=f.Session.Snapshot;var first=f.Call(f.Request("save","first.cadoryx"));await f.Hold!.Entered.Task;
        var queued=f.Call(f.Request("save","queued.cadoryx"));await f.Session.ExecuteAsync(new EditDocumentCommand("Rename document",doc=>doc with{Name="New name"}));var current=f.Session.Snapshot;
        f.Hold.Release.TrySetResult();var result=await first;Success(result);Assert.Equal(before.StateId.ToString(),result.GetProperty("receipt").GetProperty("stateId").GetString());
        Assert.Equal(current.StateId.ToString(),result.GetProperty("currentStateId").GetString());Assert.True(f.Session.IsDirty);Assert.False(result.GetProperty("savedCurrentState").GetBoolean());
        Assert.False((await queued).GetProperty("success").GetBoolean());Assert.False(File.Exists(f.PathFor("queued.cadoryx")));Assert.Equal(1,f.Hold.Calls);
        using var loaded=await f.Storage.LoadAsync(f.PathFor("first.cadoryx"),f.Assets);Assert.Equal(before.StateId,loaded.Snapshot.StateId);Assert.Equal(before.Name,loaded.Snapshot.Name);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PrePublicationCancellationOrCloseRetainsOldOutputAndReleasesCapture(bool close)
    {
        await using var f=await Fixture.Create(hold:true);var target=f.PathFor("out.step");await File.WriteAllTextAsync(target,"OLD");var request=f.Request("export","out.step");request["overwrite"]=true;
        using var cancellation=new CancellationTokenSource();var task=f.Call(request,cancellation.Token);await f.Hold!.Entered.Task;
        Task? disposal=null;if(close)disposal=f.Session.DisposeAsync().AsTask();else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);if(disposal is not null)await disposal;
        Assert.Equal("OLD",await File.ReadAllTextAsync(target));Assert.Empty(Directory.GetFiles(f.Directory,".cadoryx-file-*"));
        if(close)Assert.Equal(0,f.Assets.Count);else Assert.Equal(1,f.Assets.Count);
    }

    [Fact]
    public async Task CancellationAfterPublicationStillReturnsTheSuccessfulReceipt()
    {
        await using var f=await Fixture.Create(hold:true,after:true);using var cancellation=new CancellationTokenSource();var task=f.Call(f.Request("save","published.cadoryx"),cancellation.Token);
        await f.Hold!.Entered.Task;Assert.True(File.Exists(f.PathFor("published.cadoryx")));cancellation.Cancel();f.Hold.Release.TrySetResult();
        var result=await task;Success(result);Assert.True(result.GetProperty("published").GetBoolean());Assert.False(f.Session.IsDirty);
    }

    [Fact]
    public async Task RollbackExportsAreRejectedAndNativeSaveKeepsFullAuthorSnapshot()
    {
        await using var f=await Fixture.Create();var snapshot=f.Session.Snapshot;var part=snapshot.Definitions.Values.OfType<PartDefinition>().Single();
        await f.Session.RollbackFeaturesAsync(part.Id,0);Assert.Empty(f.Session.DisplaySnapshot.Bodies);
        Assert.False((await f.Call(f.Request("export","preview.step"))).GetProperty("success").GetBoolean());
        Assert.False((await f.Call(f.Request("report","preview.csv","BomCsv"))).GetProperty("success").GetBoolean());
        Success(await f.Call(f.Request("save","author.cadoryx")));using var loaded=await f.Storage.LoadAsync(f.PathFor("author.cadoryx"),f.Assets);Assert.Single(loaded.Snapshot.Bodies);Assert.Equal(snapshot.StateId,loaded.Snapshot.StateId);
        Assert.True(f.Session.IsRolledBack);
    }

    [Fact]
    public async Task ContractIsBoundedDiscoverableWithoutDocumentAndRequestsAreStrict()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);var contract=await tools.ExecuteAsync(new("c","cad_file","{\"action\":\"contract\"}"),default);
        Success(Parse(contract));Assert.True(contract.Length<=5500);var schema=tools.ToolDefinitions.Single(t=>t.Name=="cad_file").Parameters;
        Assert.Equal(JsonValueKind.Object,schema.ValueKind);Assert.Equal(JsonValueKind.Object,schema.GetProperty("properties").ValueKind);
        foreach(var json in new[]{"{\"action\":\"contract\",\"path\":\"x\"}","{\"action\":\"contract\",\"action\":\"contract\"}",new string('x',8193)})
            Assert.False(Parse(await tools.ExecuteAsync(new("c","cad_file",json),default)).GetProperty("success").GetBoolean());
    }
    [Theory][InlineData("save",false)][InlineData("save",true)][InlineData("export",false)][InlineData("export",true)]
    public async Task LateNativeWriteFailureOrCancellationCannotReplaceTheOriginal(string kind,bool cancel)
    {
        await using var f=await Fixture.Create();using var cancellation=new CancellationTokenSource();
        var fault=new NativeWriteBoundary(f.Storage,f.Kernel,()=>{if(cancel)cancellation.Cancel();else throw new InvalidOperationException("Native write boundary failure.");});
        var files=new DocumentFileService(fault,fault,new DocumentDeliveryService(fault,fault));var extension=kind=="save"?".cadoryx":".step";
        var path=f.PathFor("retained"+extension);await File.WriteAllTextAsync(path,"ORIGINAL");var before=f.Session.Snapshot;
        var task=f.Session.WriteFileAsync(files,new(kind,path,true),DocumentCommandExpectation.Capture(before),cancellation.Token);
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);else await Assert.ThrowsAsync<InvalidOperationException>(()=>task);
        Assert.True(fault.NativeCompleted);Assert.Equal("ORIGINAL",await File.ReadAllTextAsync(path));Assert.Same(before,f.Session.Snapshot);Assert.True(f.Session.IsDirty);
        Assert.Equal(1,f.Assets.Count);Assert.Empty(Directory.GetFiles(f.Directory,".cadoryx-file-*"));
    }
    private sealed class NativeWriteBoundary(IDocumentStorage storage,IGeometryKernel kernel,Action boundary):IDocumentStorage,IGeometryKernel
    {
        internal bool NativeCompleted{get;private set;}
        public string Version=>kernel.Version;
        public bool Supports(GeometryRecipe recipe)=>kernel.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>kernel.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>kernel.ImportAsync(path,assets,token);
        public Task<LoadedDocument> LoadAsync(string path,IAssetStore assets,CancellationToken token=default)=>storage.LoadAsync(path,assets,token);
        public Task<DocumentSettings> ReadSettingsAsync(string path,CancellationToken token=default)=>storage.ReadSettingsAsync(path,token);
        public async Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)
        {await storage.SaveAsync(snapshot,assets,path,token);NativeCompleted=File.Exists(path);boundary();}
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>kernel.ExportAsync(snapshot,assets,path,token);
        public async Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)
        {var result=await kernel.ExportAsync(snapshot,assets,path,options,token);NativeCompleted=File.Exists(path);boundary();return result;}
    }
    private static JsonElement Parse(string json){using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
    private static void Success(JsonElement result)=>Assert.True(result.GetProperty("success").GetBoolean(),result.GetRawText());

    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal OcctGeometryKernel Kernel{get;}=new();internal CadDocumentStorage Storage{get;}=new();
        internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal CadAgentToolset Tools{get;}internal HoldingFiles? Hold{get;}
        internal string Directory{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx-file-tests-"+Guid.NewGuid().ToString("N"));
        private Fixture(bool hold,bool after)
        {
            System.IO.Directory.CreateDirectory(Directory);var files=new DocumentFileService(Storage,Kernel,new DocumentDeliveryService(Storage,Kernel));
            Hold=hold?new(files,after):null;Session=new(DocumentSnapshot.Create("File tools"),Assets,Kernel,new InlineSessionDispatcher());
            Document=new(Session,Kernel,new CadMessageLog(),Storage,fileService:(IDocumentFileService?)Hold??files);Tools=new(new CadCommandLineService(),Document);
        }
        internal static async Task<Fixture> Create(bool hold=false,bool after=false)
        {var f=new Fixture(hold,after);await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Box"));return f;}
        internal string PathFor(string name)=>System.IO.Path.Combine(Directory,name);
        internal JsonObject Request(string action,string name,string? output=null)
        {
            var request=new JsonObject{["action"]=action,["documentId"]=Session.Snapshot.Id.ToString(),["stateId"]=Session.Snapshot.StateId.ToString(),["path"]=PathFor(name)};
            if(output is not null)request["optionsJson"]=JsonSerializer.Serialize(new{output});return request;
        }
        internal async Task<JsonElement> Call(JsonObject args,CancellationToken token=default)=>Parse(await Tools.ExecuteAsync(new("f","cad_file",args.ToJsonString()),token));
        public async ValueTask DisposeAsync(){Hold?.Release.TrySetResult();Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);System.IO.Directory.Delete(Directory,true);}
    }
    private sealed class HoldingFiles(IDocumentFileService real,bool after):IDocumentFileService
    {
        internal TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Calls{get;private set;}
        public async Task<DocumentFileReceipt> WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,DocumentFileRequest request,CancellationToken token=default)
        {
            Calls++;DocumentFileReceipt? receipt=null;if(after)receipt=await real.WriteAsync(snapshot,assets,request,token);
            Entered.TrySetResult();if(after)await Release.Task;else await Release.Task.WaitAsync(token);
            return receipt??await real.WriteAsync(snapshot,assets,request,token);
        }
    }
}
