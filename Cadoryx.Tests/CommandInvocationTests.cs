using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CommandInvocationTests
{
    [Theory]
    [InlineData("BOX New 2 3 4")][InlineData("GRID SPACING 17")][InlineData("UNDO")][InlineData("REDO")]
    public async Task CapturedTerminalContextRejectsOldAuthorStateForEveryDocumentEdit(string input)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Guard"),assets,native,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Base"));
            var document=new CadDocumentViewModel(session,native,new CadMessageLog());
            try
            {
                var context=new CadCommandContext(document);await session.ExecuteAsync(DocumentEdits.SetGrid(session.Snapshot.Settings.Grid with {SpacingMm=12}));
                var before=session.Snapshot;var usage=session.HistoryUsage;int count=assets.Count;
                var result=await new CadCommandLineService().ExecuteAsync(input,context);
                Assert.False(result.Success);Assert.False(result.IsCancelled);Assert.Same(before,session.Snapshot);
                Assert.Equal(usage,session.HistoryUsage);Assert.Equal(count,assets.Count);
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory]
    [InlineData("missing")][InlineData("foreign-document")][InlineData("foreign-state")]
    [InlineData("empty-state")][InlineData("number-state")][InlineData("extra")][InlineData("duplicate")]
    public async Task AgentCommandRejectsMissingForeignOrUnexpectedIdentityArguments(string mutation)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Arguments"),assets,native,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,native,new CadMessageLog());
        try
        {
            var before=session.Snapshot;var json=JsonNode.Parse(Arguments(before,"BOX New 2 3 4"))!;
            if(mutation=="missing")json.AsObject().Remove("stateId");
            if(mutation=="foreign-document")json["documentId"]=Guid.NewGuid().ToString();
            if(mutation=="foreign-state")json["stateId"]=Guid.NewGuid().ToString();
            if(mutation=="empty-state")json["stateId"]=Guid.Empty.ToString();
            if(mutation=="number-state")json["stateId"]=123;
            if(mutation=="extra")json["override"]=true;
            string input=json.ToJsonString();if(mutation=="duplicate")input=input[..^1]+",\"command\":\"UNDO\"}";
            string result=await new CadAgentToolset(new CadCommandLineService(),document).ExecuteAsync(new("1","cad_command",input),default);
            Assert.False(Success(result));Assert.Same(before,session.Snapshot);Assert.False(session.CanUndo);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TerminalAndAgentCancelLateNativeResultWithoutCommittingOrLeaking(bool agent)
    {
        var assets=new MemoryAssetStore();using var kernel=new GatedKernel(new OcctGeometryKernel());
        await kernel.PrepareAsync(new BoxRecipe(2,3,4,RigidTransform3d.Identity),assets);
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Cancel"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());using var cancel=new CancellationTokenSource();
        var before=session.Snapshot;var service=new CadCommandLineService();
        try
        {
            var tools=new CadAgentToolset(service,document);
            Task<string>? toolTask=agent?tools.ExecuteAsync(new("1","cad_command",Arguments(before,"BOX New 2 3 4")),cancel.Token):null;
            Task<CadCommandResult>? terminalTask=agent?null:service.ExecuteAsync("BOX New 2 3 4",new CadCommandContext(document),cancel.Token);
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));Assert.True(session.IsBusy);Assert.Equal(1,assets.Count);
            cancel.Cancel();kernel.Release.TrySetResult();
            if(agent)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>toolTask!);
            else{var result=await terminalTask!;Assert.False(result.Success);Assert.True(result.IsCancelled);}
            Assert.Same(before,session.Snapshot);Assert.False(session.CanUndo);Assert.False(session.IsBusy);Assert.Equal(0,assets.Count);
        }
        finally{kernel.Release.TrySetResult();document.Detach();}
    }

    [Fact]
    public async Task AgentKeepsActualCommittedSuccessWhenCancellationArrivesFromCommitNotification()
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Committed"),assets,native,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,native,new CadMessageLog());using var cancel=new CancellationTokenSource();
        session.CommandCommitted+=(_,_)=>cancel.Cancel();
        try
        {
            var result=await new CadAgentToolset(new CadCommandLineService(),document).ExecuteAsync(
                new("1","cad_command",Arguments(session.Snapshot,"BOX New 2 3 4")),cancel.Token);
            Assert.True(cancel.IsCancellationRequested);Assert.True(Success(result));Assert.Equal(24,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,6);
            using var parsed=JsonDocument.Parse(result);Assert.Equal(session.Snapshot.StateId.ToString(),parsed.RootElement.GetProperty("stateId").GetString());
            Assert.True(session.CanUndo);Assert.False(session.IsBusy);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TerminalRelayCancelAndDocumentSwitchCancelTheCapturedInvocation(bool switched)
    {
        var assets=new MemoryAssetStore();using var kernel=new GatedKernel(new OcctGeometryKernel());
        await kernel.PrepareAsync(new BoxRecipe(2,3,4,RigidTransform3d.Identity),assets);
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Relay cancel"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        var terminal=new CommandLineToolboxViewModel(new CadCommandLineService(),new Icons(),new CadMessageLog());
        var before=session.Snapshot;
        try
        {
            terminal.Bind(document);terminal.CommandText="BOX New 2 3 4";var pending=terminal.ExecuteCommand.ExecuteAsync(null);
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));Assert.True(terminal.IsRunning);
            Assert.True(terminal.ExecuteCancelCommand.CanExecute(null));
            if(switched)terminal.Bind(null);else terminal.ExecuteCancelCommand.Execute(null);
            Assert.True(terminal.ExecuteCommand.IsCancellationRequested);kernel.Release.TrySetResult();await pending;
            Assert.False(terminal.IsRunning);Assert.False(terminal.ExecuteCancelCommand.CanExecute(null));
            Assert.Contains(terminal.Entries,e=>e.Kind=="Error"&&e.Text=="Command cancelled.");
            Assert.Same(before,session.Snapshot);Assert.False(session.CanUndo);Assert.Equal(0,assets.Count);
        }
        finally{kernel.Release.TrySetResult();terminal.Bind(null);document.Detach();}
    }

    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task QueuedUndoOrRedoRechecksCancellationAndExpectedStateInsideTheSession(bool redo,bool cancelled)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();var dispatcher=new GatedDispatcher();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Queued history"),assets,native,dispatcher))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Base"));
            if(redo)await session.UndoAsync();
            var expected=DocumentCommandExpectation.Capture(session.Snapshot);using var cancel=new CancellationTokenSource();
            dispatcher.Pause=true;var pending=redo?session.RedoAsync(cancel.Token,expected):session.UndoAsync(cancel.Token,expected);
            await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));dispatcher.Pause=false;
            if(cancelled)cancel.Cancel();else await session.ExecuteAsync(DocumentEdits.SetGrid(session.Snapshot.Settings.Grid with {SpacingMm=17}));
            var before=session.Snapshot;var usage=session.HistoryUsage;int count=assets.Count;dispatcher.Release.TrySetResult();
            if(cancelled)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            else await Assert.ThrowsAsync<StaleDocumentException>(()=>pending);
            Assert.Same(before,session.Snapshot);Assert.Equal(usage,session.HistoryUsage);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SessionRejectsForeignExpectationOrPrecancelBeforePreparing(bool cancelled)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Before prepare"),assets,native,new InlineSessionDispatcher());
        using var cancel=new CancellationTokenSource();if(cancelled)cancel.Cancel();
        var command=new ObservedCommand();var before=session.Snapshot;
        var expectation=new DocumentCommandExpectation(DocumentId.New(),before.StateId);
        if(cancelled)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(command,cancel.Token,expectation));
        else await Assert.ThrowsAsync<StaleDocumentException>(()=>session.ExecuteAsync(command,cancel.Token,expectation));
        Assert.False(command.Prepared);Assert.Same(before,session.Snapshot);Assert.False(session.IsBusy);Assert.Equal(0,assets.Count);
    }

    private static string Arguments(DocumentSnapshot doc,string command)=>JsonSerializer.Serialize(new {command,documentId=doc.Id.ToString(),stateId=doc.StateId.ToString()});
    private static bool Success(string json){using var parsed=JsonDocument.Parse(json);return parsed.RootElement.GetProperty("success").GetBoolean();}
    private sealed class Icons:IToolboxIconProvider
    {public object ModelTree=>"";public object Properties=>"";public object Modeling=>"";public object Messages=>"";}
    private sealed class ObservedCommand:ICadDocumentCommand
    {
        public bool Prepared {get;private set;}public string Name=>"Observed";
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {Prepared=true;return Task.FromResult(new PreparedDocumentEdit(context.Snapshot));}
    }
    private sealed class GatedDispatcher:ISessionDispatcher
    {
        public bool Pause {get;set;}
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InvokeAsync(Action action){if(Pause){Entered.TrySetResult();await Release.Task;}action();}
    }
    private sealed class GatedKernel(OcctGeometryKernel native):IGeometryKernel,IDisposable
    {
        private GeometryResult? prepared;
        private GeometryRecipe? expectedRecipe;
        private IAssetStore? expectedAssets;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task PrepareAsync(GeometryRecipe recipe,IAssetStore assets)
        {
            // This fixture tests cancellation after native completion. Prepare
            // that exact geometry before timing entry to its artificial barrier,
            // so another test's global native queue cannot consume the 20s gate.
            Assert.Null(prepared);expectedRecipe=recipe;expectedAssets=assets;
            prepared=await native.EvaluateAsync(recipe,assets,CancellationToken.None);
        }
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            Assert.Equal(expectedRecipe,recipe);Assert.Same(expectedAssets,assets);
            var result=Interlocked.Exchange(ref prepared,null)??throw new InvalidOperationException("The late-result fixture was not prepared.");
            Entered.TrySetResult();await Release.Task;return result; // A completed native operation may ignore cancellation until it returns.
        }
        public void Dispose(){Release.TrySetResult();Interlocked.Exchange(ref prepared,null)?.Dispose();}
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
    }
}
