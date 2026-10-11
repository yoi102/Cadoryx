using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class HistoryAgentTests
{
    [Fact]
    public async Task StructuredSuppressionRestoreAndLegalChronologyReuseNativeHistoryAndUndo()
    {
        await using var f = await Fixture.Create();
        await f.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut, [f.Base.OutputBodyId, f.Tool.OutputBodyId]));
        var cut = f.Session.Snapshot.Features.Values.Single(x => x.Recipe is BooleanRecipe);
        var inputs = cut.Inputs; var before = f.Session.Snapshot;
        var invalid = await f.CallResult("reorder", new { partId = f.Part.Value, order = new[] { cut.Id.Value, f.Base.Id.Value, f.Tool.Id.Value } });
        Assert.False(Success(invalid)); Assert.Same(before, f.Session.Snapshot);
        var result = await f.Call("reorder", new { partId = f.Part.Value, order = new[] { f.Tool.Id.Value, f.Base.Id.Value, cut.Id.Value } });
        Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal(inputs, f.Session.Snapshot.Features[cut.Id].Inputs);
        Assert.Equal(before.Features[cut.Id].Result, f.Session.Snapshot.Features[cut.Id].Result);
        var reordered = f.Session.Snapshot;
        await f.Session.UndoAsync(); Assert.Same(before, f.Session.Snapshot); await f.Session.RedoAsync(); Assert.Same(reordered, f.Session.Snapshot);
        var suppressed = await f.Call("set_suppressed", new { id = f.Base.Id.Value, revision = f.Session.Snapshot.Features[f.Base.Id].Result.Revision.Value, suppressed = true });
        Assert.True(suppressed.GetProperty("changed").GetBoolean()); Assert.True(f.Session.Snapshot.Features[f.Base.Id].IsSuppressed);
        Assert.False(f.Session.Snapshot.Bodies.ContainsKey(cut.OutputBodyId));
        await f.Call("set_suppressed", new { id = f.Base.Id.Value, revision = f.Session.Snapshot.Features[f.Base.Id].Result.Revision.Value, suppressed = false });
        Assert.Equal(12, f.Session.Snapshot.Features[cut.Id].Result.VolumeMm3, 7); f.Session.Snapshot.Validate();
        var noOp = await f.Call("set_suppressed", new { id = f.Base.Id.Value, revision = f.Session.Snapshot.Features[f.Base.Id].Result.Revision.Value, suppressed = false });
        Assert.False(noOp.GetProperty("changed").GetBoolean());
        var state = f.Session.Snapshot;
        Assert.False(Success(await f.CallResult("set_suppressed", new { id = f.Base.Id.Value, revision = Guid.NewGuid(), suppressed = true })));
        Assert.Same(state, f.Session.Snapshot);
    }

    [Fact]
    public async Task PreviewHasASeparateCursorIdentityAndCannotBeBypassedByTheCommandTool()
    {
        await using var f = await Fixture.Create(); var before = f.Session.Snapshot;
        var staleEnd = f.Arguments("return_end");
        var rollback = await f.Call("rollback", new { partId = f.Part.Value, position = 1 });
        Assert.False(rollback.GetProperty("changed").GetBoolean()); Assert.True(rollback.GetProperty("previewChanged").GetBoolean());
        Assert.Same(before, f.Session.Snapshot); Assert.True(f.Session.IsRolledBack);
        Assert.Single(f.Session.DisplaySnapshot.Bodies); Assert.Equal(f.Base.OutputBodyId, f.Session.DisplaySnapshot.Bodies.Keys.Single());
        Assert.False(Success(await f.Tools.ExecuteAsync(new("1", "cad_history", staleEnd.ToJsonString()), default)));
        var current = f.Session.FeatureCursor;
        Assert.False(Success(await f.CallResult("set_suppressed", new { id = f.Base.Id.Value, revision = f.Base.Result.Revision.Value, suppressed = true })));
        Assert.Equal(current, f.Session.FeatureCursor); Assert.Same(before, f.Session.Snapshot);
        foreach (var command in new[] { "HISTORY END", "history END", "\"HISTORY\" END" })
            Assert.False(Success(await f.Tools.ExecuteAsync(new("2", "cad_command", JsonSerializer.Serialize(new
            { command, documentId = before.Id.ToString(), stateId = before.StateId.ToString() })), default)));
        var copiedCursor = rollback.GetProperty("cursor");
        Assert.Equal(1, copiedCursor.GetProperty("position").GetInt32());
        var returnedJson = await f.Tools.ExecuteAsync(new("4", "cad_history", JsonSerializer.Serialize(new
        { action = "return_end", documentId = before.Id.ToString(), stateId = before.StateId.ToString(), cursor = copiedCursor })), default);
        Assert.True(Success(returnedJson), returnedJson); using var parsed = JsonDocument.Parse(returnedJson); var returned = parsed.RootElement;
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("cursor").ValueKind);
        Assert.False(returned.GetProperty("changed").GetBoolean()); Assert.True(returned.GetProperty("previewChanged").GetBoolean());
        Assert.False(f.Session.IsRolledBack); Assert.Same(before, f.Session.Snapshot);
        Assert.False((await f.Call("return_end")).GetProperty("previewChanged").GetBoolean());
    }

    [Fact]
    public async Task TerminalHistoryCapturesThePreviewBoundaryAndUsesExactRevisions()
    {
        await using var f = await Fixture.Create(); var service = new CadCommandLineService(); var stale = new CadCommandContext(f.Document);
        async Task<CadCommandResult> Run(string input) => await service.ExecuteAsync(input, new CadCommandContext(f.Document));
        Assert.True((await Run($"HISTORY ROLLBACK {f.Part} 1")).Success);
        Assert.False((await service.ExecuteAsync("HISTORY END", stale)).Success); Assert.True(f.Session.IsRolledBack);
        Assert.True((await Run("HISTORY END")).Success); Assert.False(f.Session.IsRolledBack);
        Assert.True((await Run($"HISTORY SUPPRESS {f.Base.Id} {f.Base.Result.Revision}")).Success);
        Assert.True(f.Session.Snapshot.Features[f.Base.Id].IsSuppressed);
        var revision = f.Session.Snapshot.Features[f.Base.Id].Result.Revision;
        Assert.True((await Run($"HISTORY RESTORE {f.Base.Id} {revision}")).Success);
        var before = f.Session.Snapshot;
        foreach (var input in new[] { "HISTORY END EXTRA", $"HISTORY ROLLBACK {f.Part} -1", "HISTORY SUPPRESS by-name", $"HISTORY REORDER {f.Part} {f.Base.Id} {f.Base.Id}" })
            Assert.False((await Run(input)).Success, input);
        Assert.Same(before, f.Session.Snapshot);
    }

    [Theory][InlineData(false, "cancel")][InlineData(true, "cancel")][InlineData(false, "state")]
    [InlineData(true, "state")][InlineData(false, "cursor")][InlineData(true, "cursor")]
    public async Task QueuedPreviewRechecksTokenAuthorStateAndCursorUnderTheSessionLock(bool returnEnd, string boundary)
    {
        var dispatcher = new DelayedDispatcher(); await using var f = await Fixture.Create(dispatcher);
        if (returnEnd) await f.Session.RollbackFeaturesAsync(f.Part, 1);
        var expected = new DocumentHistoryExpectation(DocumentCommandExpectation.Capture(f.Session.Snapshot), f.Session.FeatureCursor);
        using var cancel = new CancellationTokenSource(); dispatcher.Pause = true;
        var pending = returnEnd ? f.Session.ReturnToHistoryEndAsync(cancel.Token, expected) : f.Session.RollbackFeaturesAsync(f.Part, 1, cancel.Token, expected);
        await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); dispatcher.Pause = false;
        if (boundary == "cancel") cancel.Cancel();
        if (boundary == "cursor")
        { if (returnEnd) await f.Session.ReturnToHistoryEndAsync(); else await f.Session.RollbackFeaturesAsync(f.Part, 0); }
        if (boundary == "state")
        { await f.Session.ReturnToHistoryEndAsync(); await f.Session.ExecuteAsync(DocumentEdits.SetGrid(f.Session.Snapshot.Settings.Grid with { SpacingMm = 17 })); }
        var snapshot = f.Session.Snapshot; var cursor = f.Session.FeatureCursor; var generation = f.Session.Generation; var count = f.Assets.Count;
        dispatcher.Release.TrySetResult();
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<StaleDocumentException>(() => pending);
        Assert.Same(snapshot, f.Session.Snapshot); Assert.Equal(cursor, f.Session.FeatureCursor); Assert.Equal(generation, f.Session.Generation); Assert.Equal(count, f.Assets.Count);
    }

    [Theory][InlineData("missing-cursor")][InlineData("foreign-state")][InlineData("cursor-extra")][InlineData("unknown")]
    [InlineData("duplicate")][InlineData("position-type")][InlineData("position-range")][InlineData("wrong-part")]
    public async Task MalformedPreviewArgumentsPreserveAuthorAndDisplayState(string mutation)
    {
        await using var f = await Fixture.Create();
        var args = f.Arguments("rollback", new { partId = f.Part.Value, position = 1 });
        if (mutation == "missing-cursor") args.Remove("cursor");
        if (mutation == "foreign-state") args["stateId"] = Guid.NewGuid().ToString();
        if (mutation == "cursor-extra") args["cursor"] = new JsonObject { ["stateId"] = f.Session.Snapshot.StateId.ToString(), ["partId"] = f.Part.ToString(), ["position"] = 0, ["force"] = true };
        if (mutation == "unknown") args["force"] = true;
        if (mutation == "position-type") args["position"] = "1";
        if (mutation == "position-range") args["position"] = 1000001;
        if (mutation == "wrong-part") args["partId"] = f.Session.Snapshot.RootAssemblyId.ToString();
        var json = args.ToJsonString(); if (mutation == "duplicate") json = json[..^1] + ",\"position\":0}";
        var before = f.Session.Snapshot; var generation = f.Session.Generation;
        Assert.False(Success(await f.Tools.ExecuteAsync(new("3", "cad_history", json), default)));
        Assert.Same(before, f.Session.Snapshot); Assert.False(f.Session.IsRolledBack); Assert.Equal(generation, f.Session.Generation);
    }

    private static bool Success(string json) { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.GetProperty("success").GetBoolean(); }
    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new();
        internal CadDocumentSession Session { get; }
        internal CadDocumentViewModel Document { get; }
        internal CadAgentToolset Tools { get; }
        internal FeatureDefinition Base { get; private set; } = null!;
        internal FeatureDefinition Tool { get; private set; } = null!;
        internal DefinitionId Part => Base.PartId;
        private Fixture(ISessionDispatcher dispatcher)
        {
            var kernel = new OcctGeometryKernel(); Session = new(DocumentSnapshot.Create("History tools"), Assets, kernel, dispatcher);
            Document = new(Session, kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal static async Task<Fixture> Create(ISessionDispatcher? dispatcher = null)
        {
            var f = new Fixture(dispatcher ?? new InlineSessionDispatcher());
            await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), "Base")); f.Base = f.Session.Snapshot.Features.Values.Single();
            await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Translate(1, 0, 0)), "Tool", f.Part));
            f.Tool = f.Session.Snapshot.Features.Values.Single(x => x.Id != f.Base.Id); return f;
        }
        internal JsonObject Arguments(string action, object? fields = null)
        {
            var args = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields)!.AsObject();
            args["action"] = action; args["documentId"] = Session.Snapshot.Id.ToString(); args["stateId"] = Session.Snapshot.StateId.ToString();
            var cursor = Session.FeatureCursor;
            args["cursor"] = cursor is null ? null : JsonSerializer.SerializeToNode(new { stateId = cursor.State.ToString(), partId = cursor.Part.ToString(), cursor.Position }, CadAuthoringInspection.JsonOptions);
            return args;
        }
        internal Task<string> CallResult(string action, object? fields = null) => Tools.ExecuteAsync(new("1", "cad_history", Arguments(action, fields).ToJsonString()), default);
        internal async Task<JsonElement> Call(string action, object? fields = null)
        { var result = await CallResult(action, fields); Assert.True(Success(result), result); using var parsed = JsonDocument.Parse(result); return parsed.RootElement.Clone(); }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); }
    }

    private sealed class DelayedDispatcher : ISessionDispatcher
    {
        internal bool Pause { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InvokeAsync(Action action) { if (Pause) { Entered.TrySetResult(); await Release.Task; } action(); }
    }
}
