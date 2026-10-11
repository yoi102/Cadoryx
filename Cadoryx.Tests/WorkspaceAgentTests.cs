using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class WorkspaceAgentTests
{
    [Fact] public async Task EmptyWorkspaceCreatesAndSameToolsetModelsTheNewActiveDocument()
    {
        await using var f = new Fixture();
        var empty = await f.List(); Assert.Empty(empty["sessions"]!.AsArray());
        Assert.Null(empty["expected"]!["activeSessionId"]); Assert.Null(empty["expected"]!["documentId"]);
        var result = await f.Tool(f.Request("new", ("name", JsonValue.Create("New part")))); Success(result);
        Assert.Equal(f.Active!.Session.SessionId.ToString(), result["active"]!["sessionId"]!.GetValue<string>());
        Assert.False(result["changed"]!.GetValue<bool>()); Assert.True(result["workspaceChanged"]!.GetValue<bool>());
        var status = await f.Call("cad_status", "{}"); Assert.Equal("New part", status["name"]!.GetValue<string>());
        var edited = await f.Call("cad_command", JsonSerializer.Serialize(new { command = "BOX Tool 2 3 4", documentId = f.Active.Session.Snapshot.Id.ToString(), stateId = f.Active.Session.Snapshot.StateId.ToString() }));
        Success(edited); Assert.Single(f.Active.Session.Snapshot.Bodies);
        Assert.False((await f.Tool(emptyRequest(empty, "new", "name", "Stale")))["success"]!.GetValue<bool>());
    }

    [Fact] public async Task ActiveEditsAndSwitchAwayBackRejectStaleWorkspaceExpectation()
    {
        await using var f = new Fixture(); var first = f.Add("First"); var second = f.Add("Second"); f.SetActive(first);
        var before = f.Request("new", ("name", JsonValue.Create("Late")));
        await first.Session.ExecuteAsync(new EditDocumentCommand("rename", s => s with { Name = "Changed" }));
        Assert.False((await f.Tool(before))["success"]!.GetValue<bool>());
        before = f.Request("new", ("name", JsonValue.Create("Late")));
        f.SetActive(second); f.SetActive(first);
        Assert.False((await f.Tool(before))["success"]!.GetValue<bool>()); Assert.Equal(2, f.Documents.Count);
    }

    [Fact] public async Task ActivateRequiresExactSessionAndTargetStatePreservesUndo()
    {
        await using var f = new Fixture(); var first = f.Add("Same"); var second = f.Add("Same");
        await first.Session.ExecuteAsync(new EditDocumentCommand("rename", s => s with { Name = "Same again" }));
        var original = first.Session.Snapshot; var target = Target(first);
        Success(await f.Tool(f.Request("activate", ("target", target)))); Assert.Same(first, f.Active);
        Assert.Same(original, first.Session.Snapshot); Assert.True(first.Session.CanUndo);
        Success(await f.Tool(f.Request("activate", ("target", Target(first)))));
        f.SetActive(second); await first.Session.UndoAsync();
        Assert.False((await f.Tool(f.Request("activate", ("target", target))))["success"]!.GetValue<bool>());
        target = Target(first); target["sessionId"] = Guid.NewGuid().ToString();
        Assert.False((await f.Tool(f.Request("activate", ("target", target))))["success"]!.GetValue<bool>());
        first.IsClosingRequested = true;
        Assert.False((await f.Tool(f.Request("activate", ("target", Target(first)))))["success"]!.GetValue<bool>()); Assert.Same(second, f.Active);
    }

    [Fact] public async Task NativeOpenUsesRealStorageThenReopenPathActivatesWithoutAnotherSession()
    {
        await using var f = new Fixture(); var path = await f.NativeFile();
        Success(await f.Tool(f.Request("open", ("path", JsonValue.Create(path)))));
        var opened = f.Active!; Assert.Single(opened.Session.Snapshot.Bodies); Assert.False(opened.Session.IsDirty); Assert.Equal(path, opened.Session.FilePath);
        var firstState = opened.Session.Snapshot.StateId;
        f.Add("Other"); Success(await f.Tool(f.Request("open", ("path", JsonValue.Create(path)))));
        Assert.Same(opened, f.Active); Assert.Equal(2, f.Documents.Count); Assert.Equal(firstState, opened.Session.Snapshot.StateId);
        var copy = f.PathFor("copy.cadoryx"); File.Copy(path, copy);
        Assert.False((await f.Tool(f.Request("open", ("path", JsonValue.Create(copy)))))["success"]!.GetValue<bool>());
        Assert.Equal(2, f.Documents.Count);
    }

    [Fact] public async Task ExchangeOpenUsesRealImporterAndKeepsNativeSavePathUnset()
    {
        await using var f = new Fixture(); var path = await f.NativeFile();
        using (var loaded = await f.Storage.LoadAsync(path, f.Assets)) await f.Kernel.ExportAsync(loaded.Snapshot, f.Assets, f.PathFor("model.step"));
        Success(await f.Tool(f.Request("open", ("path", JsonValue.Create(f.PathFor("model.step"))))));
        Assert.NotEmpty(f.Active!.Session.Snapshot.Bodies); Assert.Null(f.Active.Session.FilePath); Assert.True(f.Active.Session.IsDirty);
    }

    [Theory] [InlineData("cancel")] [InlineData("edit")] [InlineData("switch")] [InlineData("busy")] [InlineData("close")]
    public async Task LateNativeReadNeverAttachesAfterInvalidationAndReleasesAllLeases(string boundary)
    {
        await using var f = new Fixture(true); var path = await f.NativeFile(); var current = f.Add("Active");
        using var cancellation = new CancellationTokenSource(); var count = f.Documents.Count;
        var pending = f.Tool(f.Request("open", ("path", JsonValue.Create(path))), cancellation.Token);
        await f.Holding!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(f.Assets.Count > 0);
        if (boundary == "cancel") cancellation.Cancel();
        if (boundary == "edit") await current.Session.ExecuteAsync(new EditDocumentCommand("rename", s => s with { Name = "Edited" }));
        if (boundary == "switch") { f.SetActive(null); f.SetActive(current); }
        if (boundary == "busy") f.Available = false;
        if (boundary == "close") current.IsClosingRequested = true;
        f.Holding.Release.TrySetResult(true);
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.False((await pending)["success"]!.GetValue<bool>());
        Assert.Equal(count, f.Documents.Count); Assert.Equal(count, f.Workspace.Documents.Count); Assert.Equal(0, f.Assets.Count);
    }

    [Fact] public async Task PendingReadRejectsConcurrentAuthorAndFailedDockAttachmentReleasesCandidate()
    {
        await using var f = new Fixture(true); var path = await f.NativeFile();
        var pending = f.Tool(f.Request("open", ("path", JsonValue.Create(path)))); await f.Holding!.Started.Task;
        Assert.False((await f.Tool(f.Request("new", ("name", JsonValue.Create("Concurrent")))))["success"]!.GetValue<bool>());
        f.FailAttachment = true; f.Holding.Release.TrySetResult(true); Assert.False((await pending)["success"]!.GetValue<bool>());
        Assert.Empty(f.Documents); Assert.Empty(f.Workspace.Documents); Assert.Equal(0, f.Assets.Count);
        f.FailAttachment = false; Success(await f.Tool(f.Request("new", ("name", JsonValue.Create("Recovered")))));
    }

    [Fact] public async Task TerminalFromEmptyWorkspaceKeepsOwnCommandAliveAndNextCommandUsesNewDocument()
    {
        await using var f = new Fixture(); var terminal = new CommandLineToolboxViewModel(f.Commands, new Icons(), f.Log);
        terminal.BindWorkspace(f.Author); f.Changed = d => terminal.Bind(d);
        terminal.CommandText = "WORKSPACE {\"action\":\"new\",\"name\":\"Terminal part\"}";
        await terminal.ExecuteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(terminal.Entries, e => e.Kind == "Error"); Assert.Equal("Terminal part", f.Active!.Session.Snapshot.Name);
        Assert.False(terminal.ExecuteCommand.IsCancellationRequested);
        terminal.CommandText = "BOX Next 2 3 4"; await terminal.ExecuteCommand.ExecuteAsync(null); Assert.Single(f.Active.Session.Snapshot.Bodies);
        Assert.Contains(f.Commands.Commands, d => d.Name == "WORKSPACE");
        var list = await f.Commands.ExecuteAsync("WS LIST", null, new CadWorkspaceCommandContext(f.Author)); Assert.True(list.Success);
        Assert.False((await f.Commands.ExecuteAsync("WORKSPACE LIST", null)).Success);
    }

    [Fact] public async Task AiToolSwitchKeepsConversationAndManualSwitchCancelsAndResets()
    {
        await using var f = new Fixture(); var codex = new Codex(); var runner = new Runner();
        using var assistant = new AiAssistantToolboxViewModel(new Settings(), runner, new Chat(), codex, f.Commands, new Icons());
        assistant.BindWorkspace(f.Author); f.Changed = d => assistant.Bind(d);
        runner.Run = async (request, token) =>
        {
            var list = JsonNode.Parse(await request.Toolset!.ExecuteAsync(new("list", "cad_workspace", "{\"action\":\"list\"}"), token))!.AsObject();
            var created = JsonNode.Parse(await request.Toolset.ExecuteAsync(new("new", "cad_workspace", emptyRequest(list, "new", "name", "AI part").ToJsonString()), token))!.AsObject();
            Success(created); Assert.False(token.IsCancellationRequested);
            var active = created["active"]!;
            var command = JsonSerializer.Serialize(new { command = "BOX AI 2 3 4", documentId = active["documentId"]!.GetValue<string>(), stateId = active["stateId"]!.GetValue<string>() });
            Success(JsonNode.Parse(await request.Toolset.ExecuteAsync(new("box", "cad_command", command), token))!.AsObject());
        };
        assistant.UserInput = "Create a document then a box"; await assistant.SendCommand.ExecuteAsync(null);
        Assert.Single(f.Active!.Session.Snapshot.Bodies); Assert.Equal(0, codex.Resets);
        Assert.Contains(assistant.Messages, m => m.Role == "User"); Assert.Equal("Ready", assistant.ConnectionStatus);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Run = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        assistant.UserInput = "Wait"; var pending = assistant.SendCommand.ExecuteAsync(null); await started.Task;
        f.SetActive(null); await pending; Assert.Equal(1, codex.Resets);
        Assert.Single(assistant.Messages); Assert.Equal("No active document.", assistant.Messages[0].Content);
        Assert.Equal("Ready", assistant.ConnectionStatus);
    }

    [Theory]
    [InlineData("{\"action\":\"list\",\"name\":\"forbidden\"}")]
    [InlineData("{\"action\":\"contract\",\"extra\":true}")]
    [InlineData("{\"action\":\"new\",\"name\":\"No identity\"}")]
    [InlineData("{\"action\":\"list\",\"limit\":51}")]
    [InlineData("{\"action\":\"list\",\"offset\":-1}")]
    [InlineData("{\"action\":\"list\",\"action\":\"list\"}")]
    [InlineData("{\"action\":\"close\"}")]
    public async Task MalformedOrUnimplementedWorkspaceActionsDoNotChangeSessions(string json)
    { await using var f = new Fixture(); Assert.False((await f.Call("cad_workspace", json))["success"]!.GetValue<bool>()); Assert.Empty(f.Documents); }

    [Fact] public async Task InvalidPathsPartialNullIdentityAndUnknownTargetsAreRejected()
    {
        await using var f = new Fixture();
        foreach (var path in new[] { "relative.cadoryx", f.PathFor("missing.cadoryx") })
            Assert.False((await f.Tool(f.Request("open", ("path", JsonValue.Create(path)))))["success"]!.GetValue<bool>());
        var partial = f.Request("new", ("name", JsonValue.Create("Partial"))); partial["expected"]!["documentId"] = Guid.NewGuid().ToString();
        Assert.False((await f.Tool(partial))["success"]!.GetValue<bool>());
        Assert.False((await f.Call("cad_command", "{\"command\":\"WS {\\\"action\\\":\\\"new\\\",\\\"name\\\":\\\"Bypass\\\"}\"}"))["success"]!.GetValue<bool>());
        Assert.Empty(f.Documents);
    }

    [Fact] public async Task WorkspacePagingAndProviderSchemaExposeExactIdentities()
    {
        await using var f = new Fixture(); for (int i = 0; i < 25; i++) f.Add("Page " + i);
        var result = await f.Call("cad_workspace", "{\"action\":\"list\",\"offset\":20,\"limit\":5}");
        Assert.Equal(25, result["total"]!.GetValue<int>()); Assert.Equal(5, result["sessions"]!.AsArray().Count);
        var schema = ToolSchemaPresentation.Create(f.Tools.ToolDefinitions.Single(t => t.Name == "cad_workspace").Parameters);
        Assert.True(schema.GetProperty("properties").GetProperty("expected").GetProperty("properties").TryGetProperty("activeSessionId", out _));
        Assert.True(schema.GetProperty("properties").GetProperty("target").GetProperty("properties").TryGetProperty("sessionId", out _));
        Success(await f.Call("cad_workspace", "{\"action\":\"contract\"}"));
    }

    private static JsonObject emptyRequest(JsonObject list, string action, string field, string value) => new()
    { ["action"] = action, ["expected"] = list["expected"]!.DeepClone(), [field] = value };
    private static JsonObject Target(CadDocumentViewModel doc) => new() { ["sessionId"] = doc.Session.SessionId.ToString(), ["documentId"] = doc.Session.Snapshot.Id.ToString(), ["stateId"] = doc.Session.Snapshot.StateId.ToString() };
    private static void Success(JsonObject value) => Assert.True(value["success"]!.GetValue<bool>(), value.ToJsonString());

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new(); internal OcctGeometryKernel Kernel { get; } = new(); internal CadDocumentStorage Storage { get; } = new();
        internal CadWorkspace Workspace { get; } internal ObservableCollection<CadDocumentViewModel> Documents { get; } = [];
        internal CadDocumentViewModel? Active; internal CadWorkspaceAuthoring Author { get; } internal CadAgentToolset Tools { get; }
        internal CadCommandLineService Commands { get; } = new(); internal CadMessageLog Log { get; } = new(); internal HoldingStorage? Holding;
        internal bool Available = true, FailAttachment; internal Action<CadDocumentViewModel?>? Changed;
        private string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cadoryx-workspace-tests-" + Guid.NewGuid().ToString("N"));
        internal Fixture(bool hold = false)
        {
            System.IO.Directory.CreateDirectory(Directory); Workspace = new(Kernel, Assets, new InlineSessionDispatcher());
            Holding = hold ? new(Storage) : null;
            Author = new(Workspace, Documents, () => Active, Attach, SetActive, () => Available, Assets, (IDocumentStorage?)Holding ?? Storage, new Importer(Kernel));
            Tools = new(Commands, null, Author);
        }
        internal CadDocumentViewModel Add(string name) => Attach(Workspace.Create(name));
        private CadDocumentViewModel Attach(CadDocumentSession session)
        {
            if (FailAttachment) throw new InvalidOperationException("Dock failed");
            var doc = new CadDocumentViewModel(session, Kernel, Log, Storage); Documents.Add(doc); SetActive(doc); return doc;
        }
        internal void SetActive(CadDocumentViewModel? doc) { if (ReferenceEquals(Active, doc)) return; Active = doc; Author.NotifyChanged(); Changed?.Invoke(doc); }
        internal string PathFor(string name) => System.IO.Path.Combine(Directory, name);
        internal async Task<string> NativeFile()
        {
            await using var source = new CadDocumentSession(DocumentSnapshot.Create("Native source"), Assets, Kernel, new InlineSessionDispatcher());
            await source.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), "Native Box"));
            var path = PathFor("source.cadoryx"); await source.SaveAsync(Storage, path); return path;
        }
        internal JsonObject Request(string action, params (string, JsonNode?)[] fields)
        {
            var identity = Author.Capture(); var value = new JsonObject { ["action"] = action,
                ["expected"] = new JsonObject { ["workspaceStateId"] = identity.WorkspaceStateId.ToString(), ["activeSessionId"] = identity.ActiveSessionId?.ToString(),
                    ["documentId"] = identity.DocumentId?.ToString(), ["stateId"] = identity.StateId?.ToString() } };
            foreach (var (name, item) in fields) value[name] = item?.DeepClone(); return value;
        }
        internal Task<JsonObject> List() => Call("cad_workspace", "{\"action\":\"list\"}");
        internal Task<JsonObject> Tool(JsonObject request, CancellationToken token = default) => Call("cad_workspace", request.ToJsonString(), token);
        internal async Task<JsonObject> Call(string name, string json, CancellationToken token = default) => JsonNode.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(), name, json), token))!.AsObject();
        public async ValueTask DisposeAsync()
        {
            foreach (var doc in Documents) doc.Detach(); await Workspace.DisposeAsync(); Assert.Equal(0, Assets.Count); System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class HoldingStorage(IDocumentStorage inner) : IDocumentStorage
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LoadedDocument> LoadAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default)
        { var loaded = await inner.LoadAsync(path, assets, cancellationToken); Started.SetResult(); await Release.Task; return loaded; }
        public Task SaveAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => inner.SaveAsync(snapshot, assets, path, cancellationToken);
        public Task<DocumentSettings> ReadSettingsAsync(string path, CancellationToken cancellationToken = default) => inner.ReadSettingsAsync(path, cancellationToken);
    }
    private sealed class Importer(IGeometryKernel kernel) : IExchangeImportService
    { public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => kernel.ImportAsync(path, assets, cancellationToken); }
    private sealed class Icons : IToolboxIconProvider
    { public object ModelTree { get; } = new(); public object Properties { get; } = new(); public object Modeling { get; } = new(); public object Messages { get; } = new(); }
    private sealed class Settings : IAiAssistantSettingsStore
    { public AiAssistantSettings Load() => new() { Model = "test" }; public void Save(AiAssistantSettings settings) { } }
    private sealed class Chat : IAiChatClient
    {
        public Task<IReadOnlyList<string>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Runner : IAgentRunner
    {
        internal Func<AgentRunRequest, CancellationToken, Task> Run = (_, _) => Task.CompletedTask;
        public async Task<AgentRunResult> RunAsync(AgentRunRequest request, Func<AgentRunEvent, ValueTask>? reportEvent = null, CancellationToken cancellationToken = default)
        { await Run(request, cancellationToken); return new("test", request.ContextWindowTokens, false); }
    }
    private sealed class Codex : ICodexAgentClient
    {
        internal int Resets;
        public Task ResetConversationAsync(CancellationToken cancellationToken = default) { Resets++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetModelsAsync(CodexAgentOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<CodexAgentRunResult> RunAsync(CodexAgentRunRequest request, Func<AgentRunEvent, ValueTask>? reportEvent = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
