using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DrawingMutationTypeFeedbackTests
{
    private static readonly string[] Types = ["sheet", "template", "view", "dimension", "datum", "control_frame", "balloon", "hole_table", "field", "note"];
    private static readonly string[] Actions = ["create", "edit", "reselect", "delete"];
    private static readonly string[] Faults = ["omitted", "null", "empty", "feature", "parts_table", "zones", "number", "wrong-case"];
    public static IEnumerable<object[]> InvalidTypes() => from action in Actions from fault in Faults from populated in new[] { false, true } select new object[] { action, fault, populated };

    [Theory, MemberData(nameof(InvalidTypes))]
    public async Task InvalidMutationTypesIdentifyTheTypeBeforeOwnerOrObjectLookupAndLeaveHistoryUnchanged(string action, string fault, bool populated)
    {
        await using var f = new Fixture();
        if (populated) await f.CreateSheet();
        var request = f.Request(action);
        if (fault != "omitted") request["type"] = fault switch
        { "null" => null, "empty" => "", "number" => JsonValue.Create(42), "wrong-case" => "View", _ => JsonValue.Create(fault) };
        var original = request.ToJsonString();
        var before = f.Session.Snapshot; var history = f.Session.HistoryUsage;
        using var result = await f.Query(original);
        var r = result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_drawing_object_type", r.GetProperty("rejection").GetString());
        Assert.Equal(action, r.GetProperty("action").GetString());
        Assert.Equal(new[] { "type" }, Strings(r, "missingOrInvalid"));
        Assert.Equal(action switch { "delete" => Types, "reselect" => Types.Where(t => t is "view" or "dimension" or "datum" or "control_frame" or "balloon" or "hole_table").ToArray(), _ => Types.Where(t => t != "template").ToArray() }, Strings(r, "allowedTypes"));
        Assert.Contains("Modeling feature/body/part IDs are not drawing object IDs", r.GetProperty("message").GetString());
        Assert.Equal(new[] { "action", "documentId", "stateId", "type", "sheetId", "id" }.Concat(action == "delete" ? [] : new[] { "authorJson" }), Strings(r, "allowedArguments"));
        Assert.Equal("{\"action\":\"contract\"}", r.GetProperty("contractRequest").GetRawText());
        Assert.False(r.TryGetProperty("id", out _)); Assert.False(r.TryGetProperty("sheetId", out _));
        Assert.False(r.TryGetProperty("documentId", out _)); Assert.False(r.TryGetProperty("stateId", out _));
        Assert.Equal(original, request.ToJsonString());
        Assert.Same(before, f.Session.Snapshot); Assert.Equal(history, f.Session.HistoryUsage);
        Assert.Equal(0, f.Assets.Count); Assert.Equal(0, f.Kernel.Calls);
    }

    [Theory]
    [InlineData("create", false)][InlineData("edit", false)][InlineData("reselect", false)][InlineData("delete", false)]
    [InlineData("create", true)][InlineData("edit", true)][InlineData("reselect", true)][InlineData("delete", true)]
    public async Task ExactDocumentAndStateGuardsPrecedeInvalidTypeGuidance(string action, bool foreignDocument)
    {
        await using var f = new Fixture(); var request = f.Request(action); request["type"] = "feature";
        request[foreignDocument ? "documentId" : "stateId"] = Guid.NewGuid().ToString();
        var before = f.Session.Snapshot;
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("allowedTypes", out _));
        Assert.False(result.RootElement.TryGetProperty("contractRequest", out _));
        Assert.Same(before, f.Session.Snapshot); Assert.False(f.Session.CanUndo);
    }

    [Theory]
    [InlineData("create")][InlineData("edit")][InlineData("reselect")][InlineData("delete")]
    public async Task UnexpectedOuterFieldsKeepTheOriginalPreExecutionRejection(string action)
    {
        await using var f = new Fixture(); var request = f.Request(action); request["type"] = "feature"; request["path"] = "not-a-drawing-field";
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "path" }, Strings(result.RootElement, "unexpectedArguments"));
        Assert.False(result.RootElement.TryGetProperty("allowedTypes", out _));
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls);
    }

    [Theory]
    [InlineData("sheet")][InlineData("template")][InlineData("view")][InlineData("dimension")][InlineData("datum")]
    [InlineData("control_frame")][InlineData("balloon")][InlineData("hole_table")][InlineData("field")][InlineData("note")]
    public async Task ValidDeleteTypesStillRequireExactOwnerAndObjectIds(string type)
    {
        await using var f = new Fixture(); var request = f.Request("delete"); request["type"] = type;
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("allowedTypes", out _));
        Assert.Contains(type is "sheet" or "template" ? "Exact object ID" : "Exact sheetId owner", result.RootElement.GetProperty("message").GetString());
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls);
    }

    [Theory]
    [InlineData("create")][InlineData("edit")][InlineData("reselect")]
    public async Task LibraryTemplatesAreRejectedBeforeOwnerLookupForOrdinaryAuthoring(string action)
    {
        await using var f = new Fixture(); var request = f.Request(action); request["type"] = "template";
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("template", Strings(result.RootElement, "allowedTypes"));
        Assert.Contains("save_template/import_template", result.RootElement.GetProperty("message").GetString());
        Assert.False(f.Session.CanUndo);
    }

    [Theory]
    [InlineData("sheet")][InlineData("field")][InlineData("note")]
    public async Task ReselectOnlyAdvertisesObjectsWithAnExistingAttachmentContract(string type)
    {
        await using var f = new Fixture(); var request = f.Request("reselect"); request["type"] = type;
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "view", "dimension", "datum", "control_frame", "balloon", "hole_table" }, Strings(result.RootElement, "allowedTypes"));
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls);
    }

    [Fact]
    public async Task ExplicitCallerChoosesTheExistingSheetContractCreatesAndDeletesThroughOriginalHistory()
    {
        await using var f = new Fixture(); var request = f.Request("create"); request["type"] = "feature";
        using (var rejected = await f.Query(request.ToJsonString()))
        {
            Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
            using var contract = await f.Query(rejected.RootElement.GetProperty("contractRequest").GetRawText());
            Assert.True(contract.RootElement.GetProperty("success").GetBoolean());
        }
        Assert.Equal("feature", request["type"]!.GetValue<string>());
        await f.CreateSheet();
        var sheet = Assert.Single(f.Session.Snapshot.DrawingSheets.Values);
        await f.Session.UndoAsync(); Assert.Empty(f.Session.Snapshot.DrawingSheets);
        await f.Session.RedoAsync(); Assert.Equal(sheet.Id, Assert.Single(f.Session.Snapshot.DrawingSheets.Values).Id);
        var deletion = f.Request("delete"); deletion["type"] = "sheet"; deletion["id"] = sheet.Id.ToString();
        using (var result = await f.Query(deletion.ToJsonString())) Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(f.Session.Snapshot.DrawingSheets);
        await f.Session.UndoAsync(); Assert.Equal(sheet, Assert.Single(f.Session.Snapshot.DrawingSheets.Values));
        Assert.Equal(0, f.Assets.Count); Assert.Equal(0, f.Kernel.Calls);
    }

    [Theory]
    [InlineData("create")][InlineData("edit")][InlineData("reselect")][InlineData("delete")]
    public async Task PreCancelledMutationDoesNotParseOrReturnGuidance(string action)
    {
        await using var f = new Fixture(); var request = f.Request(action); request["type"] = "feature";
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Query(request.ToJsonString(), cancellation.Token));
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls);
    }

    [Theory]
    [InlineData("create")][InlineData("edit")][InlineData("reselect")][InlineData("delete")]
    public async Task DetachedDocumentIsRejectedBeforeGuidance(string action)
    {
        await using var f = new Fixture(); var request = f.Request(action); request["type"] = "feature";
        f.Document.Detach();
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("allowedTypes", out _));
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls);
    }

    private static string[] Strings(JsonElement r, string key) => r.GetProperty(key).EnumerateArray().Select(v => v.GetString()!).ToArray();
    private sealed class Fixture : IAsyncDisposable
    {
        internal NoGeometryKernel Kernel { get; } = new(); internal MemoryAssetStore Assets { get; } = new();
        internal CadDocumentSession Session { get; } internal CadDocumentViewModel Document { get; }
        private CadAgentToolset Tools { get; }
        internal Fixture()
        {
            Session = new(DocumentSnapshot.Create("Drawing mutation type"), Assets, Kernel, new InlineSessionDispatcher());
            Document = new(Session, Kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal JsonObject Request(string action) => new() { ["action"] = action, ["documentId"] = Session.Snapshot.Id.ToString(), ["stateId"] = Session.Snapshot.StateId.ToString() };
        internal async Task<JsonDocument> Query(string text, CancellationToken token = default) => JsonDocument.Parse(await Tools.ExecuteAsync(new("test", "cad_drawing", text), token));
        internal async Task CreateSheet()
        {
            var request = Request("create"); request["type"] = "sheet";
            request["authorJson"] = "{\"name\":\"Sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Sheet\",\"standard\":\"ISO\"}";
            using var result = await Query(request.ToJsonString()); Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.GetRawText());
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); Assert.Equal(0, Kernel.Calls); }
    }
    private sealed class NoGeometryKernel : IGeometryKernel, ITechnicalDrawingKernel
    {
        internal int Calls { get; private set; } public string Version => "No geometry in drawing type feedback";
        public Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances,
            DrawingViewKind kind, Vector3d? sectionNormal, double sectionOffsetMm, IAssetStore assets,
            CancellationToken token = default, double sectionHatchSpacingMm = 2.5, IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns = null)
        { Calls++; throw new InvalidOperationException("Type feedback cannot project geometry."); }
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected geometry evaluation."); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
