using Cadoryx.AI.Contracts;
using System.Text.RegularExpressions;

namespace Cadoryx.Agent;

internal sealed record AgentRequestContext(
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiToolDefinition> Tools,
    int MaxOutputTokens,
    int EstimatedPromptTokens);

internal static partial class AgentRequestContextBuilder
{
    private sealed record NativeReadCompaction(AiToolCall Call, string Receipt, int MaximumCharacters);
    private const int NormalSafetyTokens = 640;
    private const int RetrySafetyTokens = 1280;
    private const int NormalMaximumToolResultCharacters = 6000;
    private const int RetryMaximumToolResultCharacters = 2400;
    private const int MinimumHistoryTokens = 256;
    private const string TruncatedContentMarker = "\n[content truncated to fit the model context window]";

    internal static AgentRequestContext Build(
        string systemPrompt,
        IReadOnlyList<AiChatMessage> conversation,
        IReadOnlyList<AiToolDefinition> tools,
        int contextWindowTokens,
        bool aggressive = false)
    {
        contextWindowTokens = Math.Clamp(
            contextWindowTokens,
            AiAssistantSettings.MinimumContextWindowTokens,
            AiAssistantSettings.MaximumContextWindowTokens);
        var maxOutputTokens = Math.Clamp(contextWindowTokens / 8, 512, 2048);
        var safetyTokens = aggressive ? RetrySafetyTokens : NormalSafetyTokens;
        var promptBudget = Math.Max(1024, contextWindowTokens - maxOutputTokens - safetyTokens);

        var systemMessage = AiChatMessage.System(systemPrompt);
        var systemTokens = EstimateMessageTokens(systemMessage);
        var availableToolTokens = Math.Max(0, promptBudget - systemTokens - MinimumHistoryTokens);
        var historyTokens = conversation.Sum(EstimateMessageTokens);
        var selectedTools = FitTools(tools, availableToolTokens, promptBudget, historyTokens, aggressive);
        var fixedTokens = systemTokens + selectedTools.Sum(EstimateToolTokens);
        var historyBudget = Math.Max(0, promptBudget - fixedTokens);
        var history = SelectHistory(
            conversation,
            historyBudget,
            aggressive ? RetryMaximumToolResultCharacters : NormalMaximumToolResultCharacters);

        var messages = new List<AiChatMessage>(history.Count + 1)
        {
            systemMessage
        };
        messages.AddRange(history);
        var estimate = messages.Sum(EstimateMessageTokens) + selectedTools.Sum(EstimateToolTokens);
        return new AgentRequestContext(messages, selectedTools, maxOutputTokens, estimate);
    }

    private static IReadOnlyList<AiToolDefinition> FitTools(
        IReadOnlyList<AiToolDefinition> tools,
        int availableTokens,
        int totalPromptBudget,
        int historyTokens,
        bool aggressive)
    {
        if (tools.Count == 0 || availableTokens <= 0)
            return [];

        // A short conversation does not need the full proportional history reserve. Keep the
        // safety/output budgets and minimum history, but make unused space available to tools.
        // Long conversations retain the existing proportional budget and history compaction.
        var fixedBudget = Math.Min(availableTokens, Math.Max(
            (int)(totalPromptBudget * (aggressive ? 0.58 : 0.68)),
            availableTokens - Math.Max(0, historyTokens - MinimumHistoryTokens)));
        var result = new List<AiToolDefinition>(tools.Count);
        var used = 0;
        foreach (var tool in tools)
        {
            var cost = EstimateToolTokens(tool);
            if (used + cost > fixedBudget)
                continue;
            result.Add(tool);
            used += cost;
        }
        return result;
    }

