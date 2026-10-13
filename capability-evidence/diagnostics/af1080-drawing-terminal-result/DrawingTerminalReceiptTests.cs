using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DrawingTerminalReceiptTests
{
    public static IEnumerable<object[]> Rejections() => from action in new[] { "inspect", "create", "edit", "reselect", "delete" }
                                                       from hasType in new[] { false, true } from direct in new[] { false, true }
                                                       select new object[] { action, hasType, direct };
    [Theory, MemberData(nameof(Rejections))]
    public async Task TerminalSuccessMatchesAnActualRejectedProductionDrawingReceipt(string action, bool hasType, bool direct)
    {
        await using var f = new Fixture(); var before = f.Session.Snapshot; var history = f.Session.HistoryUsage;
        var request = new JsonObject { ["action"] = action }; if (hasType) request["type"] = "feature";
        var result = await f.Call(request.ToJsonString(), direct);
        using var receipt = JsonDocument.Parse(result.Message);
        Assert.False(receipt.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(receipt.RootElement.GetProperty("success").GetBoolean(), result.Success);
        Assert.False(result.IsCancelled); Assert.False(result.ClearOutput);
        Assert.Equal(action == "inspect" ? null : "invalid_drawing_object_type", receipt.RootElement.TryGetProperty("rejection", out var rejection) ? rejection.GetString() : null);
        Assert.Same(before, f.Session.Snapshot); Assert.Equal(history, f.Session.HistoryUsage);
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    public static IEnumerable<object[]> Contracts() => from type in new string?[] { null, "sheet", "view", "dimension", "datum", "control_frame", "balloon", "hole_table", "field", "note", "template", "parts_table", "zones" }
                                                      from direct in new[] { false, true } select new object[] { type!, direct };
    [Theory, MemberData(nameof(Contracts))]
    public async Task EveryExistingReadOnlyContractStillReturnsTerminalSuccess(string? type, bool direct)
    {
        await using var f = new Fixture(); var request = new JsonObject { ["action"] = "contract" }; if (type is not null) request["type"] = type;
        var result = await f.Call(request.ToJsonString(), direct);
        using var receipt = JsonDocument.Parse(result.Message);
        Assert.True(receipt.RootElement.GetProperty("success").GetBoolean()); Assert.True(result.Success);
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ActualTerminalCreateAndDeleteRemainSuccessfulWithOneCommitAndOriginalUndoRedo(bool direct)
    {
        await using var f = new Fixture();
        var request = new JsonObject { ["action"] = "create", ["type"] = "sheet", ["authorJson"] = "{\"name\":\"Terminal\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Terminal\",\"standard\":\"ISO\"}" };
        var result = await f.Call(request.ToJsonString(), direct); Assert.True(result.Success);
        using (var receipt = JsonDocument.Parse(result.Message)) Assert.True(receipt.RootElement.GetProperty("success").GetBoolean());
        var sheet = Assert.Single(f.Session.Snapshot.DrawingSheets.Values); var created = f.Session.Snapshot;
        await f.Session.UndoAsync(); Assert.Empty(f.Session.Snapshot.DrawingSheets); Assert.False(f.Session.CanUndo);
        await f.Session.RedoAsync(); Assert.Same(created, f.Session.Snapshot);
        result = await f.Call(new JsonObject { ["action"] = "delete", ["type"] = "sheet", ["id"] = sheet.Id.ToString() }.ToJsonString(), direct);
        Assert.True(result.Success); Assert.Empty(f.Session.Snapshot.DrawingSheets);
        await f.Session.UndoAsync(); Assert.Equal(sheet, Assert.Single(f.Session.Snapshot.DrawingSheets.Values));
        Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task UnknownFieldsRetainOriginalTerminalFailureInsteadOfBeingRepaired(bool direct)
    {
        await using var f = new Fixture(); var request = "{\"action\":\"delete\",\"type\":\"feature\",\"path\":\"wrong-owner\"}";
        if (direct) await Assert.ThrowsAsync<ArgumentException>(() => f.Call(request, true));
        else { var result = await f.Call(request, false); Assert.False(result.Success); Assert.Contains("Unknown", result.Message); }
        Assert.False(f.Session.CanUndo); Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal NoGeometryKernel Kernel { get; } = new(); internal MemoryAssetStore Assets { get; } = new();
        internal CadDocumentSession Session { get; } private CadDocumentViewModel Document { get; }
        internal Fixture()
        {
            Session = new(DocumentSnapshot.Create("Terminal drawing receipt"), Assets, Kernel, new InlineSessionDispatcher());
            Document = new(Session, Kernel, new CadMessageLog());
        }
        internal Task<CadCommandResult> Call(string request, bool direct)
        {
            var context = new CadCommandContext(Document);
            return direct ? context.EditDrawingAsync(request) : new CadCommandLineService().ExecuteAsync("DRAWING " + request, context);
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); Assert.Equal(0, Kernel.Calls); }
    }
    private sealed class NoGeometryKernel : IGeometryKernel, ITechnicalDrawingKernel
    {
        internal int Calls { get; private set; } public string Version => "No geometry in terminal drawing receipts";
        public Task<DrawingProjectionResult> ProjectAsync(IReadOnlyList<GeometryInstance> instances, DrawingViewKind kind,
            Vector3d? sectionNormal, double sectionOffsetMm, IAssetStore assets, CancellationToken token = default,
            double sectionHatchSpacingMm = 2.5, IReadOnlyList<SectionHatchPattern>? sectionHatchPatterns = null)
        { Calls++; throw new InvalidOperationException("Unexpected projection."); }
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected geometry evaluation."); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
