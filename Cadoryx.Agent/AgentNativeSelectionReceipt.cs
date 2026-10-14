using System.Text.Json;
using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

internal static class AgentNativeSelectionReceipt
{
    internal const string OmittedExchangeMarker =
        "Context omitted the latest native source read for this user turn. " +
        "Read current native sources again before authoring; do not invent a selection.";

    internal static string? FindLatestCompleteExchange(IReadOnlyList<AiChatMessage> messages)
    {
        var mutation = AgentSuccessfulActionReceipt.FindLatestCompleteExchange(messages);
        var after = -1;
        if (mutation is not null)
            for (var index = messages.Count - 1; index >= 0; index--)
                if (messages[index].Role == AiChatRole.Tool && messages[index].ToolCallId == mutation) { after = index; break; }
        var statusId = AgentSuccessfulActionReceipt.FindLatestCompleteExchange(messages,
            (call, content) => call.Name == "cad_status" && ReadState(content) is not null);
        var statusIndex = statusId is null ? -1 : ResultIndex(messages, statusId);
        var status = statusIndex < 0 ? null : ReadState(messages[statusIndex].Content);
        return AgentSuccessfulActionReceipt.FindLatestCompleteExchange(messages, (call, content) =>
        {
            if (messages.Where(m => m.Role == AiChatRole.Assistant).SelectMany(m => m.ToolCalls ?? []).Count(c => c.Id == call.Id) != 1 ||
                !IsCompleteRead(call, content)) return false;
            if (status is null || statusIndex < ResultIndex(messages, call.Id)) return true;
            using var receipt = JsonDocument.Parse(content!);
            return State(receipt.RootElement) == status;
        }, after);
    }

    private static int ResultIndex(IReadOnlyList<AiChatMessage> messages, string id)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].Role == AiChatRole.Tool && messages[index].ToolCallId == id) return index;
        return -1;
    }

    private static (Guid Document, Guid State)? ReadState(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.Length > 1048576) return null;
        try
        {
            using var parsed = JsonDocument.Parse(content); var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || AgentToolResultCompactor.HasDuplicateFields(root) ||
                !root.TryGetProperty("active", out var active)) return null;
            return active.ValueKind switch
            {
                JsonValueKind.True => State(root),
                JsonValueKind.False => (Guid.Empty, Guid.Empty),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static (Guid Document, Guid State)? State(JsonElement root) =>
        root.TryGetProperty("documentId", out var document) && document.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(document.GetString(), "D", out var doc) && doc != Guid.Empty &&
        root.TryGetProperty("stateId", out var state) && state.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(state.GetString(), "D", out var revision) && revision != Guid.Empty ? (doc, revision) : null;

    private static bool IsCompleteRead(AiToolCall call, string? content)
    {
        if (call.Name != "cad_drawing" || string.IsNullOrEmpty(content) || content.Length > 1048576) return false;
        try
        {
            using var parsed = JsonDocument.Parse(content); var root = parsed.RootElement;
            string[] fields = ["success", "action", "documentId", "stateId", "offset", "limit", "total", "hasMore", "items"];
            if (root.ValueKind != JsonValueKind.Object || AgentToolResultCompactor.HasDuplicateFields(root) ||
                root.EnumerateObject().Count() != fields.Length || fields.Any(f => !root.TryGetProperty(f, out _)) ||
                root.GetProperty("success").ValueKind != JsonValueKind.True ||
                root.GetProperty("action").ValueKind != JsonValueKind.String || root.GetProperty("action").GetString() != "inspect_sources" ||
                !Number(root, "offset", 0, 100000, out var offset) || !Number(root, "limit", 1, 50, out var limit) ||
                !Number(root, "total", 0, int.MaxValue, out var total) ||
                root.GetProperty("hasMore").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("hasMore").GetBoolean() != ((long)offset + limit < total) ||
                root.GetProperty("items").ValueKind != JsonValueKind.Array) return false;
            var items = root.GetProperty("items").EnumerateArray().ToArray();
            if (items.Length != Math.Min(limit, Math.Max(0, total - offset)) || items.Any(i => i.ValueKind != JsonValueKind.Object)) return false;
            return AgentNativeReadContinuation.FromActualQuery(call, root, items) is not null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private static bool Number(JsonElement root, string name, int minimum, int maximum, out int value)
    {
        value = 0;
        return root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetInt32(out value) && value >= minimum && value <= maximum;
    }
}
