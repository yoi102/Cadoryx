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

public sealed class ProviderIdentityReinspectionTests
{
    [Theory]
    [InlineData("documentId", "missing")]
    [InlineData("documentId", "malformed")]
    [InlineData("stateId", "missing")]
    [InlineData("stateId", "malformed")]
    public async Task DrawingIdentitySourceIsReadOnlyAndNeverSuppliesAReplacementMutation(string field, string fault)
    {
        await using var f = new Fixture(); var document = f.Add("Drawing source"); var before = document.Session.Snapshot;
        var request = DrawingRequest(document);
        if (fault == "missing") request.Remove(field); else request[field] = "not-an-identity";
        var original = request.ToJsonString(); var reply = await f.Drawing(original);
        Assert.False(reply["success"]!.GetValue<bool>());
        Assert.Equal(field, Assert.Single(reply["missingOrInvalid"]!.AsArray())!.GetValue<string>());
        var source = reply["identitySource"]!.AsObject();
        Assert.Equal("cad_status", source["toolName"]!.GetValue<string>()); Assert.Empty(source["arguments"]!.AsObject());
        Assert.Contains("active cad_status", reply["identityRole"]!.GetValue<string>());
        Assert.False(reply.ContainsKey("documentId")); Assert.False(reply.ContainsKey("stateId")); Assert.False(reply.ContainsKey("nextRequest"));
        Assert.Same(before, document.Session.Snapshot); Assert.False(document.Session.CanUndo);
        Assert.Equal(original, request.ToJsonString()); f.NoReadsOrGeometry();
    }

    [Fact]
    public async Task CallerMustReadActualStatusAndExplicitlyResubmitBeforeDrawingCreation()
    {
        await using var f = new Fixture(); var document = f.Add("Drawing source"); var before = document.Session.Snapshot;
        var request = DrawingRequest(document); request.Remove("stateId");
        var rejected = await f.Drawing(request.ToJsonString()); Assert.Empty(before.DrawingSheets);
        var source = rejected["identitySource"]!;
        var status = JsonNode.Parse(await new CadAgentToolset(new CadCommandLineService(), document).ExecuteAsync(
            new("explicit-status", source["toolName"]!.GetValue<string>(), source["arguments"]!.ToJsonString()), default))!;
        request["documentId"] = status["documentId"]!.DeepClone(); request["stateId"] = status["stateId"]!.DeepClone();
        var created = await f.Drawing(request.ToJsonString()); Assert.True(created["success"]!.GetValue<bool>(), created.ToJsonString());
        Assert.Single(document.Session.Snapshot.DrawingSheets); var after = document.Session.Snapshot;
        await document.Session.UndoAsync(); Assert.Same(before, document.Session.Snapshot);
        await document.Session.RedoAsync(); Assert.Same(after, document.Session.Snapshot); f.NoReadsOrGeometry();
    }

    [Fact]
    public async Task WellFormedStaleDrawingIdentityStillRejectsThroughTheOriginalGuard()
    {
        await using var f = new Fixture(); var document = f.Add("Drawing source"); var before = document.Session.Snapshot;
        var request = DrawingRequest(document); request["stateId"] = Guid.NewGuid().ToString();
        var reply = await f.Drawing(request.ToJsonString()); Assert.False(reply["success"]!.GetValue<bool>());
        Assert.False(reply.ContainsKey("missingOrInvalid")); Assert.False(reply.ContainsKey("identitySource"));
        Assert.Same(before, document.Session.Snapshot); Assert.False(document.Session.CanUndo); f.NoReadsOrGeometry();
    }

    [Fact]
    public async Task ContractArgumentGuideKeepsItsIdentityFreeReadExamples()
    {
        await using var f = new Fixture(); f.Add("Contract source");
        var reply = await f.Drawing("{\"action\":\"contract\",\"type\":\"sheet\",\"documentId\":\"not-a-guid\"}");
        Assert.False(reply["success"]!.GetValue<bool>()); Assert.False(reply.ContainsKey("identitySource"));
        var example = Assert.Single(reply["validRequests"]!.AsArray())!.AsObject();
        Assert.Equal(2, example.Count); Assert.Equal("contract", example["action"]!.GetValue<string>());
        Assert.Equal("sheet", example["type"]!.GetValue<string>()); f.NoReadsOrGeometry();
    }

    [Fact]
    public async Task CancelledDrawingDoesNotParseOrReturnIdentityFeedback()
    {
        await using var f = new Fixture(); f.Add("Cancelled source"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Drawing("not JSON", cancel.Token)); f.NoReadsOrGeometry();
    }

