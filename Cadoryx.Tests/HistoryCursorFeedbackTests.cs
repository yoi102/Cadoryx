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

[Collection("ModelAgentNativeGates")]
public sealed class HistoryCursorFeedbackTests
{
    [Theory]
    [InlineData("stateId", "missing")][InlineData("stateId", "invalid")]
    [InlineData("stateId", "empty")][InlineData("stateId", "number")]
    [InlineData("partId", "missing")][InlineData("partId", "invalid")]
    [InlineData("partId", "empty")][InlineData("partId", "number")]
    [InlineData("position", "missing")][InlineData("position", "text")]
    [InlineData("position", "negative")][InlineData("position", "over-budget")]
    [InlineData("position", "fraction")][InlineData("position", "null")]
    [InlineData("stateId", "duplicate")][InlineData("partId", "duplicate")]
    [InlineData("position", "duplicate")][InlineData("force", "unknown")]
    public async Task InvalidNestedFieldReportsCursorPathsWithoutChangingTheRealPreview(string field, string mutation)
    {
        await using var f = await Fixture.Create();
        await f.Session.RollbackFeaturesAsync(f.Part, 0);
        var args = f.Arguments(); var cursor = args["cursor"]!.AsObject();
        if (mutation == "missing") cursor.Remove(field);
        else if (mutation == "invalid") cursor[field] = "by-name";
        else if (mutation == "empty") cursor[field] = Guid.Empty.ToString();
        else if (mutation == "number") cursor[field] = 1;
        else if (mutation == "text") cursor[field] = "0";
        else if (mutation == "negative") cursor[field] = -1;
        else if (mutation == "over-budget") cursor[field] = 1000001;
        else if (mutation == "fraction") cursor[field] = .5;
        else if (mutation == "null") cursor[field] = null;
        else if (mutation == "unknown") cursor[field] = true;
        var json = args.ToJsonString();
        if (mutation == "duplicate")
            json = json.Replace(cursor.ToJsonString(), cursor.ToJsonString()[..^1] + ",\"" + field + "\":" + cursor[field]!.ToJsonString() + "}", StringComparison.Ordinal);
        var before = f.Capture();
        var result = await f.Call(json);
        Assert.False(result.GetProperty("success").GetBoolean());
        var message = result.GetProperty("message").GetString()!;
        Assert.StartsWith("Invalid cursor object:", message);
        Assert.Contains(mutation is "negative" or "over-budget" or "fraction" or "text" or "null" ? "history position" : field, message);
        Assert.Contains("cursor.stateId", message); Assert.Contains("cursor.partId", message); Assert.Contains("cursor.position", message);
        Assert.Contains("root partId is only rollback/reorder", message);
        f.AssertSame(before);
    }

