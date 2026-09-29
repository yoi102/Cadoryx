using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentContextTests
{
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
