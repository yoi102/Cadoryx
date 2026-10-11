using System.Text.Json;
using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

internal static class AgentToolResultCompactor
{
    private const int MaximumReceiptCharacters = 1048576;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "success", "action", "documentId", "stateId", "offset", "limit", "total", "hasMore", "items" };

    internal static bool TryCompactNativeSelectionPage(string content, int maximumCharacters, out string compacted, AiToolCall? actualCall = null)
    {
        compacted = content;
        if (content.Length <= maximumCharacters || content.Length > MaximumReceiptCharacters || maximumCharacters <= 0)
            return false;
        try
        {
            using var parsed = JsonDocument.Parse(content); var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) return false;
            if (seen.Count != Fields.Count || root.GetProperty("success").ValueKind != JsonValueKind.True ||
                root.GetProperty("action").ValueKind != JsonValueKind.String || root.GetProperty("action").GetString() != "inspect_sources" ||
                !Identity(root.GetProperty("documentId")) || !Identity(root.GetProperty("stateId")) ||
                !Integer(root.GetProperty("offset"), 0, 100000, out var offset) ||
                !Integer(root.GetProperty("limit"), 1, 50, out var limit) ||
                !Integer(root.GetProperty("total"), 0, int.MaxValue, out var total) ||
                root.GetProperty("hasMore").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("hasMore").GetBoolean() != ((long)offset + limit < total) ||
                root.GetProperty("items").ValueKind != JsonValueKind.Array) return false;
            var items = root.GetProperty("items").EnumerateArray().ToArray();
            if (items.Length != Math.Min(limit, Math.Max(0, total - offset)) ||
                items.Any(i => i.ValueKind != JsonValueKind.Object || HasDuplicateFields(i))) return false;

            var continuation = AgentNativeReadContinuation.FromActualQuery(actualCall, root, items);

            string Page(int retained)
            {
                var page = root.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value, StringComparer.Ordinal);
                page["items"] = items[..retained];
                if (retained < items.Length)
                {
                    var context = new Dictionary<string, object>
                    {
                    ["originalItemCount"] = items.Length, ["retainedItemCount"] = retained, ["omittedItemCount"] = items.Length - retained,
                    ["nextOffset"] = offset + retained,
                    ["message"] = retained == 0
                        ? "No complete item fits this context page; no selection is available. Do not skip or invent an item."
                        : "Context omitted later items; listed items are complete. Fetch omitted items with inspect_sources offset=nextOffset and a smaller limit."
                    };
                    if (retained > 0 && offset + retained <= 100000 && continuation is not null)
                    {
                        context["toolName"] = actualCall!.Name;
                        context["nextRequest"] = continuation.NextRequest(offset + retained, limit, retained);
                        context["requestNote"] = "Read-only continuation copied from this successful query. Not executed; stale identities still reject.";
                    }
                    page["contextPage"] = context;
                }
                return JsonSerializer.Serialize(page);
            }
            var full = Page(items.Length);
            if (full.Length <= maximumCharacters) { compacted = full; return true; }
            var smallest = Page(0);
            if (smallest.Length > maximumCharacters) return false;
            var low = 0; var high = items.Length; compacted = smallest;
            while (low < high)
            {
                var middle = (low + high + 1) / 2; var candidate = Page(middle);
                if (candidate.Length <= maximumCharacters) { low = middle; compacted = candidate; }
                else high = middle - 1;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Identity(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(value.GetString(), "D", out var id) && id != Guid.Empty;
    private static bool HasDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(HasDuplicateFields);
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(p => !seen.Add(p.Name) || HasDuplicateFields(p.Value));
    }
    private static bool Integer(JsonElement element, int minimum, int maximum, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }
}