    [Theory][InlineData("[]")][InlineData("true")][InlineData("1")][InlineData("\"not-an-object\"")]
    public async Task NonObjectCursorIsRejectedWithItsPathAndDoesNotReturnToEnd(string cursorJson)
    {
        await using var f = await Fixture.Create(); await f.Session.RollbackFeaturesAsync(f.Part, 0);
        var args = f.Arguments(); args["cursor"] = JsonNode.Parse(cursorJson); var before = f.Capture();
        var result = await f.Call(args.ToJsonString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Invalid cursor object: Expected an object.", result.GetProperty("message").GetString());
        f.AssertSame(before);
    }

    [Fact]
    public async Task MissingCursorIsNotInferredAsNullEvenAtTheActualHistoryEnd()
    {
        await using var f = await Fixture.Create(); var args = f.Arguments(); args.Remove("cursor"); var before = f.Capture();
        var result = await f.Call(args.ToJsonString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Supply the inspected cursor or explicit null", result.GetProperty("message").GetString());
        f.AssertSame(before);
    }

    [Fact]
    public async Task ReturnEndRejectsRootPartIdAndDoesNotMoveItIntoCursor()
    {
        await using var f = await Fixture.Create(); await f.Session.RollbackFeaturesAsync(f.Part, 0);
        var args = f.Arguments(); args["partId"] = f.Part.ToString(); var before = f.Capture();
        var result = await f.Call(args.ToJsonString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown or duplicate argument: partId.", result.GetProperty("message").GetString());
        f.AssertSame(before);
    }

    [Fact]
    public async Task RollbackRootPartIdDoesNotSupplyMissingNestedCursorPartId()
    {
        await using var f = await Fixture.Create(); await f.Session.RollbackFeaturesAsync(f.Part, 0);
        var args = f.Arguments(); args["action"] = "rollback"; args["partId"] = f.Part.ToString(); args["position"] = 1;
        args["cursor"]!.AsObject().Remove("partId"); var before = f.Capture();
        var result = await f.Call(args.ToJsonString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Missing or invalid partId.", result.GetProperty("message").GetString());
        Assert.Contains("cursor.partId", result.GetProperty("message").GetString());
        f.AssertSame(before);
    }

    [Fact]
    public async Task ExactCursorReturnsToEndButValidStaleCursorCannotBypassTheSessionGuard()
    {
        await using var f = await Fixture.Create();
        var part = f.Part;
        await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Translate(5, 0, 0)), "Second", part));
        await f.Session.RollbackFeaturesAsync(part, 0);
        var original = f.Session.Snapshot; var stale = f.Arguments().ToJsonString();
        await f.Session.RollbackFeaturesAsync(part, 1); var before = f.Capture();
        var rejected = await f.Call(stale); Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("Invalid cursor object:", rejected.GetProperty("message").GetString()); f.AssertSame(before);
        var returned = await f.Call(f.Arguments().ToJsonString());
        Assert.True(returned.GetProperty("success").GetBoolean()); Assert.False(returned.GetProperty("changed").GetBoolean());
        Assert.True(returned.GetProperty("previewChanged").GetBoolean()); Assert.Equal(JsonValueKind.Null, returned.GetProperty("cursor").ValueKind);
        Assert.Same(original, f.Session.Snapshot); Assert.False(f.Session.IsRolledBack);
        await f.Session.UndoAsync(); Assert.Single(f.Session.Snapshot.Bodies);
        await f.Session.RedoAsync(); Assert.Same(original, f.Session.Snapshot);
        Assert.Equal(2, f.Session.Snapshot.Bodies.Count); Assert.All(f.Session.Snapshot.Bodies.Values, b => Assert.Equal(24, b.Geometry.VolumeMm3, 7));
    }

    [Fact]
    public async Task ExplicitNullIsAcceptedOnlyWhenItMatchesTheActualHistoryEnd()
    {
        await using var f = await Fixture.Create(); var args = f.Arguments(); var before = f.Capture();
        var accepted = await f.Call(args.ToJsonString()); Assert.True(accepted.GetProperty("success").GetBoolean());
        Assert.False(accepted.GetProperty("changed").GetBoolean()); Assert.False(accepted.GetProperty("previewChanged").GetBoolean()); f.AssertSame(before);
        await f.Session.RollbackFeaturesAsync(f.Part, 0); before = f.Capture();
        var rejected = await f.Call(args.ToJsonString()); Assert.False(rejected.GetProperty("success").GetBoolean()); f.AssertSame(before);
    }

    [Fact]
    public async Task ForeignCursorStateAndCancelledRequestKeepTheRealCursorAndAssets()
    {
        await using var f = await Fixture.Create(); await f.Session.RollbackFeaturesAsync(f.Part, 0);
        var args = f.Arguments(); args["cursor"]!["stateId"] = Guid.NewGuid().ToString(); var before = f.Capture();
        var rejected = await f.Call(args.ToJsonString()); Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Contains("Preview cursor does not belong", rejected.GetProperty("message").GetString()); f.AssertSame(before);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Call("{bad-json", cancel.Token)); f.AssertSame(before);
    }

    private sealed record CapturedState(DocumentSnapshot Author, DocumentSnapshot Display, FeatureCursor? Cursor, long Generation,
        DocumentHistoryUsage History, int Assets, bool Dirty);
    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new(); internal CadDocumentSession Session { get; }
        private CadDocumentViewModel Document { get; } private CadAgentToolset Tools { get; }
        internal DefinitionId Part => Session.Snapshot.Features.Values.Single().PartId;
        private Fixture()
        {
            var kernel = new OcctGeometryKernel(); Session = new(DocumentSnapshot.Create("Cursor feedback"), Assets, kernel, new InlineSessionDispatcher());
            Document = new(Session, kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), "Seed")); return f;
        }
        internal JsonObject Arguments()
        {
            var cursor = Session.FeatureCursor;
            return new JsonObject { ["action"] = "return_end", ["documentId"] = Session.Snapshot.Id.ToString(), ["stateId"] = Session.Snapshot.StateId.ToString(),
                ["cursor"] = cursor is null ? null : new JsonObject { ["stateId"] = cursor.State.ToString(), ["partId"] = cursor.Part.ToString(), ["position"] = cursor.Position } };
        }
        internal CapturedState Capture() => new(Session.Snapshot, Session.DisplaySnapshot, Session.FeatureCursor, Session.Generation, Session.HistoryUsage, Assets.Count, Session.IsDirty);
        internal void AssertSame(CapturedState before)
        {
            Assert.Same(before.Author, Session.Snapshot); Assert.Same(before.Display, Session.DisplaySnapshot); Assert.Equal(before.Cursor, Session.FeatureCursor);
            Assert.Equal(before.Generation, Session.Generation); Assert.Equal(before.History, Session.HistoryUsage); Assert.Equal(before.Assets, Assets.Count); Assert.Equal(before.Dirty, Session.IsDirty);
        }
        internal async Task<JsonElement> Call(string json, CancellationToken token = default)
        {
            using var result = JsonDocument.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(), "cad_history", json), token)); return result.RootElement.Clone();
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); }
    }
}
