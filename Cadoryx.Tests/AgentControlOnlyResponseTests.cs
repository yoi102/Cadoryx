using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using System.Text.Json;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentControlOnlyResponseTests
{
    [Theory]
    [InlineData("assistant\n<think>\n\n</think>")]
    [InlineData("assistant\r\n<think>\r\n\r\n</think>")]
    [InlineData("<think>Only internal reasoning, no final reply.</think>")]
    [InlineData("assistant")]
    [InlineData(" <think></think> \n <think>still reasoning</think> ")]
    public async Task ControlOnlyReplyRetainsRawHistoryAndRecoversWithinExistingOneRetry(string raw)
    {
        var chat = new Chat(n => new(n == 1 ? raw : "Actual answer", [], "model"));
        var conversation = new AgentConversation(); conversation.AddUser("Inspect the current document"); var events = new List<AgentRunEvent>();
        var result = await new AgentRunner(chat).RunAsync(Request(conversation, 2), e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.False(result.ResponseWasEmpty); Assert.Equal(8192, result.ContextWindowTokens); Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(raw, conversation.Messages[1].Content); Assert.Equal("Actual answer", conversation.Messages[^1].Content);
        Assert.Single(events, e => e.Kind == AgentRunEventKind.EmptyResponseRetry);
        Assert.Equal("Actual answer", Assert.Single(events, e => e.Kind == AgentRunEventKind.AssistantMessage).Content);
        Assert.All(chat.Requests, r => { Assert.Equal(1024, r.MaxOutputTokens); Assert.Equal("model", r.Model); Assert.Equal(0, r.Temperature); });
    }

    [Theory]
    [InlineData("assistant\nHere is the actual CAD answer.")]
    [InlineData("<think></think>Actual answer")]
    [InlineData("```xml\n<think></think>\n```")]
    [InlineData("Literal <think></think> in the explanation")]
    [InlineData("<think>unfinished literal")]
    public async Task ActualOrLiteralTextIsPreservedWithoutAnAdditionalRequest(string raw)
    {
        var chat = new Chat(_ => new(raw, [], "model")); var conversation = new AgentConversation(); conversation.AddUser("Explain");
        var events = new List<AgentRunEvent>(); var result = await new AgentRunner(chat).RunAsync(Request(conversation, 16), e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.False(result.ResponseWasEmpty); Assert.Single(chat.Requests); Assert.Equal(raw, conversation.Messages[^1].Content);
        Assert.DoesNotContain(events, e => e.Kind == AgentRunEventKind.EmptyResponseRetry);
        Assert.Equal(raw.Trim(), Assert.Single(events, e => e.Kind == AgentRunEventKind.AssistantMessage).Content);
    }

    [Theory] [InlineData(1)] [InlineData(16)]
    public async Task RepeatedControlFramesStopAsEmptyWithoutIncreasingRoundsOrInventingAnswer(int rounds)
    {
        var chat = new Chat(_ => new("assistant\n<think>\n\n</think>", [], "model"));
        var conversation = new AgentConversation(); conversation.AddUser("Create the requested drawing"); var events = new List<AgentRunEvent>();
        var result = await new AgentRunner(chat).RunAsync(Request(conversation, rounds), e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.True(result.ResponseWasEmpty); Assert.Equal(Math.Min(rounds, 2), chat.Requests.Count);
        Assert.Equal(rounds == 1 ? 0 : 1, events.Count(e => e.Kind == AgentRunEventKind.EmptyResponseRetry));
        Assert.DoesNotContain(events, e => e.Kind == AgentRunEventKind.AssistantMessage);
        Assert.Equal(chat.Requests.Count + 1, conversation.Messages.Count);
    }

    [Fact]
    public async Task ControlOnlyContentWithToolCallsExecutesExactlyOnceAndKeepsActualReceipt()
    {
        var tools = new Tools(); var call = new AiToolCall("actual", "read", "{}");
        var chat = new Chat(n => n == 1 ? new("assistant\n<think></think>", [call], "model") : new("Actual answer", [], "model"));
        var conversation = new AgentConversation(); conversation.AddUser("Inspect"); var events = new List<AgentRunEvent>();
        var result = await new AgentRunner(chat).RunAsync(Request(conversation, 2) with { Toolset = tools }, e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.False(result.ResponseWasEmpty); Assert.Equal(1, tools.Calls); Assert.Equal(2, chat.Requests.Count);
        Assert.DoesNotContain(events, e => e.Kind == AgentRunEventKind.EmptyResponseRetry);
        Assert.Single(events, e => e.Kind == AgentRunEventKind.ToolResult); Assert.Single(events, e => e.Kind == AgentRunEventKind.AssistantMessage);
        Assert.Equal("{\"success\":true,\"changed\":false}", Assert.Single(conversation.Messages, m => m.Role == AiChatRole.Tool).Content);
        Assert.Equal("assistant\n<think></think>", conversation.Messages[1].Content);
    }

    private static AgentRunRequest Request(AgentConversation conversation, int rounds) => new("http://localhost:15630/v1", "model", "CAD assistant", "Inspect", conversation, 8192, 0, MaximumToolRounds: rounds);
    private sealed class Chat(Func<int, AiChatCompletion> reply) : IAiChatClient
    {
        internal List<AiChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<string>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["model"]);
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.FromResult(reply(Requests.Count)); }
    }
    private sealed class Tools : IAgentToolset
    {
        internal int Calls { get; private set; }
        public IReadOnlyList<AiToolDefinition> ToolDefinitions { get; } = [new("read", "Read current state", JsonSerializer.SerializeToElement(new { type = "object" }))];
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => ToolDefinitions;
        public Task<string> ExecuteAsync(AiToolCall call, CancellationToken token) { Calls++; return Task.FromResult("{\"success\":true,\"changed\":false}"); }
    }
}