    [Theory]
    [InlineData("new", "target-role")]
    [InlineData("open", "target-role")]
    [InlineData("activate", "target-role")]
    [InlineData("new", "old-workspace")]
    [InlineData("open", "old-workspace")]
    [InlineData("activate", "old-workspace")]
    [InlineData("new", "old-state")]
    [InlineData("open", "old-state")]
    [InlineData("activate", "old-state")]
    public async Task RejectedWorkspaceExpectationExplainsCurrentAndTargetRolesWithoutReadingOrRetrying(string action, string fault)
    {
        await using var f = new Fixture(); var first = f.Add("Same name"); var second = f.Add("Same name");
        var input = await f.InputFile(); var listed = await f.WorkspaceCall("{\"action\":\"list\"}");
        var request = new JsonObject { ["action"] = action, ["expected"] = listed["expected"]!.DeepClone() };
        if (action == "new") request["name"] = "Explicit third";
        if (action == "open") request["path"] = input;
        if (action == "activate") request["target"] = Target(first);
        var expected = request["expected"]!.AsObject();
        if (fault == "target-role")
        { expected["activeSessionId"] = first.Session.SessionId.ToString(); expected["documentId"] = first.Session.Snapshot.Id.ToString(); expected["stateId"] = first.Session.Snapshot.StateId.ToString(); }
        else expected[fault == "old-workspace" ? "workspaceStateId" : "stateId"] = Guid.NewGuid().ToString();
        var original = request.ToJsonString(); var before = f.Author.Capture();
        var reply = await f.WorkspaceCall(original); Assert.False(reply["success"]!.GetValue<bool>()); AssertReinspection(reply);
        Assert.False(reply.ContainsKey("invalidFields")); Assert.False(reply.ContainsKey("identitySource"));
        Assert.Equal(before, f.Author.Capture()); Assert.Same(second, f.Active); Assert.Equal(2, f.Documents.Count);
        Assert.Equal(original, request.ToJsonString()); f.NoReadsOrGeometry();
        var source = reply["reinspection"]!;
        var fresh = await f.WorkspaceCall(source["arguments"]!.ToJsonString()); request["expected"] = fresh["expected"]!.DeepClone();
        var accepted = await f.WorkspaceCall(request.ToJsonString()); Assert.True(accepted["success"]!.GetValue<bool>());
        if (action == "activate") { Assert.Same(first, f.Active); Assert.Equal(2, f.Documents.Count); }
        else Assert.Equal(3, f.Documents.Count);
        Assert.Equal(action == "open" ? 1 : 0, f.Storage.Reads); Assert.Equal(0, f.Kernel.Calls);
    }

