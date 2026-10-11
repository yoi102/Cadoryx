using System.Text.Json;
using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

internal static class AgentSuccessfulActionReceipt
{
    internal const string OmittedExchangeMarker =
        "Context omitted the latest successful tool exchange for this user turn. " +
        "Do not assume its identifiers or repeat completed edits; inspect the current state.";

    internal static string? FindLatestCompleteExchange(IReadOnlyList<AiChatMessage> messages)
    {
        var user = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].Role == AiChatRole.User) { user = index; break; }
        if (user < 0) return null;

        for (var index = messages.Count - 1; index > user; index--)
        {
            if (messages[index].Role != AiChatRole.Assistant ||
                messages[index].ToolCalls is not { Count: > 0 } calls) continue;
            var ids = calls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != calls.Count) continue;
            var end = index + 1;
            while (end < messages.Count && messages[end].Role == AiChatRole.Tool) end++;
            var results = messages.Skip(index + 1).Take(end - index - 1).ToArray();
            if (results.Length != calls.Count || ids.Any(id => results.Count(r => r.ToolCallId == id) != 1)) continue;
            // A reused call ID cannot identify one exchange unambiguously.
            if (ids.Any(id => messages.Skip(user + 1).Where(m => m.Role == AiChatRole.Assistant)
                    .SelectMany(m => m.ToolCalls ?? []).Count(call => call.Id == id) != 1)) continue;
            var mutation = results.LastOrDefault(result => IsSuccessfulChange(result.Content));
            if (mutation is not null) return mutation.ToolCallId;
        }
        return null;
    }

    private static bool IsSuccessfulChange(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.Length > 1048576) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return false;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return false;
            var changed = root.TryGetProperty("changed", out var change);
            var workspaceChanged = root.TryGetProperty("workspaceChanged", out var workspaceChange);
            if ((changed && change.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                (workspaceChanged && workspaceChange.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return false;
            return (changed && change.ValueKind == JsonValueKind.True) ||
                   (workspaceChanged && workspaceChange.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    internal static bool ContainsExchange(IReadOnlyList<AiChatMessage> messages, string callId) =>
        messages.Any(message => message.Role == AiChatRole.Assistant &&
                                message.ToolCalls?.Any(call => call.Id == callId) == true);
}