    private static IReadOnlyList<AiChatMessage> SelectHistory(
        IReadOnlyList<AiChatMessage> conversation,
        int tokenBudget,
        int maximumToolResultCharacters)
    {
        if (conversation.Count == 0)
            return [];

        var protectedCallId = AgentSuccessfulActionReceipt.FindLatestCompleteExchange(conversation);
        var nativeCallId = AgentNativeSelectionReceipt.FindLatestCompleteExchange(conversation);
        var protectedCallIds = new[] { protectedCallId, nativeCallId }.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var uniqueCalls = conversation.Where(message => message.Role == AiChatRole.Assistant)
            .SelectMany(message => message.ToolCalls ?? []).GroupBy(call => call.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var compacted = NormalizeToolMessages(conversation
                .Select(message => CompactMessage(message, maximumToolResultCharacters,
                    message.ToolCallId is { } id ? uniqueCalls.GetValueOrDefault(id) : null))
                .ToArray())
            .ToArray();
        var turnStarts = compacted
            .Select((message, index) => (message, index))
            .Where(item => item.message.Role == AiChatRole.User)
            .Select(item => item.index)
            .ToArray();
        if (turnStarts.Length == 0)
            return TakeNewestMessages(compacted, tokenBudget);

        var selectedStart = turnStarts[^1];
        var used = EstimateRange(compacted, selectedStart, compacted.Length);
        for (var turn = turnStarts.Length - 2; turn >= 0; turn--)
        {
            var start = turnStarts[turn];
            var cost = EstimateRange(compacted, start, selectedStart);
            if (used + cost > tokenBudget)
                break;
            selectedStart = start;
            used += cost;
        }

        var selected = compacted[selectedStart..].ToList();
        NativeReadCompaction? nativeRead = null;
        if (nativeCallId is not null && uniqueCalls.TryGetValue(nativeCallId, out var nativeCall) &&
            conversation.SingleOrDefault(m => m.Role == AiChatRole.Tool && m.ToolCallId == nativeCallId)?.Content is { } nativeReceipt)
            nativeRead = new(nativeCall, nativeReceipt, maximumToolResultCharacters);
        TrimHistoryToBudget(selected, tokenBudget, protectedCallIds, nativeRead);
        var omitted = new List<string>();
        if (protectedCallId is not null && !AgentSuccessfulActionReceipt.ContainsExchange(selected, protectedCallId))
            omitted.Add(AgentSuccessfulActionReceipt.OmittedExchangeMarker);
        if (nativeCallId is not null && !AgentSuccessfulActionReceipt.ContainsExchange(selected, nativeCallId))
            omitted.Add(AgentNativeSelectionReceipt.OmittedExchangeMarker);
        if (omitted.Count > 0)
        {
            // This is context-compaction metadata, not a replacement tool receipt.
            var marker = AiChatMessage.System(string.Join("\n", omitted));
            var markerCost = EstimateMessageTokens(marker);
            if (markerCost <= tokenBudget)
            {
                TrimHistoryToBudget(selected, tokenBudget - markerCost);
                var user = selected.FindIndex(message => message.Role == AiChatRole.User);
                selected.Insert(user >= 0 ? user + 1 : 0, marker);
            }
        }
        return NormalizeToolMessages(selected);
    }

    private static void TrimHistoryToBudget(List<AiChatMessage> selected, int tokenBudget, IReadOnlySet<string>? protectedCallIds = null,
        NativeReadCompaction? nativeRead = null)
    {
        while (selected.Count > 0 && selected.Sum(EstimateMessageTokens) > tokenBudget)
        {
            var secondUser = selected.FindIndex(1, message => message.Role == AiChatRole.User);
            if (secondUser > 0)
            {
                selected.RemoveRange(0, secondUser);
                continue;
            }

            var assistant = selected.FindIndex(1, message => message.Role == AiChatRole.Assistant &&
                (protectedCallIds is null || message.ToolCalls?.Any(call => protectedCallIds.Contains(call.Id)) != true));
            // When the complete protected exchange cannot fit even on its own,
            // retain the original bounded fallback and report its omission.
            if (assistant < 0 && nativeRead is not null && TryFitNativeRead(selected, tokenBudget, nativeRead))
                continue;
            if (assistant < 0)
                assistant = selected.FindIndex(1, message => message.Role == AiChatRole.Assistant);
            if (assistant >= 0)
            {
                RemoveAssistantExchange(selected, assistant);
                continue;
            }

            var tool = selected.FindIndex(message => message.Role == AiChatRole.Tool);
            if (tool >= 0)
            {
                selected.RemoveAt(tool);
                continue;
            }

            var user = selected.FindIndex(message => message.Role == AiChatRole.User);
            if (user < 0)
            {
                selected.RemoveAt(0);
                continue;
            }

            selected[user] = TruncateMessageToTokenBudget(selected[user], tokenBudget);
            break;
        }
    }

    private static bool TryFitNativeRead(List<AiChatMessage> selected, int tokenBudget, NativeReadCompaction nativeRead)
    {
        int index = selected.FindIndex(m => m.Role == AiChatRole.Tool && m.ToolCallId == nativeRead.Call.Id);
        if (index < 0) return false;
        var original = selected[index];
        int available = tokenBudget - selected.Sum(EstimateMessageTokens) + EstimateMessageTokens(original) - 8;
        if (available <= 0 || !AgentToolResultCompactor.TryCompactNativeSelectionPage(nativeRead.Receipt,
                nativeRead.MaximumCharacters, out var content, nativeRead.Call, available)) return false;
        var candidate = original with { Content = content };
        if (EstimateMessageTokens(candidate) >= EstimateMessageTokens(original)) return false;
        selected[index] = candidate;
        return true;
    }

    private static void RemoveAssistantExchange(List<AiChatMessage> messages, int assistantIndex)
    {
        var assistant = messages[assistantIndex];
        messages.RemoveAt(assistantIndex);
        if (assistant.ToolCalls is { Count: > 0 } calls)
        {
            var callIds = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            messages.RemoveAll(message => message.Role == AiChatRole.Tool &&
                                          message.ToolCallId is { } id &&
                                          callIds.Contains(id));
        }
    }

    private static IReadOnlyList<AiChatMessage> NormalizeToolMessages(
        IReadOnlyList<AiChatMessage> messages)
    {
        if (messages.Count == 0)
            return [];

        var retained = messages.ToList();
        for (var index = retained.Count - 1; index >= 0; index--)
        {
            var message = retained[index];
            if (message.Role != AiChatRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
                continue;

            var resultIds = retained
                .Where(candidate => candidate.Role == AiChatRole.Tool && candidate.ToolCallId is not null)
                .Select(candidate => candidate.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            if (calls.All(call => resultIds.Contains(call.Id)))
                continue;

            RemoveAssistantExchange(retained, index);
        }

        var retainedCallIds = retained
            .Where(message => message.Role == AiChatRole.Assistant)
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.Id)
            .ToHashSet(StringComparer.Ordinal);
        retained.RemoveAll(message => message.Role == AiChatRole.Tool &&
                                      (message.ToolCallId is null ||
                                       !retainedCallIds.Contains(message.ToolCallId)));
        return retained;
    }

    private static IReadOnlyList<AiChatMessage> TakeNewestMessages(
        IReadOnlyList<AiChatMessage> messages,
        int tokenBudget)
    {
        var result = new List<AiChatMessage>();
        var used = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var cost = EstimateMessageTokens(messages[index]);
            if (used + cost > tokenBudget && result.Count > 0)
                break;
            result.Add(messages[index]);
            used += cost;
        }
        result.Reverse();
        TrimHistoryToBudget(result, tokenBudget);
        return NormalizeToolMessages(result);
    }

    private static AiChatMessage CompactMessage(AiChatMessage message, int maximumToolResultCharacters, AiToolCall? actualCall = null)
    {
        if (message.Role != AiChatRole.Tool ||
            string.IsNullOrEmpty(message.Content) ||
            message.Content.Length <= maximumToolResultCharacters)
        {
            return message;
        }

        var suffix = "\n[tool result truncated to fit the model context window]";
        return message with
        {
            Content = AgentToolResultCompactor.TryCompactNativeSelectionPage(message.Content, maximumToolResultCharacters, out var page, actualCall)
                ? page
                : string.Concat(message.Content.AsSpan(0, maximumToolResultCharacters - suffix.Length), suffix)
        };
    }

    private static AiChatMessage TruncateMessageToTokenBudget(AiChatMessage message, int tokenBudget)
    {
        if (EstimateMessageTokens(message) <= tokenBudget)
            return message;

        if (message.ContentParts is { Count: > 0 } contentParts &&
            contentParts.Any(part => part.Type == AiChatContentPartType.Image) &&
            contentParts.Count(part => part.Type == AiChatContentPartType.Image) * 1024 > tokenBudget)
        {
            message = message with
            {
                ContentParts = contentParts
                    .Where(part => part.Type != AiChatContentPartType.Image)
                    .ToArray()
            };
            if (EstimateMessageTokens(message) <= tokenBudget)
                return message;
        }

        var textLength = (message.Content?.Length ?? 0) +
                         (message.ContentParts ?? [])
                         .Where(part => part.Type == AiChatContentPartType.Text)
                         .Sum(part => part.Text?.Length ?? 0);
        if (textLength == 0)
            return message;

        var low = 0;
        var high = textLength;
        while (low < high)
        {
            var keptCharacters = low + (high - low + 1) / 2;
            var candidate = CreateCharacterBudgetMessage(message, keptCharacters);
            if (EstimateMessageTokens(candidate) <= tokenBudget)
                low = keptCharacters;
            else
                high = keptCharacters - 1;
        }

        return CreateCharacterBudgetMessage(message, low);
    }

    private static AiChatMessage CreateCharacterBudgetMessage(
        AiChatMessage message,
        int characterBudget)
    {
        var remaining = characterBudget;
        var content = AllocateTextCharacters(message.Content, ref remaining);
        var parts = message.ContentParts?
            .Select(part => part.Type == AiChatContentPartType.Text
                ? part with { Text = AllocateTextCharacters(part.Text, ref remaining) }
                : part)
            .ToArray();
        return message with
        {
            Content = content,
            ContentParts = parts
        };
    }

    private static string? AllocateTextCharacters(string? text, ref int remaining)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var keptCharacters = Math.Min(text.Length, Math.Max(remaining, 0));
        remaining -= keptCharacters;
        return keptCharacters == text.Length
            ? text
            : CreateTruncatedContent(text, keptCharacters);
    }