    [Fact]
    public async Task LateOpenRejectionDoesNotClaimThatNoFileReadOccurredAndKeepsTheNewForeground()
    {
        await using var f = new Fixture(); f.Add("Before read"); var input = await f.InputFile();
        var list = await f.WorkspaceCall("{\"action\":\"list\"}"); CadDocumentViewModel? newForeground = null;
        f.Storage.BeforeRead = () => newForeground = f.Add("Foreground changed during read");
        var reply = await f.WorkspaceCall(new JsonObject { ["action"] = "open", ["expected"] = list["expected"]!.DeepClone(), ["path"] = input }.ToJsonString());
        Assert.False(reply["success"]!.GetValue<bool>()); AssertReinspection(reply);
        Assert.Equal(1, f.Storage.Reads); Assert.Equal(2, f.Documents.Count); Assert.Same(newForeground, f.Active);
        Assert.DoesNotContain("No file read", reply.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(reply.ContainsKey("expected")); Assert.False(reply.ContainsKey("target")); Assert.Equal(0, f.Assets.Count);
    }

    [Fact]
    public async Task CancelledWorkspaceDoesNotReadOrGenerateReinspectionFeedback()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.WorkspaceCall("not JSON", cancel.Token));
        Assert.Empty(f.Documents); f.NoReadsOrGeometry();
    }

    private static void AssertReinspection(JsonObject reply)
    {
        Assert.Equal(CadWorkspaceAuthoring.ExpectationChangedMessage, reply["message"]!.GetValue<string>());
        Assert.Equal("workspace_expectation_changed", reply["rejection"]!.GetValue<string>());
        Assert.Equal("cad_workspace", reply["reinspection"]!["toolName"]!.GetValue<string>());
        var read = reply["reinspection"]!["arguments"]!.AsObject(); Assert.Single(read); Assert.Equal("list", read["action"]!.GetValue<string>());
        Assert.Contains("currently active", reply["expectedRole"]!.GetValue<string>());
        Assert.Contains("separately", reply["targetRole"]!.GetValue<string>());
        Assert.False(reply.ContainsKey("expected")); Assert.False(reply.ContainsKey("target")); Assert.False(reply.ContainsKey("nextRequest"));
    }

    private static JsonObject Target(CadDocumentViewModel document) => new()
    { ["sessionId"] = document.Session.SessionId.ToString(), ["documentId"] = document.Session.Snapshot.Id.ToString(), ["stateId"] = document.Session.Snapshot.StateId.ToString() };

    private static JsonObject DrawingRequest(CadDocumentViewModel document) => new()
    {
        ["action"] = "create", ["type"] = "sheet", ["documentId"] = document.Session.Snapshot.Id.ToString(), ["stateId"] = document.Session.Snapshot.StateId.ToString(),
        ["authorJson"] = "{\"name\":\"Explicit sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"standard\":\"ISO\",\"title\":\"Identity\"}"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new(); internal NoGeometryKernel Kernel { get; } = new();
        internal CountingStorage Storage { get; } = new(); internal ObservableCollection<CadDocumentViewModel> Documents { get; } = [];
        internal CadWorkspace Workspace { get; } internal CadWorkspaceAuthoring Author { get; } internal CadDocumentViewModel? Active;
        private TestFiles Files { get; } = new();
        internal Fixture()
        {
            Workspace = new(Kernel, Assets, new InlineSessionDispatcher());
            Author = new(Workspace, Documents, () => Active, Attach, SetActive, () => true, Assets, Storage, new NoImporter());
        }
        private CadDocumentViewModel Attach(CadDocumentSession session)
        { var document = new CadDocumentViewModel(session, Kernel, new CadMessageLog(), Storage); Documents.Add(document); SetActive(document); return document; }
        private void SetActive(CadDocumentViewModel document) { Active = document; Author.NotifyChanged(); }
        internal CadDocumentViewModel Add(string name) => Attach(Workspace.Create(name));
        internal async Task<string> InputFile()
        { var path = Files.PathFor("read-" + Guid.NewGuid().ToString("N") + ".cadoryx"); await Storage.SaveAsync(DocumentSnapshot.Create("Read source"), Assets, path); return path; }
        internal async Task<JsonObject> WorkspaceCall(string text, CancellationToken token = default) =>
            JsonNode.Parse(await CadAgentWorkspaceTools.ExecuteAsync(new("workspace", "cad_workspace", text), Author, token))!.AsObject();
        internal async Task<JsonObject> Drawing(string text, CancellationToken token = default) =>
            JsonNode.Parse(await CadAgentDrawingTools.ExecuteAsync(new("drawing", "cad_drawing", text), Active, token))!.AsObject();
        internal void NoReadsOrGeometry() { Assert.Equal(0, Storage.Reads); Assert.Equal(0, Kernel.Calls); Assert.Equal(0, Assets.Count); }
        public async ValueTask DisposeAsync()
        { foreach (var document in Documents) document.Detach(); await Workspace.DisposeAsync(); Assert.Equal(0, Assets.Count); Files.Dispose(); }
    }

    private sealed class CountingStorage : IDocumentStorage
    {
        private readonly CadDocumentStorage inner = new(); internal int Reads; internal Action? BeforeRead;
        public async Task<LoadedDocument> LoadAsync(string path, IAssetStore assets, CancellationToken token = default)
        { Reads++; BeforeRead?.Invoke(); return await inner.LoadAsync(path, assets, token); }
        public Task SaveAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken token = default) => inner.SaveAsync(snapshot, assets, path, token);
        public Task<DocumentSettings> ReadSettingsAsync(string path, CancellationToken token = default) => inner.ReadSettingsAsync(path, token);
    }
    private sealed class NoImporter : IExchangeImportService
    { public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken token = default) => throw new NotSupportedException(); }
    private sealed class NoGeometryKernel : IGeometryKernel, ITechnicalDrawingKernel
    {
        internal int Calls; public string Version => "Identity reinspection has no geometry";
        public Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,
            DrawingViewKind kind, Vector3d? sectionNormal, double sectionOffsetMm, IAssetStore assets,
            CancellationToken token = default, double sectionHatchSpacingMm = 2.5, IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns = null)
        { Calls++; throw new NotSupportedException("Identity reinspection must not project geometry."); }
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken token = default)
        { Calls++; throw new NotSupportedException(); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken token = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken token = default) => throw new NotSupportedException();
    }
}
