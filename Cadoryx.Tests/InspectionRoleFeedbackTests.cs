using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.AI.Contracts;
using Cadoryx.Agent;
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
public sealed class InspectionRoleFeedbackTests
{
    [Theory]
    [InlineData("omitted")][InlineData("null")][InlineData("empty")][InlineData("document")]
    [InlineData("part")][InlineData("unknown")][InlineData("number")][InlineData("oversized")]
    public async Task InvalidContractIdExplainsItsTypeRoleWithoutReadingOrSelectingAModel(string fault)
    {
        await using var f = await Fixture.Create();
        var request = new JsonObject { ["area"] = "model_contract" };
        if (fault != "omitted") request["id"] = fault switch
        {
            "null" => null, "empty" => "", "document" => f.Session.Snapshot.Id.ToString(),
            "part" => f.Part.ToString(), "unknown" => "not-a-model-type",
            "number" => JsonValue.Create(42), "oversized" => new string('x', 257), _ => throw new ArgumentException()
        };
        var original = request.ToJsonString();
        using var result = await f.Query(original);
        var r = result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());
        var message = r.GetProperty("message").GetString()!;
        Assert.Contains("id=<model type>", message);
        Assert.Contains("area=model_contract", message);
        Assert.Contains("not a document, part or feature GUID", message);
        Assert.Contains("extrude", message); Assert.Contains("hole", message);
        Assert.True(message.Length < 1000); Assert.False(r.TryGetProperty("items", out _));
        Assert.Equal(original, request.ToJsonString());
    }

    [Theory]
    [InlineData("omitted")][InlineData("part")][InlineData("document")][InlineData("feature")]
    public async Task TimelineNeverPromotesAnIdFilterIntoAnImplicitPartChoice(string fault)
    {
        await using var f = await Fixture.Create();
        var request = new JsonObject { ["area"] = "timeline" };
        if (fault != "omitted") request["id"] = fault switch
        {
            "part" => f.Part.ToString(), "document" => f.Session.Snapshot.Id.ToString(),
            "feature" => f.Feature.ToString(), _ => throw new ArgumentException()
        };
        using var result = await f.Query(request.ToJsonString());
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        var message = result.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("partId, not id", message);
        Assert.Contains("area=parts", message); Assert.Contains("items[].id into partId", message);
        Assert.Contains("optional feature filter", message);
        Assert.False(result.RootElement.TryGetProperty("items", out _));
        Assert.False(request.ContainsKey("partId"));
    }

    [Theory]
    [InlineData("assembly")][InlineData("document")][InlineData("body")][InlineData("absent")]
    public async Task WrongPartRoleIsRejectedWithoutFallingBackToTheOnlyExistingPart(string fault)
    {
        await using var f = await Fixture.Create();
        var partId = fault switch
        {
            "assembly" => f.Session.Snapshot.RootAssemblyId.ToString(),
            "document" => f.Session.Snapshot.Id.ToString(), "body" => f.Body.ToString(),
            "absent" => Guid.NewGuid().ToString(), _ => throw new ArgumentException()
        };
        using var result = await f.Query(JsonSerializer.Serialize(new { area = "timeline", partId }));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        var message = result.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("not a part definition", message); Assert.Contains("explicitly choose a part", message);
        Assert.Contains("area=parts", message);
    }

    [Fact]
    public async Task CallerUsesExplicitDiscoveryAndCorrectedRolesThenGetsTheOriginalReadOnlyResults()
    {
        await using var f = await Fixture.Create();
        using (var bad = await f.Query("""{"area":"timeline"}"""))
            Assert.False(bad.RootElement.GetProperty("success").GetBoolean());
        using var parts = await f.Query("""{"area":"parts"}""");
        Assert.True(parts.RootElement.GetProperty("success").GetBoolean());
        var chosen = parts.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString();
        Assert.Equal(f.Part.ToString(), chosen);
        using var timeline = await f.Query(JsonSerializer.Serialize(new { area = "timeline", partId = chosen, id = f.Feature.ToString() }));
        Assert.True(timeline.RootElement.GetProperty("success").GetBoolean());
        var feature = timeline.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(f.Feature.ToString(), feature.GetProperty("id").GetString());
        Assert.Equal(chosen, feature.GetProperty("partId").GetString());
        using var absent = await f.Query(JsonSerializer.Serialize(new { area = "timeline", partId = chosen, id = Guid.NewGuid().ToString() }));
        Assert.True(absent.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(absent.RootElement.GetProperty("items").EnumerateArray());
        foreach (var type in new[] { "extrude", "revolve", "boolean", "hole", "repetition", "hole_pattern", "extrusion_pattern",
            "sweep", "loft", "shell", "draft", "solid_offset", "planar_push_pull", "defeature", "contour_local", "assembly_pattern" })
        {
            using var contract = await f.Query(JsonSerializer.Serialize(new { area = "model_contract", id = type }));
            Assert.True(contract.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Theory]
    [InlineData("unknown-model")][InlineData("duplicate-model")][InlineData("unknown-timeline")][InlineData("duplicate-timeline")]
    public async Task OriginalUnknownAndDuplicateFieldRefusalStillPrecedesRoleGuidance(string fault)
    {
        await using var f = await Fixture.Create();
        var input = fault switch
        {
            "unknown-model" => """{"area":"model_contract","id":"hole","partId":"unused"}""",
            "duplicate-model" => """{"area":"model_contract","id":"hole","id":"extrude"}""",
            "unknown-timeline" => """{"area":"timeline","type":"hole"}""",
            "duplicate-timeline" => "{\"area\":\"timeline\",\"partId\":\"" + f.Part + "\",\"partId\":\"" + f.Part + "\"}",
            _ => throw new ArgumentException()
        };
        using var result = await f.Query(input);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        var message = result.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("Unknown or duplicate argument", message);
        Assert.DoesNotContain("id=<model type>", message); Assert.DoesNotContain("items[].id into partId", message);
    }

    [Theory, InlineData("timeline"), InlineData("model_contract")]
    public async Task PreCancellationNeverReturnsRoleGuidance(string area)
    {
        await using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Query(JsonSerializer.Serialize(new { area }), cancel.Token));
    }

    [Theory, InlineData("timeline"), InlineData("model_contract")]
    public async Task DetachedDocumentCannotRevealRoleGuidance(string area)
    {
        await using var f = await Fixture.Create(); f.Document.Detach();
        using var result = await f.Query(JsonSerializer.Serialize(new { area }));
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("No active document.", result.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void RoleFeedbackChangesNoDefaultSchemaOrContextBudget()
    {
        var tools = new CadAgentToolset(new CadCommandLineService(), null);
        var inspect = tools.ToolDefinitions.Single(x => x.Name == "cad_inspect");
        Assert.Equal(new[] { "area" }, inspect.Parameters.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(inspect.Parameters.GetProperty("additionalProperties").GetBoolean());
        foreach (var aggressive in new[] { false, true })
        {
            var prompt = "Inspect timeline and model_contract";
            var context = AgentRequestContextBuilder.Build("CAD assistant", [AiChatMessage.User(prompt)],
                tools.SelectTools(prompt, aggressive), 8192, aggressive);
            Assert.Equal(12, context.Tools.Count); Assert.Equal(1024, context.MaxOutputTokens);
            Assert.True(context.EstimatedPromptTokens <= 8192 - 1024 - (aggressive ? 1280 : 640));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new();
        internal CadDocumentSession Session { get; }
        internal CadDocumentViewModel Document { get; }
        private CadAgentToolset Tools { get; }
        internal DefinitionId Part => Session.Snapshot.Bodies.Values.Single().PartId;
        internal BodyId Body => Session.Snapshot.Bodies.Keys.Single();
        internal FeatureId Feature => Session.Snapshot.Features.Keys.Single();
        private Fixture()
        {
            var kernel = new OcctGeometryKernel();
            Session = new(DocumentSnapshot.Create("Inspection roles"), Assets, kernel, new InlineSessionDispatcher());
            Document = new(Session, kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try { await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), "Box")); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task<JsonDocument> Query(string input, CancellationToken token = default)
        {
            var before = Session.Snapshot; var generation = Session.Generation;
            var count = Assets.Count; var bytes = Assets.SizeBytes; var undo = Session.CanUndo; var redo = Session.CanRedo;
            var cursor = Session.FeatureCursor; var items = Document.Selection.Items; var occurrence = Document.Selection.Occurrence;
            try { return JsonDocument.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(), "cad_inspect", input), token)); }
            finally
            {
                Assert.Same(before, Session.Snapshot); Assert.Equal(generation, Session.Generation);
                Assert.Equal(count, Assets.Count); Assert.Equal(bytes, Assets.SizeBytes);
                Assert.Equal(undo, Session.CanUndo); Assert.Equal(redo, Session.CanRedo); Assert.Equal(cursor, Session.FeatureCursor);
                Assert.Equal(items, Document.Selection.Items); Assert.Equal(occurrence, Document.Selection.Occurrence);
            }
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); Assert.Equal(0, Assets.SizeBytes); }
    }
}
