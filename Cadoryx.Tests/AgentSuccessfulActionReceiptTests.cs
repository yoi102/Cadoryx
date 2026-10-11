using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
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
public sealed class AgentSuccessfulActionReceiptTests
{
    private const string Request = "Create exactly one sheet; keep the successful ID while inspecting sources.";
    private const string Receipt = "{\"success\":true,\"changed\":true,\"id\":\"actual-created-id\"}";

    [Theory][InlineData(false)][InlineData(true)]
    public void LongReadsRetainTheLatestSuccessfulExchangeAndEveryOriginalTool(bool aggressive)
    {
        var history = Start(); Exchange(history, "mutation", Receipt); Reads(history);
        var original = JsonSerializer.Serialize(history);
        var toolset = new CadAgentToolset(new CadCommandLineService(), null);
        Assert.Equal(12, toolset.ToolDefinitions.Count);
        var tools = toolset.SelectTools(Request, aggressive);
        var shortContext = AgentRequestContextBuilder.Build("CAD assistant", [AiChatMessage.User(Request)], tools, 8192, aggressive);
        Assert.Equal(12, shortContext.Tools.Count);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, tools, 8192, aggressive);
        // Frozen pre-change DLL comparison proves these original long-history
        // selections; the default 12-tool guard above remains intact.
        Assert.Equal(OriginalLongHistoryTools(aggressive), context.Tools.Select(t => t.Name));
        Assert.Equal(Receipt, Assert.Single(context.Messages, m => m.ToolCallId == "mutation").Content);
        Assert.Contains(context.Messages, m => m.ToolCallId == "read-11");
        Assert.DoesNotContain(context.Messages, m => m.ToolCallId == "read-0");
        Assert.Equal(original, JsonSerializer.Serialize(history)); Check(context, 8192, aggressive);
    }

    [Theory]
    [InlineData("{\"success\":true,\"workspaceChanged\":true,\"sessionId\":\"actual-session\"}")]
    [InlineData("{\"success\":true,\"changed\":false,\"workspaceChanged\":true}")]
    public void ActualWorkspaceChangeFlagsHaveTheSameBoundedRetention(string receipt)
    {
        var history = Start(); Exchange(history, "mutation", receipt); Reads(history);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096);
        Assert.Equal(receipt, Assert.Single(context.Messages, m => m.ToolCallId == "mutation").Content); Check(context, 4096, false);
    }

    [Theory]
    [InlineData("{\"success\":false,\"changed\":true}")]
    [InlineData("{\"success\":true,\"changed\":false}")]
    [InlineData("{\"success\":true,\"published\":true}")]
    [InlineData("{\"success\":\"true\",\"changed\":true}")]
    [InlineData("{\"success\":true,\"changed\":\"true\"}")]
    [InlineData("{\"success\":true,\"changed\":true,\"workspaceChanged\":1}")]
    [InlineData("{\"success\":true,\"changed\":true,\"success\":false}")]
    [InlineData("{\"result\":{\"success\":true,\"changed\":true}}")]
    [InlineData("{\"success\":true,\"changed\":true")]
    [InlineData("I successfully created an object; changed=true")]
    public void FailuresNoOpsMalformedAndClaimedSuccessCannotPinAnExchange(string receipt)
    {
        var history = Start(); Exchange(history, "unproven", receipt, "create_box"); Reads(history);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096);
        Assert.DoesNotContain(context.Messages, m => m.ToolCallId == "unproven");
        Assert.DoesNotContain(context.Messages, m => m.Content == AgentSuccessfulActionReceipt.OmittedExchangeMarker); Check(context, 4096, false);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void MultiToolRoundKeepsEveryPairedResultAndOriginalOrder(bool aggressive)
    {
        var history = Start();
        history.Add(AiChatMessage.Assistant("actual call round", [new("change", "cad_drawing", "{}"), new("sibling", "cad_status", "{}")]));
        history.Add(AiChatMessage.Tool("change", Receipt)); history.Add(AiChatMessage.Tool("sibling", "actual sibling receipt")); Reads(history);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096, aggressive);
        var results = context.Messages.Where(m => m.ToolCallId is "change" or "sibling").ToArray();
        Assert.Equal(new[] { "change", "sibling" }, results.Select(m => m.ToolCallId));
        Assert.Equal("actual sibling receipt", results[1].Content); Check(context, 4096, aggressive);
    }

    [Theory][InlineData("missing")][InlineData("reused")][InlineData("duplicate-result")][InlineData("no-user")]
    public void IncompleteOrAmbiguousExchangesCannotBePinned(string invalid)
    {
        var history = invalid == "no-user" ? new List<AiChatMessage>() : Start();
        history.Add(AiChatMessage.Assistant(null, [new("change", "cad_drawing", "{}"), new("missing", "cad_status", "{}")]));
        history.Add(AiChatMessage.Tool("change", Receipt));
        if (invalid != "missing") history.Add(AiChatMessage.Tool("missing", "actual read"));
        if (invalid == "duplicate-result") history.Add(AiChatMessage.Tool("change", Receipt));
        if (invalid == "reused") Exchange(history, "change", "{\"success\":false}");
        Assert.Null(AgentSuccessfulActionReceipt.FindLatestCompleteExchange(history));
    }

    [Fact]
    public void NewUserTurnDoesNotPermanentlyPinAnOldMutationOrAddAWarning()
    {
        var history = Start(); Exchange(history, "old", Receipt); Reads(history); history.Add(AiChatMessage.User("New read-only request"));
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096);
        Assert.Equal("New read-only request", context.Messages[^1].Content);
        Assert.DoesNotContain(context.Messages, m => m.ToolCallId == "old" || m.Content == AgentSuccessfulActionReceipt.OmittedExchangeMarker); Check(context, 4096, false);
    }

    [Fact]
    public void OnlyTheLatestGenuineMutationIsReservedWithoutReorderingHistory()
    {
        var history = Start(); Exchange(history, "old", Receipt); Exchange(history, "latest", Receipt.Replace("actual-created-id", "latest-id")); Reads(history);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096);
        Assert.Contains(context.Messages, m => m.ToolCallId == "latest" && m.Content!.Contains("latest-id"));
        Assert.DoesNotContain(context.Messages, m => m.ToolCallId == "old"); Check(context, 4096, false);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void ExchangeThatCannotFitIsOmittedAsAWholeWithTruthfulContextMetadata(bool aggressive)
    {
        var history = Start();
        history.Add(AiChatMessage.Assistant(new string('a', 22000), [new("large", "cad_drawing", "{}"), new("sibling", "cad_status", "{}")]));
        history.Add(AiChatMessage.Tool("large", Receipt)); history.Add(AiChatMessage.Tool("sibling", "real sibling"));
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096, aggressive);
        Assert.DoesNotContain(context.Messages, m => m.Role == AiChatRole.Tool);
        Assert.Contains(context.Messages, m => m.Role == AiChatRole.System && m.Content == AgentSuccessfulActionReceipt.OmittedExchangeMarker);
        Assert.Equal(4, history.Count); Check(context, 4096, aggressive);
    }

    [Theory][InlineData(false, 6000)][InlineData(true, 2400)]
    public void OversizedReceiptsKeepTheOriginalCharacterLimitAndExplicitTruncation(bool aggressive, int maximum)
    {
        var receipt = JsonSerializer.Serialize(new { success = true, changed = true, id = "actual-id", detail = new string('x', 20000) });
        var history = Start(); Exchange(history, "change", receipt); Reads(history);
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 8192, aggressive);
        var retained = Assert.Single(context.Messages, m => m.ToolCallId == "change");
        Assert.InRange(retained.Content!.Length, 1, maximum); Assert.Contains("[tool result truncated", retained.Content);
        Assert.Equal(receipt, history[2].Content); Check(context, 8192, aggressive);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealNativeSheetReceiptSurvivesActualSourceReadsWithoutAdditionalEdits(bool aggressive)
    {
        var assets = new MemoryAssetStore(); var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Retained sheet"), assets, kernel, new InlineSessionDispatcher());
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog()); var tools = new CadAgentToolset(new CadCommandLineService(), document);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2, 3, 4, RigidTransform3d.Identity), "Native box"));
            var body = session.Snapshot.Bodies.Values.Single(); var originalGeometry = body.Geometry; var beforeSheet = session.Snapshot;
            var history = Start();
            var create = new AiToolCall("sheet-created", "cad_drawing", JsonSerializer.Serialize(new { action = "create", type = "sheet",
                documentId = session.Snapshot.Id.ToString(), stateId = session.Snapshot.StateId.ToString(),
                authorJson = "{\"name\":\"One sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"One sheet\",\"standard\":\"ISO\"}" }));
            var receipt = await tools.ExecuteAsync(create, default); using var parsed = JsonDocument.Parse(receipt);
            Assert.True(parsed.RootElement.GetProperty("success").GetBoolean()); Assert.True(parsed.RootElement.GetProperty("changed").GetBoolean());
            var sheet = parsed.RootElement.GetProperty("id").GetGuid(); history.Add(AiChatMessage.Assistant(null, [create])); history.Add(AiChatMessage.Tool(create.Id, receipt));
            var snapshot = session.Snapshot;
            for (int i = 0; i < 12; i++)
            {
                var read = new AiToolCall("source-" + i, "cad_drawing", JsonSerializer.Serialize(new { action = "inspect_sources",
                    documentId = snapshot.Id.ToString(), stateId = snapshot.StateId.ToString(),
                    path = snapshot.EnumerateOccurrences().Single().Path.ToString(), bodyId = body.Id.ToString(), revision = body.Geometry.Revision.ToString(), offset = 0, limit = 50 }));
                var result = await tools.ExecuteAsync(read, default); using var check = JsonDocument.Parse(result);
                Assert.True(check.RootElement.GetProperty("success").GetBoolean()); history.Add(AiChatMessage.Assistant(null, [read])); history.Add(AiChatMessage.Tool(read.Id, result));
            }
            var originalHistory = JsonSerializer.Serialize(history);
            var context = AgentRequestContextBuilder.Build("CAD assistant", history, tools.SelectTools(Request, aggressive), 8192, aggressive);
            Assert.Equal(OriginalLongHistoryTools(aggressive), context.Tools.Select(t => t.Name));
            Assert.Equal(receipt, Assert.Single(context.Messages, m => m.ToolCallId == create.Id).Content);
            Assert.Contains(sheet.ToString(), context.Messages.Single(m => m.ToolCallId == create.Id).Content);
            Assert.DoesNotContain(context.Messages, m => m.ToolCallId == "source-0"); Assert.Same(snapshot, session.Snapshot);
            Assert.Single(snapshot.DrawingSheets); Assert.Equal(originalGeometry, snapshot.Bodies.Values.Single().Geometry);
            Assert.Equal(originalHistory, JsonSerializer.Serialize(history)); Check(context, 8192, aggressive);
            await session.UndoAsync(); Assert.Same(beforeSheet, session.Snapshot); await session.RedoAsync(); Assert.Same(snapshot, session.Snapshot);
        }
        finally { document.Detach(); await session.DisposeAsync(); Assert.Equal(0, assets.Count); }
    }

    private static List<AiChatMessage> Start() => [AiChatMessage.User(Request)];
    private static string[] OriginalLongHistoryTools(bool aggressive) => aggressive
        ? ["cad_status", "cad_inspect", "cad_command", "cad_select", "cad_workspace", "cad_file", "cad_drawing", "cad_history", "cad_edit"]
        : ["cad_status", "cad_inspect", "cad_command", "cad_select", "cad_workspace", "cad_file", "cad_drawing", "cad_history", "cad_edit", "cad_parameter"];
    private static void Exchange(List<AiChatMessage> history, string id, string receipt, string name = "cad_drawing")
    {
        history.Add(AiChatMessage.Assistant(null, [new(id, name, "{}") ])); history.Add(AiChatMessage.Tool(id, receipt));
    }
    private static void Reads(List<AiChatMessage> history)
    {
        for (int i = 0; i < 12; i++) Exchange(history, "read-" + i, JsonSerializer.Serialize(new { success = true, detail = new string('x', 5000) }), "cad_inspect");
    }
    private static void Check(AgentRequestContext context, int window, bool aggressive)
    {
        Assert.Equal("CAD assistant", context.Messages[0].Content); Assert.Contains(context.Messages, m => m.Role == AiChatRole.User && m.Content == Request || m.Content == "New read-only request");
        Assert.True(context.EstimatedPromptTokens <= window - context.MaxOutputTokens - (aggressive ? 1280 : 640));
        Assert.Equal(window / 8, context.MaxOutputTokens);
        var calls = context.Messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).ToArray();
        var results = context.Messages.Where(m => m.Role == AiChatRole.Tool).ToArray();
        Assert.All(calls, call => Assert.Single(results, result => result.ToolCallId == call.Id));
        Assert.All(results, result => Assert.Single(calls, call => call.Id == result.ToolCallId));
    }
}
