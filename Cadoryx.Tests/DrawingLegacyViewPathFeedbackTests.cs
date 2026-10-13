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

public sealed class DrawingLegacyViewPathFeedbackTests
{
    [Theory]
    [InlineData("omitted")][InlineData("null")][InlineData("empty")][InlineData("quoted-empty")]
    [InlineData("leaf")][InlineData("foreign")][InlineData("missing-slot")][InlineData("zero-slot")]
    [InlineData("malformed-slot")][InlineData("oversize")]
    public async Task InvalidLegacyViewPathOffersReadOnlyDiscoveryWithoutChoosingSources(string fault)
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot;
        var request = f.Request();
        if (fault == "omitted") request.Remove("path");
        else request["path"] = fault switch
        {
            "null" => null, "empty" => "", "quoted-empty" => "\"\"", "leaf" => f.Slot.ToString(),
            "foreign" => Guid.NewGuid() + "/" + f.Slot,
            "missing-slot" => before.Id + "/" + Guid.NewGuid(),
            "zero-slot" => before.Id + "/" + Guid.Empty,
            "malformed-slot" => before.Id + "/wrong", _ => new string('x', 5001)
        };
        var original = request.ToJsonString(); using var result = await f.Query(request);
        var r = result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());
        Assert.Equal("drawing_view_path_invalid", r.GetProperty("rejection").GetString());
        Assert.Equal("add_drawing_view", r.GetProperty("action").GetString());
        Assert.Contains("cad_drawing", r.GetProperty("message").GetString());
        var discovery = r.GetProperty("sourceRead");
        Assert.Equal("cad_inspect", discovery.GetProperty("toolName").GetString());
        Assert.Equal("occurrences", discovery.GetProperty("arguments").GetProperty("area").GetString());
        Assert.False(discovery.GetProperty("arguments").TryGetProperty("id", out _));
        var contract = r.GetProperty("contractRead");
        Assert.Equal("cad_drawing", contract.GetProperty("toolName").GetString());
        Assert.Equal("contract", contract.GetProperty("arguments").GetProperty("action").GetString());
        Assert.Equal("view", contract.GetProperty("arguments").GetProperty("type").GetString());
        Assert.Equal(2, contract.GetProperty("arguments").EnumerateObject().Count());
        Assert.False(r.TryGetProperty("documentId", out _)); Assert.False(r.TryGetProperty("path", out _));
        Assert.False(r.TryGetProperty("selection", out _)); Assert.Equal(original, request.ToJsonString());
        // Suggestions are data. Only the caller can choose to execute a read.
        using var read = JsonDocument.Parse(await f.Tools.ExecuteAsync(new("read", "cad_inspect",
            discovery.GetProperty("arguments").GetRawText()), default));
        Assert.True(read.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(f.Path.ToString(), Assert.Single(read.RootElement.GetProperty("items").EnumerateArray()).GetProperty("path").GetString());
        f.Unchanged(before);
    }

    [Theory][InlineData("documentId")][InlineData("stateId")][InlineData("sheetId")]
    public async Task InvalidOwnerOrStateRejectsBeforeSourceGuidance(string field)
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot;
        var request = f.Request(); request[field] = Guid.NewGuid().ToString("D"); request.Remove("path");
        using var result = await f.Query(request);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("rejection", out _)); f.Unchanged(before);
    }

    [Theory][InlineData("close")][InlineData("detach")]
    public async Task LifecycleGuardPrecedesMissingPathGuidance(string boundary)
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot;
        var request = f.Request(); request.Remove("path");
        if (boundary == "close") f.Document.IsClosingRequested = true; else f.Document.Detach();
        using var result = await f.Query(request);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("rejection", out _)); f.Unchanged(before);
    }

    [Fact]
    public async Task CancellationStillPropagatesBeforeParsingOrGuidance()
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Query(f.Request(), cancel.Token));
        f.Unchanged(before);
    }

    [Fact]
    public async Task ExactLegacyPathStillReachesItsOriginalKindGuard()
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot;
        var request = f.Request(); request["kind"] = "Section";
        using var result = await f.Query(request);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Drawing view kind must be Front, Top, Right or Isometric.", result.RootElement.GetProperty("message").GetString());
        Assert.False(result.RootElement.TryGetProperty("rejection", out _)); f.Unchanged(before);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new();
        private NoGeometryKernel Kernel { get; } = new();
        internal CadDocumentSession Session { get; }
        internal CadDocumentViewModel Document { get; }
        internal CadAgentToolset Tools { get; }
        internal ComponentSlotId Slot { get; } = ComponentSlotId.New();
        internal OccurrencePath Path { get; }
        private Guid SheetId { get; }
        internal Fixture()
        {
            var snapshot = DocumentSnapshot.Create("Legacy drawing path feedback");
            var part = DefinitionId.New(); var root = (AssemblyDefinition)snapshot.Definitions[snapshot.RootAssemblyId];
            var sheet = TechnicalDrawingSheet.A4Landscape("Sheet"); SheetId = sheet.Id;
            snapshot = snapshot with
            {
                Definitions = snapshot.Definitions.Add(part, new PartDefinition(part, "Part", [], []))
                    .SetItem(root.Id, root with { Children = [new ComponentSlot(Slot, part, "Part instance", RigidTransform3d.Identity)] }),
                DrawingSheets = snapshot.DrawingSheets.Add(sheet.Id, sheet)
            };
            snapshot.Validate(); Path = new(snapshot.Id, [Slot]);
            Session = new(snapshot, Assets, Kernel, new InlineSessionDispatcher());
            Document = new(Session, Kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal JsonObject Request() => new()
        {
            ["action"] = "add_drawing_view", ["documentId"] = Session.Snapshot.Id.ToString(),
            ["stateId"] = Session.Snapshot.StateId.ToString(), ["sheetId"] = SheetId.ToString("D"),
            ["path"] = Path.ToString(), ["kind"] = "Top", ["name"] = "Top", ["scale"] = 1,
            ["centerX"] = 100, ["centerY"] = 90
        };
        internal async Task<JsonDocument> Query(JsonObject request, CancellationToken token = default) =>
            JsonDocument.Parse(await Tools.ExecuteAsync(new("test", "cad_edit", request.ToJsonString()), token));
        internal void Unchanged(DocumentSnapshot before)
        {
            Assert.Same(before, Session.Snapshot); Assert.False(Session.CanUndo); Assert.False(Session.CanRedo);
            Assert.Equal(0, Assets.Count); Assert.Equal(0, Assets.SizeBytes); Assert.Equal(0, Kernel.Calls);
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); }
    }

    private sealed class NoGeometryKernel : IGeometryKernel
    {
        internal int Calls { get; private set; } public string Version => "Managed path rejection fixture";
        private Exception Unexpected() { Calls++; return new InvalidOperationException("Path feedback must never load geometry."); }
        public bool Supports(GeometryRecipe recipe) => throw Unexpected();
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken token = default) => throw Unexpected();
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken token = default) => throw Unexpected();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken token = default) => throw Unexpected();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken token = default) => throw Unexpected();
    }
}
