using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

public sealed class AgentRunner(IAiChatClient chatClient) : IAgentRunner
{
    internal const string EmptyResponseRecoveryInstruction =
        "Empty reply: use receipts; finish latest request; do not repeat completed edits.";
    internal const string TruncatedResponseRecoveryInstruction =
        "Truncated reply: use receipts; finish latest request; do not repeat completed edits.";

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
        var responseRetryUsed = false;
        string? recoveryInstruction = null;
        var conservativeContext = false;

        for (var round = 0; round < request.MaximumToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Empty replies stay in history; rejected truncated replies do not.
            // Recovery changes only the next request, preserving actual receipts.
            var completionRequest = recoveryInstruction is not null
                ? request with { SystemPrompt = request.SystemPrompt + "\n" + recoveryInstruction }
                : request;
            recoveryInstruction = null;
            CompletionAttempt completion;
            try
            {
                completion = await CompleteWithContextRetryAsync(
                    completionRequest,
                    selectedTools,
                    availableTools,
                    contextWindowTokens,
                    reportEvent,
                    cancellationToken,
                    conservativeContext);
            }
            catch (AiResponseTruncatedException) when (!responseRetryUsed && round + 1 < request.MaximumToolRounds)
            {
                // Share the existing single recovery allowance with empty replies.
                // Use existing conservative context compaction, without raising caps.
                responseRetryUsed = true;
                conservativeContext = true;
                recoveryInstruction = TruncatedResponseRecoveryInstruction;
                await ReportAsync(reportEvent, new AgentRunEvent(AgentRunEventKind.TruncatedResponseRetry));
                continue;
            }
            contextWindowTokens = completion.ContextWindowTokens;

            request.Conversation.AddAssistant(completion.Completion);
            if (!IsEmptyOrControlOnly(completion.Completion.Content))
            {
                await ReportAsync(
                    reportEvent,
                    new AgentRunEvent(
                        AgentRunEventKind.AssistantMessage,
                        completion.Completion.Content.Trim()));
            }

            if (completion.Completion.ToolCalls.Count == 0)
            {
                if (IsEmptyOrControlOnly(completion.Completion.Content) &&
                    !responseRetryUsed && round + 1 < request.MaximumToolRounds)
                {
                    responseRetryUsed = true;
                    recoveryInstruction = EmptyResponseRecoveryInstruction;
                    await ReportAsync(reportEvent, new AgentRunEvent(AgentRunEventKind.EmptyResponseRetry));
                    continue;
                }
                return new AgentRunResult(
                    completion.Completion.Model,
                    contextWindowTokens,
                    IsEmptyOrControlOnly(completion.Completion.Content));
            }

            if (request.Toolset is null)
                throw new InvalidOperationException("The model requested tools, but no agent toolset is available.");

            foreach (var toolCall in completion.Completion.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private static bool IsEmptyOrControlOnly([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        ReadOnlySpan<char> text = content.AsSpan().Trim();
        if (text.SequenceEqual("assistant")) return true;
        if (text.StartsWith("assistant\r\n", StringComparison.Ordinal)) text = text[11..].Trim();
        else if (text.StartsWith("assistant\n", StringComparison.Ordinal)) text = text[10..].Trim();
        bool sawReasoningBlock = false;
        while (text.StartsWith("<think>", StringComparison.Ordinal))
        {
            int close = text.IndexOf("</think>", StringComparison.Ordinal);
            if (close < 0) return false;
            text = text[(close + 8)..].Trim();
            sawReasoningBlock = true;
        }
        return sawReasoningBlock && text.IsEmpty;
    }

    private async Task<CompletionAttempt> CompleteWithContextRetryAsync(
        AgentRunRequest request,
        IReadOnlyList<AiToolDefinition> selectedTools,
        IReadOnlyList<AiToolDefinition> availableTools,
        int contextWindowTokens,
        Func<AgentRunEvent, ValueTask>? reportEvent,
        CancellationToken cancellationToken,
        bool aggressiveContext = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var aggressive = aggressiveContext || attempt > 0;
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
