using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentNativeSelectionRetentionTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void ActualDrawingReadSurvivesTheRecordedLaterQueryPressure(bool aggressive)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures/AgentContext/NativeSelectionRetention/before-request30.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path)); var root = fixture.RootElement;
        var messages = root.GetProperty("Messages").Deserialize<AiChatMessage[]>()!;
        var tools = root.GetProperty("Tools").Deserialize<AiToolDefinition[]>()!;
        string id = root.GetProperty("NativeReadToolCallId").GetString()!;
        string original = JsonSerializer.Serialize(messages);
        var context = AgentRequestContextBuilder.Build(root.GetProperty("SystemPrompt").GetString()!, messages, tools, 8192, aggressive);
        var receipt = Assert.Single(context.Messages, m => m.Role == AiChatRole.Tool && m.ToolCallId == id);
        using var result = JsonDocument.Parse(receipt.Content!);
        var page = result.RootElement;
        var rows = page.GetProperty("items").EnumerateArray().ToList();
        if (page.TryGetProperty("contextPage", out var metadata) && metadata.TryGetProperty("completeSourceItems", out var additional))
            rows.AddRange(additional.EnumerateArray().Select(r => r.GetProperty("item")));
        Assert.Contains(rows, r => r.TryGetProperty("geometry", out var kind) && kind.GetString() == "CylinderFace" &&
            JsonSerializer.Serialize(r) == JsonSerializer.Serialize(root.GetProperty("ExpectedCylinderItem")));
        Assert.Equal(original, JsonSerializer.Serialize(messages));
        Assert.Equal(messages.Last(m => m.Role == AiChatRole.User).Content, context.Messages.Last(m => m.Role == AiChatRole.User).Content);
        Assert.Equal(1024, context.MaxOutputTokens);
        Assert.True(context.EstimatedPromptTokens <= 8192 - 1024 - (aggressive ? 1280 : 640));
        AssertPairs(context.Messages);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void SourceReadAndEarlierCommittedWriteBothSurvivePressureWithoutChangingHistory(bool aggressive)
    {
        var (messages, tools, system, source) = Captured();
        int user = messages.FindLastIndex(m => m.Role == AiChatRole.User);
        messages.InsertRange(user + 1, [AiChatMessage.Assistant(null, [new("committed", "cad_drawing", "{}")]),
            AiChatMessage.Tool("committed", "{\"success\":true,\"changed\":true,\"id\":\"actual-object\"}")]);
        var original = JsonSerializer.Serialize(messages);
        var context = AgentRequestContextBuilder.Build(system, messages, tools, 8192, aggressive);
        Assert.Contains(context.Messages, m => m.ToolCallId == source);
        Assert.Contains(context.Messages, m => m.ToolCallId == "committed");
        Assert.Equal(original, JsonSerializer.Serialize(messages)); AssertPairs(context.Messages);
        Assert.True(context.EstimatedPromptTokens <= 8192 - 1024 - (aggressive ? 1280 : 640));
    }

    [Theory, InlineData("write"), InlineData("workspace"), InlineData("next-user"), InlineData("foreign-state"), InlineData("foreign-document"), InlineData("inactive-status")]
    public void SupersededReadDoesNotGainProtection(string change)
    {
        var (messages, _, _, source) = Captured();
        if (change == "next-user") messages.Add(AiChatMessage.User("A different request"));
        else if (change == "inactive-status") Exchange(messages, new("inactive-status", "cad_status", "{}"), "{\"active\":false}");
        else if (change is "write" or "workspace")
            Exchange(messages, new("later-change", "cad_edit", "{}"), change == "write" ? "{\"success\":true,\"changed\":true}" : "{\"success\":true,\"workspaceChanged\":true}");
        else
        {
            using var read = JsonDocument.Parse(messages.Single(m => m.ToolCallId == source).Content!);
            string document = change == "foreign-document" ? Guid.NewGuid().ToString() : read.RootElement.GetProperty("documentId").GetString()!;
            string state = change == "foreign-state" ? Guid.NewGuid().ToString() : read.RootElement.GetProperty("stateId").GetString()!;
            Exchange(messages, new("later-state", "cad_status", "{}"), JsonSerializer.Serialize(new { active = true, documentId = document, stateId = state }));
        }
        Assert.Null(AgentNativeSelectionReceipt.FindLatestCompleteExchange(messages));
    }

    [Theory, InlineData("failed"), InlineData("non-bool"), InlineData("action"), InlineData("documentId"), InlineData("stateId"),
        InlineData("offset"), InlineData("limit"), InlineData("total"), InlineData("hasMore"), InlineData("bodyId"), InlineData("revision"),
        InlineData("path"), InlineData("unknown"), InlineData("duplicate-field"), InlineData("other-tool"), InlineData("orphan"),
        InlineData("partial-batch"), InlineData("duplicate-id"), InlineData("reused-older-id")]
    public void UnprovedOrUnpairedReadCannotPinAnExchange(string fault)
    {
        var (messages, _, _, source) = Captured();
        var call = messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).Single(c => c.Id == source);
        var receipt = JsonNode.Parse(messages.Single(m => m.ToolCallId == source).Content!)!.AsObject();
        var args = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
        if (fault is "failed" or "non-bool") receipt["success"] = fault == "failed" ? JsonValue.Create(false) : JsonValue.Create("true");
        else if (fault == "action") receipt["action"] = "create";
        else if (fault is "documentId" or "stateId") receipt[fault] = Guid.NewGuid().ToString();
        else if (fault is "offset" or "limit" or "total") receipt[fault] = receipt[fault]!.GetValue<int>() + 1;
        else if (fault == "hasMore") receipt["hasMore"] = true;
        else if (fault is "bodyId" or "revision") args[fault] = Guid.NewGuid().ToString();
        else if (fault == "path") args["path"] = "other-source";
        else if (fault == "unknown") args["id"] = Guid.NewGuid().ToString();
        call = call with { ArgumentsJson = args.ToJsonString(), Name = fault == "other-tool" ? "unrelated" : call.Name };
        var history = new List<AiChatMessage> { AiChatMessage.User("Inspect native sources") };
        if (fault == "orphan") history.Add(AiChatMessage.Tool(source, receipt.ToJsonString()));
        else
        {
            IReadOnlyList<AiToolCall> calls = fault switch { "partial-batch" => [call, new("missing", "cad_status", "{}")], "duplicate-id" => [call, call], _ => [call] };
            history.Add(AiChatMessage.Assistant(null, calls));
            string content = receipt.ToJsonString();
            if (fault == "duplicate-field") content = content[..^1] + ",\"success\":true}";
            history.Add(AiChatMessage.Tool(source, content));
        }
        if (fault == "reused-older-id")
            history.InsertRange(0, [AiChatMessage.User("Older turn"), AiChatMessage.Assistant(null, [call]), AiChatMessage.Tool(source, receipt.ToJsonString())]);
        Assert.Null(AgentNativeSelectionReceipt.FindLatestCompleteExchange(history));
    }

    [Fact]
    public void AReadAfterARealChangeAndSameStateStatusRemainsEligible()
    {
        var (messages, _, _, source) = Captured();
        int user = messages.FindLastIndex(m => m.Role == AiChatRole.User);
        messages.InsertRange(user + 1, [AiChatMessage.Assistant(null, [new("earlier-change", "cad_edit", "{}")]),
            AiChatMessage.Tool("earlier-change", "{\"success\":true,\"changed\":true}")]);
        using var read = JsonDocument.Parse(messages.Single(m => m.ToolCallId == source).Content!);
        Exchange(messages, new("later-status", "cad_status", "{}"), JsonSerializer.Serialize(new { active = true,
            documentId = read.RootElement.GetProperty("documentId").GetString(), stateId = read.RootElement.GetProperty("stateId").GetString() }));
        Assert.Equal(source, AgentNativeSelectionReceipt.FindLatestCompleteExchange(messages));
    }

    [Fact]
    public void OversizedWholeExchangeReportsOmissionWithoutCreatingAPartialReceiptOrCall()
    {
        var (messages, _, _, source) = Captured();
        var exchange = messages.Single(m => m.Role == AiChatRole.Assistant && m.ToolCalls?.Any(c => c.Id == source) == true);
        messages = [messages.Last(m => m.Role == AiChatRole.User), exchange with { Content = new string('x', 60000) },
            messages.Single(m => m.ToolCallId == source), AiChatMessage.Assistant("later answer")];
        var context = AgentRequestContextBuilder.Build("CAD assistant", messages, [], 4096);
        Assert.DoesNotContain(context.Messages, m => m.ToolCallId == source);
        Assert.Contains(context.Messages, m => m.Role == AiChatRole.System && m.Content == AgentNativeSelectionReceipt.OmittedExchangeMarker);
        AssertPairs(context.Messages); Assert.True(context.EstimatedPromptTokens <= 4096 - 512 - 640);
    }

    [Fact]
    public void OversizedNativeRowsNeverBecomePartialSelectors()
    {
        var (messages, _, _, source) = Captured();
        var call = messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).Single(c => c.Id == source);
        var page = JsonNode.Parse(messages.Single(m => m.ToolCallId == source).Content!)!.AsObject();
        foreach (var row in page["items"]!.AsArray()) row!["diagnostic"] = new string('x', 6000);
        string receipt = page.ToJsonString();
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, 2400, out var compacted, call));
        using var result = JsonDocument.Parse(compacted);
        Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(result.RootElement.GetProperty("contextPage").TryGetProperty("completeSourceItems", out _));
        Assert.Contains("no selection is available", result.RootElement.GetProperty("contextPage").GetProperty("message").GetString());
        Assert.InRange(compacted.Length, 1, 2400);
    }

    [Theory, InlineData(2400), InlineData(6000)]
    public void WholeSourceRowsAndOriginalOmittedReadNavigationCoexistWithinTheBudget(int budget)
    {
        var (messages, _, _, source) = Captured();
        var call = messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).Single(c => c.Id == source);
        string receipt = messages.Single(m => m.ToolCallId == source).Content!;
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, budget, out var compacted, call));
        using var full = JsonDocument.Parse(receipt); using var result = JsonDocument.Parse(compacted);
        var context = result.RootElement.GetProperty("contextPage");
        var hint = Assert.Single(context.GetProperty("omittedGeometryReads").EnumerateArray(), h => h.GetProperty("geometry").GetString() == "CylinderFace");
        int offset = hint.GetProperty("offset").GetInt32();
        var actual = full.RootElement.GetProperty("items")[offset];
        Assert.Equal("CylinderFace", actual.GetProperty("geometry").GetString());
        Assert.False(hint.TryGetProperty("selection", out _));
        var extra = Assert.Single(context.GetProperty("completeSourceItems").EnumerateArray(), r => r.GetProperty("item").GetProperty("geometry").GetString() == "CylinderFace");
        Assert.Equal(offset, extra.GetProperty("offset").GetInt32());
        Assert.Equal(JsonSerializer.Serialize(actual), JsonSerializer.Serialize(extra.GetProperty("item")));
        Assert.InRange(compacted.Length, 1, budget);
    }

    private static (List<AiChatMessage> Messages, AiToolDefinition[] Tools, string System, string Source) Captured()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/AgentContext/NativeSelectionRetention/before-request30.json")));
        var root = fixture.RootElement;
        return (root.GetProperty("Messages").Deserialize<List<AiChatMessage>>()!, root.GetProperty("Tools").Deserialize<AiToolDefinition[]>()!,
            root.GetProperty("SystemPrompt").GetString()!, root.GetProperty("NativeReadToolCallId").GetString()!);
    }

    private static void Exchange(List<AiChatMessage> history, AiToolCall call, string receipt)
    { history.Add(AiChatMessage.Assistant(null, [call])); history.Add(AiChatMessage.Tool(call.Id, receipt)); }

    private static void AssertPairs(IReadOnlyList<AiChatMessage> messages)
    {
        var calls = messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).ToArray();
        var receipts = messages.Where(m => m.Role == AiChatRole.Tool).ToArray();
        Assert.All(calls, c => Assert.Single(receipts, r => r.ToolCallId == c.Id));
        Assert.All(receipts, r => Assert.Single(calls, c => c.Id == r.ToolCallId));
    }
}