    private static string CreateTruncatedContent(string content, int keptCharacters)
    {
        if (keptCharacters >= content.Length)
            return content;
        if (keptCharacters <= 0)
            return string.Empty;
        if (keptCharacters <= TruncatedContentMarker.Length + 8)
            return content[..keptCharacters];

        var contentCharacters = keptCharacters - TruncatedContentMarker.Length;
        var prefixLength = (int)(contentCharacters * 0.75);
        var suffixLength = contentCharacters - prefixLength;
        return string.Concat(
            content.AsSpan(0, prefixLength),
            TruncatedContentMarker,
            content.AsSpan(content.Length - suffixLength));
    }

    private static int EstimateRange(IReadOnlyList<AiChatMessage> messages, int start, int end)
    {
        var total = 0;
        for (var index = start; index < end; index++)
            total += EstimateMessageTokens(messages[index]);
        return total;
    }

    internal static int EstimateMessageTokens(AiChatMessage message)
    {
        var total = 8 + EstimateTextTokens(message.Content);
        if (message.ContentParts is { Count: > 0 } contentParts)
        {
            total += contentParts.Sum(part => part.Type switch
            {
                AiChatContentPartType.Image => 1024,
                _ => EstimateTextTokens(part.Text)
            });
        }
        if (message.ToolCalls is { Count: > 0 })
        {
            total += message.ToolCalls.Sum(call =>
                12 + EstimateTextTokens(call.Name) + EstimateTextTokens(call.ArgumentsJson));
        }
        return total;
    }

    internal static int EstimateToolTokens(AiToolDefinition tool) =>
        20 + EstimateTextTokens(tool.Name) + EstimateTextTokens(tool.Description) +
        EstimateTextTokens(tool.Parameters.GetRawText());

    internal static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var ascii = 0;
        var nonAscii = 0;
        foreach (var character in text)
        {
            if (character <= 0x7f)
                ascii++;
            else
                nonAscii++;
        }
        // Opaque identities are not prose. The actual native-source page used
        // 3002 model tokens where the prose estimate charged only1578. Reserve
        // one token per identity character so exact selections consume budget
        // before history is chosen; never shorten or synthesize their values.
        var identityCharacters = 0;
        foreach (Match identity in OpaqueIdentityPattern().Matches(text))
            identityCharacters += identity.Length;
        return (int)Math.Ceiling((ascii - identityCharacters) / 3.6 + nonAscii / 1.25 + identityCharacters);
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}|[A-Fa-f0-9]{64})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdentityPattern();
}
