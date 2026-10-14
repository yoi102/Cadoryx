using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentOpaqueIdentityBudgetTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory,
        "Fixtures/AgentContext/OpaqueIdentityBudget", name);

    [Fact]
    public void ActualIdentityRichPageIsChargedAtLeastItsMeasuredModelTokenCount()
    {
        using var request = JsonDocument.Parse(File.ReadAllText(Fixture("request-019.json")));
        using var measurement = JsonDocument.Parse(File.ReadAllText(Fixture("tokenizer-measurement.json")));
        Assert.Equal(measurement.RootElement.GetProperty("source").GetProperty("sha256").GetString(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture("request-019.json")))));
        var row = measurement.RootElement.GetProperty("messages")[5];
        string content = request.RootElement.GetProperty("Messages")[5].GetProperty("Content").GetString()!;
        Assert.Equal(row.GetProperty("contentSha256").GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
        Assert.Equal(3002, row.GetProperty("actualContentTokens").GetInt32());
        Assert.True(AgentRequestContextBuilder.EstimateTextTokens(content) >= row.GetProperty("actualContentTokens").GetInt32());
    }

    [Theory, InlineData(false), InlineData(true)]
    public void ActualRequestFitsOriginalBudgetsWithoutOrphanedOrRewrittenSelections(bool aggressive)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture("request-019.json")));
        var root = fixture.RootElement;
        var messages = root.GetProperty("Messages").Deserialize<List<AiChatMessage>>()!;
        var tools = root.GetProperty("Tools").Deserialize<AiToolDefinition[]>()!;
        var originals = messages.Where(m => m.Role == AiChatRole.Tool).ToDictionary(m => m.ToolCallId!, m => m.Content!);
        var context = AgentRequestContextBuilder.Build(messages[0].Content!, messages.Skip(1).ToArray(), tools, 8192, aggressive);
        Assert.Equal(1024, context.MaxOutputTokens);
        Assert.True(context.EstimatedPromptTokens <= 8192 - 1024 - (aggressive ? 1280 : 640));
        Assert.Contains(context.Messages, m => m.Role == AiChatRole.User && m.Content == messages[1].Content);
        var calls = context.Messages.SelectMany(m => m.ToolCalls ?? []).ToArray();
        foreach (var receipt in context.Messages.Where(m => m.Role == AiChatRole.Tool))
        {
            Assert.Single(calls, c => c.Id == receipt.ToolCallId);
            if (!receipt.Content!.Contains("\"items\"", StringComparison.Ordinal)) continue;
            using var actual = JsonDocument.Parse(originals[receipt.ToolCallId!]);
            using var retained = JsonDocument.Parse(receipt.Content);
            var actualRows = actual.RootElement.GetProperty("items").EnumerateArray().Select(r => r.GetRawText()).ToArray();
            foreach (var row in retained.RootElement.GetProperty("items").EnumerateArray())
                Assert.Contains(row.GetRawText(), actualRows);
            Assert.InRange(receipt.Content.Length, 1, aggressive ? 2400 : 6000);
        }
        foreach (var call in calls) Assert.Single(context.Messages, m => m.ToolCallId == call.Id);
    }
}
