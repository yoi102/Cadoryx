using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentContextTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void UnusedShortHistoryBudgetAdmitsToolsButLongHistoryKeepsItsReserve(bool aggressive)
    {
        var tool=new AiToolDefinition("large_tool",new string('x',13000),System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());
        var shortContext=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User("Current request")],[tool],8192,aggressive);
        Assert.Single(shortContext.Tools);Assert.Equal("Current request",shortContext.Messages[^1].Content);
        Assert.True(shortContext.EstimatedPromptTokens<=8192-shortContext.MaxOutputTokens-(aggressive?1280:640));
        var longContext=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User(new string('y',15000)),AiChatMessage.User("Current request")],[tool],8192,aggressive);
        // The large tool fits the normal proportional reserve, but not the retry reserve.
        if(aggressive)Assert.Empty(longContext.Tools);
        Assert.Equal("Current request",longContext.Messages[^1].Content);
        Assert.True(longContext.EstimatedPromptTokens<=8192-longContext.MaxOutputTokens-(aggressive?1280:640));
    }
    [Fact]
    public void ContextReductionKeepsLatestRequestWithoutOrphanedToolMessages()
    {
        var history = new AiChatMessage[]
        {
            AiChatMessage.User("Old request " + new string('x', 8000)),
            AiChatMessage.Assistant(null, [new AiToolCall("old-call", "cad_status", "{}")]),
            AiChatMessage.Tool("old-call", new string('y', 8000)),
            AiChatMessage.Tool("orphan-call", "unmatched result"),
            AiChatMessage.User("Inspect the current bracket")
        };

        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 4096);

        Assert.Equal(AiChatRole.System, context.Messages[0].Role);
        Assert.Equal("Inspect the current bracket", context.Messages[^1].Content);
        Assert.DoesNotContain(context.Messages, message => message.Content?.StartsWith("Old request") == true);
        Assert.DoesNotContain(context.Messages, message => message.Role == AiChatRole.Tool);
        Assert.True(context.EstimatedPromptTokens <= 4096 - context.MaxOutputTokens - 640);
    }

    [Fact]
    public void OversizedLatestRequestIsTrimmedAndKeepsItsEnding()
    {
        var latest = "START:" + new string('x', 40000) + ":END";
        var context = AgentRequestContextBuilder.Build("CAD assistant", [AiChatMessage.User(latest)], [], 4096);
        var retained = Assert.Single(context.Messages, message => message.Role == AiChatRole.User);

        Assert.Contains("[content truncated to fit the model context window]", retained.Content);
        Assert.StartsWith("START:", retained.Content);
        Assert.EndsWith(":END", retained.Content);
        Assert.True(context.EstimatedPromptTokens <= 4096 - context.MaxOutputTokens - 640);
    }

    [Fact]
    public async Task ContextWindowRetryDoesNotCommitAnUnsuccessfulAssistantTurn()
    {
        var chat = new RetryChatClient();
        var conversation = new AgentConversation();
        conversation.AddUser("Inspect current document");
        var events = new List<AgentRunEvent>();

        var result = await new AgentRunner(chat).RunAsync(new AgentRunRequest(
            "http://localhost:1234/v1", "model", "CAD assistant", "Inspect current document",
            conversation, 8192, 0.2), entry =>
        {
            events.Add(entry);
            return ValueTask.CompletedTask;
        });

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(4096, result.ContextWindowTokens);
        Assert.Single(events, entry => entry.Kind == AgentRunEventKind.ContextReduced &&
            entry.ContextWindowTokens == 4096);
        Assert.Equal([AiChatRole.User, AiChatRole.Assistant], conversation.Messages.Select(message => message.Role));
        Assert.Equal("Current document inspected", conversation.Messages[^1].Content);
    }

    private sealed class RetryChatClient : IAiChatClient
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<IReadOnlyList<string>> GetModelsAsync(string endpoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["model"]);

        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
                throw new AiContextWindowExceededException("Context exceeded", null, 9000, 4096);
            return Task.FromResult(new AiChatCompletion("Current document inspected", [], "model"));
        }
    }
}
