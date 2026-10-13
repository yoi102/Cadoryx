using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class WorkspaceIdentityFeedbackTests
{
    [Theory]
    [InlineData("new", "missing")]
    [InlineData("new", "null")]
    [InlineData("new", "string")]
    [InlineData("open", "missing")]
    [InlineData("open", "null")]
    [InlineData("open", "string")]
    [InlineData("activate", "missing")]
    [InlineData("activate", "null")]
    [InlineData("activate", "string")]
    public async Task MissingExpectedContainerProvidesReadOnlyListAndNeverAttemptsTransitionOrFileRead(string action, string fault)
    {
        await using var f = new Fixture(); var first = f.Add("First"); f.Add("Second");
        var request = f.Request(action);
        if (action == "new") request["name"] = "Not created";
        if (action == "open") request["path"] = await f.InputFile();
        if (action == "activate") request["target"] = f.Target(first);
        if (fault == "missing") request.Remove("expected"); else request["expected"] = fault == "null" ? null : JsonValue.Create("wrong-role");
        await f.Reject(request.ToJsonString(), "expected");
    }

    [Theory]
    [InlineData("workspaceStateId", "missing")]
    [InlineData("workspaceStateId", "null")]
    [InlineData("workspaceStateId", "empty")]
    [InlineData("workspaceStateId", "malformed")]
    [InlineData("activeSessionId", "missing")]
    [InlineData("activeSessionId", "empty")]
    [InlineData("activeSessionId", "numeric")]
    [InlineData("documentId", "missing")]
    [InlineData("documentId", "malformed")]
    [InlineData("stateId", "missing")]
    [InlineData("stateId", "empty")]
    [InlineData("documentId", "mixed-null")]
    [InlineData("extra", "unknown")]
    [InlineData("workspaceStateId", "duplicate")]
    public async Task ExpectedRoleReportsExactNestedPathsWithoutSupplyingReplacementIdentities(string field, string fault)
    {
        await using var f = new Fixture(); f.Add("Existing");
        var request = f.Request("new"); request["name"] = "Not created"; var expected = request["expected"]!.AsObject();
        if (fault == "missing") expected.Remove(field);
        else if (fault is "null" or "mixed-null") expected[field] = null;
        else if (fault == "empty") expected[field] = Guid.Empty.ToString();
        else if (fault == "malformed") expected[field] = "not-a-guid";
        else if (fault == "numeric") expected[field] = 42;
        else if (fault == "unknown") expected[field] = "not an allowed field";
        var text = request.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"expected\":{", "\"expected\":{\"workspaceStateId\":\"" + f.Author.Capture().WorkspaceStateId + "\",", StringComparison.Ordinal);
        await f.Reject(text, "expected." + field);
    }

    [Theory]
    [InlineData("sessionId", "missing")]
    [InlineData("sessionId", "null")]
    [InlineData("sessionId", "empty")]
    [InlineData("documentId", "missing")]
    [InlineData("documentId", "null")]
    [InlineData("documentId", "empty")]
    [InlineData("stateId", "missing")]
    [InlineData("stateId", "null")]
    [InlineData("stateId", "numeric")]
    public async Task ActivationRequiresAllThreeTargetIdsFromOneActualListedSession(string field, string fault)
    {
        await using var f = new Fixture(); var first = f.Add("Same name"); f.Add("Same name");
        var request = f.Request("activate"); var target = f.Target(first); request["target"] = target;
        if (fault == "missing") target.Remove(field);
        else if (fault == "null") target[field] = null;
        else if (fault == "empty") target[field] = Guid.Empty.ToString();
        else target[field] = 123;
        await f.Reject(request.ToJsonString(), "target." + field);
    }

    [Theory]
    [InlineData("list", "path")]
    [InlineData("list", "expected")]
    [InlineData("new", "path")]
    [InlineData("activate", "name")]
    public async Task FieldsFromAnotherActionReturnTheAllowedFieldsAndDoNotExecuteTheOriginalRequest(string action, string wrongField)
    {
        await using var f = new Fixture(); var first = f.Add("First"); f.Add("Second");
        var request = action == "list" ? new JsonObject { ["action"] = action } : f.Request(action);
        if (action == "new") request["name"] = "Not created";
        if (action == "activate") request["target"] = f.Target(first);
        request[wrongField] = wrongField == "expected" ? new JsonObject() : JsonValue.Create("Wrong action role");
        await f.Reject(request.ToJsonString(), wrongField);
    }

    [Theory]
    [InlineData("expected-workspace")]
    [InlineData("expected-state")]
    [InlineData("target-session")]
    [InlineData("target-document")]
    [InlineData("target-state")]
    public async Task WellFormedButForeignOrStaleIdsRetainStrictExecutionGuardsWithoutCallingThemMissing(string fault)
    {
        await using var f = new Fixture(); var first = f.Add("First"); f.Add("Second");
        var request = f.Request("activate"); request["target"] = f.Target(first);
        var container = fault.StartsWith("expected-", StringComparison.Ordinal) ? request["expected"]!.AsObject() : request["target"]!.AsObject();
        var field = fault switch { "expected-workspace" => "workspaceStateId", "expected-state" or "target-state" => "stateId", "target-session" => "sessionId", _ => "documentId" };
        container[field] = Guid.NewGuid().ToString(); var text = request.ToJsonString(); var expected = f.Author.Capture(); var active = f.Active;
        var result = await f.Call(text); Assert.False(result["success"]!.GetValue<bool>());
        Assert.False(result.ContainsKey("invalidFields")); Assert.False(result.ContainsKey("identitySource"));
        Assert.Equal(expected, f.Author.Capture()); Assert.Same(active, f.Active); Assert.Equal(2, f.Documents.Count);
        Assert.Equal(0, f.Storage.Reads); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(text, request.ToJsonString());
    }

    [Fact]
    public async Task ExplicitCallerReadsListAndRetriesCreateOpenAndActivateUsingReturnedRoles()
    {
        await using var f = new Fixture();
        var rejected = await f.Call("{\"action\":\"new\",\"name\":\"Same name\"}");
        Assert.False(rejected["success"]!.GetValue<bool>()); Assert.Empty(f.Documents);
        var source = rejected["identitySource"]!; Assert.Equal("cad_workspace", source["toolName"]!.GetValue<string>());
        var list = await f.Call(source["arguments"]!.ToJsonString());
        var request = new JsonObject { ["action"] = "new", ["name"] = "Same name", ["expected"] = list["expected"]!.DeepClone() };
        Assert.Null(request["expected"]!["activeSessionId"]); Assert.Null(request["expected"]!["documentId"]); Assert.Null(request["expected"]!["stateId"]);
        var created = await f.Call(request.ToJsonString()); Assert.True(created["success"]!.GetValue<bool>()); var first = f.Active!;
        var path = await f.InputFile(); list = await f.Call("{\"action\":\"list\"}");
        var opened = await f.Call(new JsonObject { ["action"] = "open", ["path"] = path, ["expected"] = list["expected"]!.DeepClone() }.ToJsonString());
        Assert.True(opened["success"]!.GetValue<bool>()); Assert.Equal(1, f.Storage.Reads); Assert.Equal(2, f.Documents.Count); Assert.NotSame(first, f.Active);
        list = await f.Call("{\"action\":\"list\"}");
        var row = Assert.Single(list["sessions"]!.AsArray(), r => r!["identity"]!["sessionId"]!.GetValue<string>() == first.Session.SessionId.ToString());
        var identity = row!["identity"]!;
        var target = new JsonObject { ["sessionId"] = identity["sessionId"]!.DeepClone(), ["documentId"] = identity["documentId"]!.DeepClone(), ["stateId"] = identity["stateId"]!.DeepClone() };
        var original = first.Session.Snapshot;
        var activated = await f.Call(new JsonObject { ["action"] = "activate", ["expected"] = list["expected"]!.DeepClone(), ["target"] = target }.ToJsonString());
        Assert.True(activated["success"]!.GetValue<bool>()); Assert.Same(first, f.Active); Assert.Same(original, first.Session.Snapshot);
        Assert.False(first.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    [Fact]
    public async Task PreCancelledWorkspaceRequestDoesNotEvenParseOrReadFeedbackSource()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Call("not JSON", cancel.Token));
        Assert.Empty(f.Documents); Assert.Equal(0, f.Storage.Reads); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new(); internal NoGeometryKernel Kernel { get; } = new();
        internal CountingStorage Storage { get; } = new(); internal ObservableCollection<CadDocumentViewModel> Documents { get; } = [];
        internal CadWorkspace Workspace { get; } internal CadWorkspaceAuthoring Author { get; } internal CadDocumentViewModel? Active;
        private TestFiles Files { get; } = new(); private CadAgentToolset Tools { get; }
        internal Fixture()
        {
            Workspace = new(Kernel, Assets, new InlineSessionDispatcher());
            Author = new(Workspace, Documents, () => Active, Attach, SetActive, () => true, Assets, Storage, new NoImporter());
            Tools = new(new CadCommandLineService(), null, Author);
        }
        private CadDocumentViewModel Attach(CadDocumentSession session)
        { var document = new CadDocumentViewModel(session, Kernel, new CadMessageLog(), Storage); Documents.Add(document); SetActive(document); return document; }
        private void SetActive(CadDocumentViewModel document) { Active = document; Author.NotifyChanged(); }
        internal CadDocumentViewModel Add(string name) => Attach(Workspace.Create(name));
        internal JsonObject Request(string action)
        {
            var expected = Author.Capture(); return new() { ["action"] = action, ["expected"] = new JsonObject
                { ["workspaceStateId"] = expected.WorkspaceStateId.ToString(), ["activeSessionId"] = expected.ActiveSessionId?.ToString(),
                    ["documentId"] = expected.DocumentId?.ToString(), ["stateId"] = expected.StateId?.ToString() } };
        }
        internal JsonObject Target(CadDocumentViewModel document) => new()
        { ["sessionId"] = document.Session.SessionId.ToString(), ["documentId"] = document.Session.Snapshot.Id.ToString(), ["stateId"] = document.Session.Snapshot.StateId.ToString() };
        internal async Task<string> InputFile()
        { var path = Files.PathFor("source-" + Guid.NewGuid().ToString("N") + ".cadoryx"); await Storage.SaveAsync(DocumentSnapshot.Create("Same name"), Assets, path); return path; }
        internal async Task<JsonObject> Call(string request, CancellationToken token = default) =>
            JsonNode.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(), "cad_workspace", request), token))!.AsObject();
        internal async Task Reject(string text, string invalidField)
        {
            var before = Author.Capture(); var active = Active; var snapshots = Documents.Select(d => d.Session.Snapshot).ToArray(); var reads = Storage.Reads;
            var result = await Call(text); Assert.False(result["success"]!.GetValue<bool>());
            Assert.Contains(result["invalidFields"]!.AsArray(), n => n!.GetValue<string>() == invalidField);
            Assert.Equal("cad_workspace", result["identitySource"]!["toolName"]!.GetValue<string>());
            Assert.Equal("list", result["identitySource"]!["arguments"]!["action"]!.GetValue<string>());
            Assert.Single(result["identitySource"]!["arguments"]!.AsObject()); Assert.NotNull(result["allowedFields"]); Assert.NotNull(result["roles"]);
            Assert.False(result.ContainsKey("expected")); Assert.False(result.ContainsKey("target"));
            Assert.Equal(before, Author.Capture()); Assert.Same(active, Active); Assert.Equal(snapshots.Length, Documents.Count);
            for (int i = 0; i < snapshots.Length; i++) Assert.Same(snapshots[i], Documents[i].Session.Snapshot);
            Assert.Equal(reads, Storage.Reads); Assert.Equal(0, Kernel.Calls); Assert.Equal(0, Assets.Count);
        }
        public async ValueTask DisposeAsync()
        { foreach (var document in Documents) document.Detach(); await Workspace.DisposeAsync(); Assert.Equal(0, Assets.Count); Files.Dispose(); }
    }
    private sealed class CountingStorage : IDocumentStorage
    {
        private readonly CadDocumentStorage storage = new(); internal int Reads { get; private set; }
        public Task<LoadedDocument> LoadAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default)
        { Reads++; return storage.LoadAsync(path, assets, cancellationToken); }
        public Task SaveAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => storage.SaveAsync(snapshot, assets, path, cancellationToken);
        public Task<DocumentSettings> ReadSettingsAsync(string path, CancellationToken cancellationToken = default) => storage.ReadSettingsAsync(path, cancellationToken);
    }
    private sealed class NoImporter : IExchangeImportService
    { public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No exchange import permitted in field feedback."); }
    private sealed class NoGeometryKernel : IGeometryKernel
    {
        internal int Calls { get; private set; } public string Version => "No geometry permitted in field feedback";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Workspace identity feedback cannot evaluate geometry."); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
