using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

public sealed class AgentRunner(IAiChatClient chatClient) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Func<AgentRunEvent, ValueTask>? reportEvent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var availableTools = request.Toolset?.ToolDefinitions ?? [];
        var selectedTools = request.Toolset?.SelectTools(request.UserPrompt) ?? [];
        var contextWindowTokens = NormalizeContextWindow(request.ContextWindowTokens);

        for (var round = 0; round < request.MaximumToolRounds; round++)
        {
            var completion = await CompleteWithContextRetryAsync(
                request,
                selectedTools,
                availableTools,
                contextWindowTokens,
                reportEvent,
                cancellationToken);
            contextWindowTokens = completion.ContextWindowTokens;

            request.Conversation.AddAssistant(completion.Completion);
            if (!string.IsNullOrWhiteSpace(completion.Completion.Content))
            {
                await ReportAsync(
                    reportEvent,
                    new AgentRunEvent(
                        AgentRunEventKind.AssistantMessage,
                        completion.Completion.Content.Trim()));
            }

            if (completion.Completion.ToolCalls.Count == 0)
            {
                return new AgentRunResult(
                    completion.Completion.Model,
                    contextWindowTokens,
                    string.IsNullOrWhiteSpace(completion.Completion.Content));
            }

            if (request.Toolset is null)
                throw new InvalidOperationException("The model requested tools, but no agent toolset is available.");

            foreach (var toolCall in completion.Completion.ToolCalls)
            {
                var result = await request.Toolset.ExecuteAsync(toolCall, cancellationToken);
                request.Conversation.AddToolResult(toolCall, result);
                await ReportAsync(
                    reportEvent,
                    new AgentRunEvent(
                        AgentRunEventKind.ToolResult,
                        result,
                        toolCall.Name));
            }
        }

        throw new InvalidOperationException("The model exceeded the maximum number of agent tool rounds.");
    }

    private async Task<CompletionAttempt> CompleteWithContextRetryAsync(
        AgentRunRequest request,
        IReadOnlyList<AiToolDefinition> selectedTools,
        IReadOnlyList<AiToolDefinition> availableTools,
        int contextWindowTokens,
        Func<AgentRunEvent, ValueTask>? reportEvent,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var aggressive = attempt > 0;
            var tools = aggressive && request.Toolset is not null && availableTools.Count > 0
                ? request.Toolset.SelectTools(request.UserPrompt, aggressive: true)
                : selectedTools;
            var context = AgentRequestContextBuilder.Build(
                request.SystemPrompt,
                request.Conversation.Messages,
                tools,
                contextWindowTokens,
                aggressive);
            try
            {
                var completion = await chatClient.CompleteAsync(
                    new AiChatRequest(
                        request.Endpoint,
                        request.Model,
                        context.Messages,
                        context.Tools,
                        request.Temperature,
                        context.MaxOutputTokens),
                    cancellationToken);
                return new CompletionAttempt(completion, contextWindowTokens);
            }
            catch (AiContextWindowExceededException exception) when (attempt == 0)
            {
                if (exception.ContextWindowTokens is { } reportedContext)
                    contextWindowTokens = NormalizeContextWindow(reportedContext);

                await ReportAsync(
                    reportEvent,
                    new AgentRunEvent(
                        AgentRunEventKind.ContextReduced,
                        ContextWindowTokens: contextWindowTokens));
            }
        }

        throw new InvalidOperationException("The agent request could not fit in the configured context window.");
    }

    private static void Validate(AgentRunRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        ArgumentNullException.ThrowIfNull(request.Conversation);
        if (request.MaximumToolRounds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumToolRounds));
    }

    private static int NormalizeContextWindow(int value) =>
        Math.Clamp(
            value,
            AiAssistantSettings.MinimumContextWindowTokens,
            AiAssistantSettings.MaximumContextWindowTokens);

    private static ValueTask ReportAsync(
        Func<AgentRunEvent, ValueTask>? reportEvent,
        AgentRunEvent agentEvent) =>
        reportEvent?.Invoke(agentEvent) ?? ValueTask.CompletedTask;

    private sealed record CompletionAttempt(
        AiChatCompletion Completion,
        int ContextWindowTokens);
}
