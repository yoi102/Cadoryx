using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class ParameterAgentTests
{
    [Fact]
    public async Task InspectedTargetsDriveNativeGeometryDrawingsUndoAndSavedExpressionsWithActualIds()
    {
        using var files = new TestFiles();
        await using var f = await Fixture.Create();
        var body = f.Session.Snapshot.Bodies.Values.Single();
        var sheet = TechnicalDrawingSheet.A4Landscape("Parameter drawing");
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id, Guid.NewGuid(), "Top", DrawingViewKind.Top, new(100, 100), 1));
        var created = await f.Edit("set_expression", new { name = "width", dimension = "Length", expression = "1 in" });
        var parameter = Guid.Parse(created.GetProperty("id").GetString()!);
        Assert.True(created.GetProperty("changed").GetBoolean());
        Assert.Equal(parameter.ToString(), created.GetProperty("createdIds")[0].GetString());
        var fields = await f.Inspect(new { area = "parameter_targets", id = body.Producer!.Value.ToString() });
        var target = fields.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("target").GetProperty("property").GetString() == "X").GetProperty("target");
        Assert.Equal("Feature", target.GetProperty("kind").GetString());
        Assert.False(target.TryGetProperty("curve", out _)); // The emitted target can be copied without inventing or pruning fields.
        var before = f.Session.Snapshot;
        var bound = await f.Edit("bind", new { parameterId = parameter, target });
        var binding = Guid.Parse(bound.GetProperty("id").GetString()!);
        var bindings = await f.Inspect(new { area = "parameter_bindings", id = binding });
        Assert.Equal("Length", bindings.GetProperty("items")[0].GetProperty("dimension").GetString());
        Assert.Equal("mm", bindings.GetProperty("items")[0].GetProperty("unit").GetString());
        var after = f.Session.Snapshot;
        Assert.Equal(25.4 * 3 * 4, after.Bodies[body.Id].Geometry.VolumeMm3, 7);
        Assert.Equal(after.Bodies[body.Id].Geometry.Revision, after.DrawingSheets[sheet.Id].Views.Single().Sources.Single().Revision);
        Assert.Equal(after.StateId.ToString(), bound.GetProperty("stateId").GetString());
        var unchanged = await f.Edit("bind", new { parameterId = parameter, target });
        Assert.False(unchanged.GetProperty("changed").GetBoolean()); Assert.Empty(unchanged.GetProperty("createdIds").EnumerateArray());
        Assert.Same(after, f.Session.Snapshot);
        await f.Session.UndoAsync(); Assert.Same(before, f.Session.Snapshot);
        await f.Session.RedoAsync(); Assert.Same(after, f.Session.Snapshot);
        var reference = await f.Edit("set_reference", new { name = "measured_width", dimension = "Length", target });
        var referenceId = Guid.Parse(reference.GetProperty("id").GetString()!);
        var inspected = await f.Inspect(new { area = "parameters" });
        Assert.Equal(25.4, inspected.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == referenceId).GetProperty("value").GetDouble(), 10);
        Assert.Equal("mm", inspected.GetProperty("units").GetProperty("length").GetString());
        Assert.False(Success(await f.EditResult("bind", new { parameterId = referenceId, target })));
        var noOp = await f.Edit("recompute"); Assert.False(noOp.GetProperty("changed").GetBoolean());
        var storage = new CadDocumentStorage(); var path = files.PathFor("agent-parameters.cadoryx");
        await f.Session.SaveAsync(storage, path);
        using var loaded = await storage.LoadAsync(path, f.Assets);
        Assert.Equal("1 in", loaded.Snapshot.Parameters.Parameters.Single(p => p.Id == parameter).Expression);
        Assert.Equal(binding, loaded.Snapshot.Parameters.Bindings.Single().Id); loaded.Snapshot.Validate();
        await f.Edit("unbind", new { id = binding });
        await f.Edit("delete", new { id = parameter });
        Assert.Equal(25.4 * 3 * 4, f.Session.Snapshot.Bodies[body.Id].Geometry.VolumeMm3, 7);
        Assert.Single(f.Session.Snapshot.Parameters.Parameters);
    }

    [Fact]
    public async Task PagedAuthoringUsesChronologyExactDatumSourcesAndAnExplicitRollbackPreview()
    {
        await using var f = await Fixture.Create("Zulu");
        var first = f.Session.Snapshot.Features.Values.Single(); var part = first.PartId;
        await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2, 5, RigidTransform3d.Identity), "Alpha", part));
        var datum = new CadDatumPlane(Guid.NewGuid(), part, "Top offset", DatumPlaneKind.Offset, DatumPlaneSource.Box(first.Id, BoxBoundary.ZMax), Value: 2);
        await f.Session.ExecuteAsync(new SetDatumsCommand(f.Session.Snapshot.Datums with { Planes = f.Session.Snapshot.Datums.Planes.Add(datum.Id, datum) }));
        await f.Edit("set_expression", new { name = "z", dimension = "Angle", expression = "90 deg" });
        await f.Edit("set_expression", new { name = "a", dimension = "Scalar", expression = "2" });
        var page = await f.Inspect(new { area = "parameters", limit = 1 });
        Assert.Equal(2, page.GetProperty("total").GetInt32()); Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal("a", page.GetProperty("items")[0].GetProperty("name").GetString());
        var second = await f.Inspect(new { area = "parameters", offset = 1, limit = 1 });
        Assert.Equal(Math.PI / 2, second.GetProperty("items")[0].GetProperty("value").GetDouble(), 12);
        Assert.Equal("rad", second.GetProperty("items")[0].GetProperty("unit").GetString());
        var timeline = await f.Inspect(new { area = "timeline", partId = part.ToString() });
        Assert.Equal("Zulu", timeline.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("Alpha", timeline.GetProperty("items")[1].GetProperty("name").GetString());
        var datums = await f.Inspect(new { area = "datums", id = datum.Id, partId = part.ToString() });
        var author = datums.GetProperty("items")[0].GetProperty("author");
        Assert.Equal("BoxFace", author.GetProperty("primary").GetProperty("kind").GetString());
        Assert.Equal(first.Result.Revision.Value, author.GetProperty("sourceRevisions")[0].GetGuid());
        Assert.False(author.GetProperty("isStale").GetBoolean());
        var before = f.Session.Snapshot; await f.Session.RollbackFeaturesAsync(part, 1);
        timeline = await f.Inspect(new { area = "timeline", partId = part.ToString() });
        Assert.True(timeline.GetProperty("authorState").GetBoolean()); Assert.True(timeline.GetProperty("rolledBack").GetBoolean());
        Assert.Equal(1, timeline.GetProperty("cursor").GetProperty("position").GetInt32());
        Assert.Equal(2, timeline.GetProperty("total").GetInt32()); Assert.Same(before, f.Session.Snapshot);
        Assert.False(Success(await f.EditResult("set_expression", new { name = "bad", dimension = "Length", expression = "10 mm" })));
        Assert.False(Success(await f.InspectResult(new { area = "timeline" })));
        Assert.False(Success(await f.InspectResult(new { area = "parameters", offset = -1 })));
        Assert.False(Success(await f.InspectResult(new { area = "datums", limit = 51 })));
        Assert.False(Success(await f.InspectResult(new { area = "timeline", partId = f.Session.Snapshot.RootAssemblyId.ToString() })));
        await f.Session.ReturnToHistoryEndAsync();
    }

    [Theory]
    [InlineData("stale")][InlineData("foreign")][InlineData("empty")][InlineData("type")]
    [InlineData("unknown-field")][InlineData("duplicate")][InlineData("enum")][InlineData("dimension")]
    [InlineData("cycle")][InlineData("negative")][InlineData("missing-id")][InlineData("oversized")]
    public async Task InvalidOrStaleParameterUpdatesPreserveStateHistoryAndAssets(string mutation)
    {
        await using var f = await Fixture.Create();
        var p = await f.Edit("set_expression", new { name = "width", dimension = "Length", expression = "2 mm" });
        var id = p.GetProperty("id").GetGuid();
        var feature = f.Session.Snapshot.Features.Values.Single();
        await f.Edit("bind", new { parameterId = id, target = new { kind = "Feature", owner = feature.Id.Value, property = "X" } });
        var args = f.Arguments("set_expression", new { id, name = "width", dimension = "Length", expression = "6 mm" });
        switch (mutation)
        {
            case "stale": args["stateId"] = Guid.NewGuid().ToString(); break;
            case "foreign": args["documentId"] = Guid.NewGuid().ToString(); break;
            case "empty": args["id"] = Guid.Empty.ToString(); break;
            case "type": args["expression"] = 42; break;
            case "unknown-field": args["force"] = true; break;
            case "enum": args["dimension"] = "1"; break;
            case "dimension": args["expression"] = "1 deg"; break;
            case "cycle": args["expression"] = "width + 1 mm"; break;
            case "negative": args["expression"] = "-1 mm"; break;
            case "missing-id": args["id"] = Guid.NewGuid().ToString(); break;
            case "oversized": args["expression"] = new string('1', 2049); break;
        }
        var before = f.Session.Snapshot; var history = f.Session.HistoryUsage; var assets = f.Assets.Count;
        var json = args.ToJsonString(); if (mutation == "duplicate") json = json[..^1] + ",\"action\":\"delete\"}";
        Assert.False(Success(await f.Tools.ExecuteAsync(new("1", "cad_parameter", json), default)));
        Assert.Same(before, f.Session.Snapshot); Assert.Equal(history, f.Session.HistoryUsage); Assert.Equal(assets, f.Assets.Count);
    }

    [Fact]
    public async Task DependencyDeletionDriverReplacementAndNoopUpdatesHaveExplicitResults()
    {
        await using var f = await Fixture.Create();
        var p = await f.Edit("set_expression", new { name = "base_width", dimension = "Length", expression = "2 mm" }); var id = p.GetProperty("id").GetGuid();
        var dependent = await f.Edit("set_expression", new { name = "total_width", dimension = "Length", expression = "base_width * 3" });
        Assert.False(Success(await f.EditResult("delete", new { id })));
        var before = f.Session.Snapshot;
        var noOp = await f.Edit("set_expression", new { id, name = "base_width", dimension = "Length", expression = "2 mm" });
        Assert.False(noOp.GetProperty("changed").GetBoolean()); Assert.Same(before, f.Session.Snapshot);
        var target = new { kind = "Feature", owner = f.Session.Snapshot.Features.Keys.Single().Value, property = "X" };
        await f.Edit("bind", new { parameterId = id, target }); before = f.Session.Snapshot;
        Assert.False(Success(await f.EditResult("bind", new { parameterId = dependent.GetProperty("id").GetGuid(), target })));
        Assert.False(Success(await f.EditResult("delete", new { id })));
        Assert.False(Success(await f.EditResult("set_expression", new { name = "BASE_WIDTH", dimension = "Length", expression = "1 mm" })));
        Assert.Same(before, f.Session.Snapshot);
    }

    [Theory][InlineData("extra")][InlineData("companion")][InlineData("owner")][InlineData("property")][InlineData("duplicate")]
    public async Task TargetSchemaRejectsHiddenCompanionsForeignFieldsAndDuplicates(string mutation)
    {
        await using var f = await Fixture.Create();
        var p = await f.Edit("set_expression", new { name = "width", dimension = "Length", expression = "2 mm" });
        var target = JsonSerializer.SerializeToNode(new { kind = "Feature", owner = f.Session.Snapshot.Features.Keys.Single().Value, property = "X" })!.AsObject();
        if (mutation == "extra") target["override"] = true;
        if (mutation == "companion") target["curve"] = Guid.NewGuid().ToString();
        if (mutation == "owner") target["owner"] = Guid.NewGuid().ToString();
        if (mutation == "property") target["property"] = "Name";
        var args = f.Arguments("bind", new { parameterId = p.GetProperty("id").GetGuid(), target }); var json = args.ToJsonString();
        if (mutation == "duplicate") json = json.Replace("\"property\":\"X\"", "\"property\":\"X\",\"property\":\"Y\"", StringComparison.Ordinal);
        var before = f.Session.Snapshot; var assets = f.Assets.Count;
        Assert.False(Success(await f.Tools.ExecuteAsync(new("1", "cad_parameter", json), default)));
        Assert.Same(before, f.Session.Snapshot); Assert.Equal(assets, f.Assets.Count);
    }

    [Fact]
    public async Task TerminalUsesSameAtomicAuthoringPathAndRejectsInvalidSyntax()
    {
        await using var f = await Fixture.Create(); var service = new CadCommandLineService();
        async Task<CadCommandResult> Run(string input) => await service.ExecuteAsync(input, new CadCommandContext(f.Document));
        var created = await Run("PARAM SET NEW width LENGTH \"6 mm\""); Assert.True(created.Success, created.Message);
        var id = JsonDocument.Parse(created.Message).RootElement.GetProperty("id").GetGuid();
        var feature = f.Session.Snapshot.Features.Values.Single();
        var bind = await Run($"PARAM BIND {id:D} Feature {feature.Id} X"); Assert.True(bind.Success, bind.Message);
        Assert.Equal(72, f.Session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3, 8);
        foreach (var command in new[] { "PARAMETERS", "BINDINGS", "TARGETS 0 1", "DATUMS", $"TIMELINE {feature.PartId} 0 1" })
            Assert.True((await Run(command)).Success, command);
        var before = f.Session.Snapshot;
        foreach (var command in new[] { "PARAM SET NEW bad 1 5", "PARAM SET NEW bad LENGTH \"1 mm\" EXTRA", "PARAMETERS -1 1", "TARGETS 0 51", $"PARAM BIND {id:D} Feature {feature.Id} X {Guid.NewGuid():D}", "PARAM DELETE width", "TIMELINE" })
            Assert.False((await Run(command)).Success, command);
        Assert.Same(before, f.Session.Snapshot);
        var stale = new CadCommandContext(f.Document); await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with { SpacingMm = 17 }));
        before = f.Session.Snapshot;
        Assert.False((await service.ExecuteAsync($"PARAM SET {id:D} width LENGTH \"9 mm\"", stale)).Success); Assert.Same(before, f.Session.Snapshot);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task InspectedSketchCompanionsAndIndexedCurveFieldsPassThroughTheToolUnchanged(bool exactCurve)
    {
        await using var f = await Fixture.Create(); var part = f.Session.Snapshot.Features.Values.Single().PartId;
        SketchDraft draft; ParameterTarget field;
        if (exactCurve)
        {
            var loop = SketchExactEndpointTests.Loop(part, 1, false); draft = loop.Draft;
            field = ParameterTarget.SketchCurve(draft.Value.Id, loop.Curve, "PoleY", 1);
        }
        else
        {
            draft = new(CadSketch.Create(part, "Rectangle", RigidTransform3d.Identity)); draft.AddRectangle(new(0, 0), new(10, 5));
            field = ParameterTarget.Sketch(draft.Value.Id, draft.Value.Constraints.OfType<OffsetXConstraint>().Single().Id);
        }
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value, new ManagedSketchConstraintSolver()));
        var inspect = await f.Inspect(new { area = "parameter_targets", id = draft.Value.Id.Value });
        var target = inspect.GetProperty("items").EnumerateArray().Single(item =>
            JsonSerializer.Deserialize<ParameterTarget>(item.GetProperty("target"), CadAuthoringInspection.JsonOptions) == field).GetProperty("target");
        Assert.True(target.TryGetProperty(exactCurve ? "curve" : "constraint", out _));
        if (exactCurve) Assert.Equal(1, target.GetProperty("index").GetInt32());
        var p = await f.Edit("set_expression", new { name = "sketch_value", dimension = "Length", expression = "7 mm" });
        var before = f.Session.Snapshot.Sketches[draft.Value.Id];
        await f.Edit("bind", new { parameterId = p.GetProperty("id").GetGuid(), target });
        var after = f.Session.Snapshot.Sketches[draft.Value.Id];
        Assert.Equal(7, ParameterTargets.Read(f.Session.Snapshot, field).Value, 8); Assert.NotEqual(before.Revision, after.Revision);
        if (exactCurve) Assert.Equal(SketchFixedCurveEndpoints.All(before).Single().Ends, SketchFixedCurveEndpoints.All(after).Single().Ends);
        f.Session.Snapshot.Validate();
    }

    [Theory][InlineData("cancel")][InlineData("stale")][InlineData("close")]
    public async Task LateNativeParameterRecomputeCannotCommitAfterCancellationStateChangeOrClose(string boundary)
    {
        var kernel = new DelayedKernel(); await using var f = await Fixture.Create(kernel: kernel);
        var p = await f.Edit("set_expression", new { name = "width", dimension = "Length", expression = "2 mm" }); var id = p.GetProperty("id").GetGuid();
        await f.Edit("bind", new { parameterId = id, target = new { kind = "Feature", owner = f.Session.Snapshot.Features.Keys.Single().Value, property = "X" } });
        var before = f.Session.Snapshot; var count = f.Assets.Count; using var cancel = new CancellationTokenSource();
        kernel.DelayNext = true;
        var pending = f.EditResult("set_expression", new { id, name = "width", dimension = "Length", expression = "6 mm" }, cancel.Token);
        await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(count + 1, f.Assets.Count);
        Task? closing = null;
        if (boundary == "cancel") cancel.Cancel();
        if (boundary == "stale") { await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with { SpacingMm = 17 })); before = f.Session.Snapshot; }
        if (boundary == "close") { f.Document.Detach(); closing = f.Session.DisposeAsync().AsTask(); }
        kernel.Release.TrySetResult();
        if (boundary == "stale") Assert.False(Success(await pending));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Same(before, f.Session.Snapshot);
        if (closing is not null) { await closing; Assert.Equal(0, f.Assets.Count); }
        else Assert.Equal(count, f.Assets.Count);
    }

    [Fact]
    public async Task CancellationAfterCommittedParameterEditStillReturnsSuccessfulActualState()
    {
        await using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource();
        f.Session.CommandCommitted += (_, _) => cancel.Cancel();
        var result = await f.Edit("set_expression", new { name = "width", dimension = "Length", expression = "6 mm" }, cancel.Token);
        Assert.True(cancel.IsCancellationRequested); Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal(f.Session.Snapshot.StateId.ToString(), result.GetProperty("stateId").GetString());
    }

    [Fact]
    public async Task FailedParameterGateSetupReleasesItsHeldNativeResult()
    {
        var kernel=new DelayedKernel();var f=await Fixture.Create(kernel:kernel);kernel.DelayNext=true;
        var before=f.Session.Snapshot;
        var pending=f.Session.ExecuteAsync(new RecomputeCommand(before.Features.Keys.Single(),new BoxRecipe(6,3,4,RigidTransform3d.Identity)));
        try
        {
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await f.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(0,f.Assets.Count);
        }
        finally{kernel.Release.TrySetResult();await f.DisposeAsync();}
    }

    private static bool Success(string json) => JsonDocument.Parse(json).RootElement.GetProperty("success").GetBoolean();
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IGeometryKernel kernel;
        internal MemoryAssetStore Assets { get; } = new();
        internal CadDocumentSession Session { get; }
        internal CadDocumentViewModel Document { get; }
        internal CadAgentToolset Tools { get; }
        private Fixture(IGeometryKernel kernel)
        {
            this.kernel=kernel;
            Session = new(DocumentSnapshot.Create("Parameter agent"), Assets, kernel, new InlineSessionDispatcher());
            Document = new(Session, kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal static async Task<Fixture> Create(string name = "Box", IGeometryKernel? kernel = null)
        {
            var fixture = new Fixture(kernel ?? new OcctGeometryKernel());
            await fixture.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), name)); return fixture;
        }
        internal JsonObject Arguments(string action, object? fields = null)
        {
            var args = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields)!.AsObject();
            args["action"] = action; args["documentId"] = Session.Snapshot.Id.ToString(); args["stateId"] = Session.Snapshot.StateId.ToString(); return args;
        }
        internal Task<string> EditResult(string action, object? fields = null, CancellationToken token = default) =>
            Tools.ExecuteAsync(new("1", "cad_parameter", Arguments(action, fields).ToJsonString()), token);
        internal async Task<JsonElement> Edit(string action, object? fields = null, CancellationToken token = default)
        { var json = await EditResult(action, fields, token); Assert.True(Success(json), json); using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone(); }
        internal Task<string> InspectResult(object fields) => Tools.ExecuteAsync(new("2", "cad_inspect", JsonSerializer.Serialize(fields)), default);
        internal async Task<JsonElement> Inspect(object fields)
        { var json = await InspectResult(fields); Assert.True(Success(json), json); using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone(); }
        public async ValueTask DisposeAsync()
        {
            Document.Detach();var closing=Session.DisposeAsync();
            if(kernel is DelayedKernel delayed)delayed.Release.TrySetResult();
            await closing;Assert.Equal(0,Assets.Count);
        }
    }

    private sealed class DelayedKernel : IGeometryKernel
    {
        private readonly OcctGeometryKernel native = new();
        internal bool DelayNext { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version => native.Version;
        public bool Supports(GeometryRecipe recipe) => native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken token = default)
        {
            var result = await native.EvaluateAsync(recipe, assets, CancellationToken.None);
            if (DelayNext) { DelayNext = false; Entered.TrySetResult(); await Release.Task; } return result;
        }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken token = default) => native.ImportAsync(path, assets, token);
        public Task ExportAsync(DocumentSnapshot doc, IAssetStore assets, string path, CancellationToken token = default) => native.ExportAsync(doc, assets, path, token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc, IAssetStore assets, string path, CadExportOptions options, CancellationToken token = default) => native.ExportAsync(doc, assets, path, options, token);
    }
}
