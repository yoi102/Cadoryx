using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

public interface IAgentToolset
{
    IReadOnlyList<AiToolDefinition> ToolDefinitions { get; }

    IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false);

    Task<string> ExecuteAsync(AiToolCall toolCall, CancellationToken cancellationToken);
}

public sealed class AgentConversation
{
    private readonly List<AiChatMessage> _messages = [];

    public IReadOnlyList<AiChatMessage> Messages => _messages;

    public void AddUser(
        string content,
        IReadOnlyList<AiChatContentPart>? contentParts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        _messages.Add(AiChatMessage.User(content, contentParts));
    }

    public void Clear() => _messages.Clear();

    internal void AddAssistant(AiChatCompletion completion) =>
        _messages.Add(AiChatMessage.Assistant(completion.Content, completion.ToolCalls));

    internal void AddToolResult(AiToolCall toolCall, string result) =>
        _messages.Add(AiChatMessage.Tool(toolCall.Id, result));
}

public sealed record AgentRunRequest(
    string Endpoint,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    AgentConversation Conversation,
    int ContextWindowTokens,
    double Temperature,
    IAgentToolset? Toolset = null,
    int MaximumToolRounds = 12);

public enum AgentRunEventKind
{
    AssistantMessage,
    ToolResult,
    ContextReduced
}

public sealed record AgentRunEvent(
    AgentRunEventKind Kind,
    string? Content = null,
    string? ToolName = null,
    int? ContextWindowTokens = null);

public sealed record AgentRunResult(
    string? Model,
    int ContextWindowTokens,
    bool ResponseWasEmpty);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Func<AgentRunEvent, ValueTask>? reportEvent = null,
        CancellationToken cancellationToken = default);
}
