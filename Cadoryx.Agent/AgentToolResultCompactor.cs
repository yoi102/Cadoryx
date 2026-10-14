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

            bool includeOmittedGeometryReads = true;
            bool includeCompleteSourceItems = true;
            int maximumAdditionalRows = 8;
            string Page(int retained)
            {
                var page = root.EnumerateObject().Where(p => p.Name != "items")
                    .ToDictionary(p => p.Name, p => (object)p.Value, StringComparer.Ordinal);
                if (retained < items.Length)
                {
                    var context = new Dictionary<string, object>
                    {
                    // Original hasMore describes the native query, while this
                    // context still omits complete rows. Keep both explicit.
                    ["hasMore"] = true, ["queryHasMore"] = root.GetProperty("hasMore"),
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
                        // Discovery hints contain native-returned geometry labels and
                        // result-page positions only. They are never topology selections.
                        // Request the actual row before supplying a selection to an edit.
                        var omittedRows = items.Select((item, position) => (item, position))
                            .Where(row => row.position >= retained && row.item.TryGetProperty("selection", out var selection) &&
                                selection.ValueKind == JsonValueKind.Object && row.item.TryGetProperty("geometry", out var geometry) &&
                                geometry.ValueKind == JsonValueKind.String && geometry.GetString() is
                                    ("GeneralFace" or "PlaneFace" or "CylinderFace" or "GeneralEdge" or "LineEdge" or "CircleEdge") &&
                                row.item.TryGetProperty("kind",out var kind) && kind.ValueKind == JsonValueKind.String &&
                                kind.GetString() == (geometry.GetString()!.EndsWith("Face",StringComparison.Ordinal) ? "Face" : "Edge"))
                            .GroupBy(row => row.item.GetProperty("geometry").GetString(), StringComparer.Ordinal)
                            // A small budget favors less frequent geometry kinds.
                            // Every row is still copied from this exact read, never inferred.
                            .OrderBy(group => group.Count()).ThenBy(group => group.First().position)
                            .Take(8).Select(group => group.First())
                            .ToArray();
                        var omittedReads = omittedRows.Select(row => new { geometry = row.item.GetProperty("geometry").GetString(), offset = offset + row.position, limit = 1 }).ToArray();
                        context["requestNote"] = "Read-only continuation copied from this successful query. Not executed; stale identities still reject.";
                        if (includeOmittedGeometryReads && omittedReads.Length > 0 && offset + items.Length <= 100001)
                        {
                            context["omittedGeometryReads"] = omittedReads;
                            context["requestNote"] = "Read-only continuation copied from this successful query. Not executed; stale identities still reject. Omitted geometry hints are result positions, not selections: copy the nextRequest identity and use a hinted offset/limit to inspect a desired row before authoring.";
                        }
                        if (includeCompleteSourceItems && offset + items.Length <= 100001)
                        {
                            var prefixKinds = items[..retained].Where(item => item.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetProperty("geometry").GetString()).ToHashSet(StringComparer.Ordinal);
                            var additional = omittedRows.Where(row => !prefixKinds.Contains(row.item.GetProperty("geometry").GetString()))
                                .Take(maximumAdditionalRows).Select(row => new { offset = offset + row.position, item = row.item }).ToArray();
                            if (additional.Length > 0)
                            {
                                // Copy complete original rows from this actual query.
                                // They supplement the prefix; they never select or author.
                                context["completeSourceItems"] = additional;
                                context["includedItemCount"] = retained + additional.Length;
                                context["remainingOmittedItemCount"] = items.Length - retained - additional.Length;
                                context["completeSourceItemsNote"] = "Original complete rows from this successful read. No selection or edit was executed. Offsets are query positions; includedItemCount includes prefix and extra rows. nextRequest starts after the prefix.";
                            }
                        }
                    }
                    page["contextPage"] = context;
                }
                // Put read navigation ahead of lengthy topology rows. Neither
                // query identities nor original rows are rewritten or executed.
                page["items"] = items[..retained];
                return JsonSerializer.Serialize(page);
            }
            var full = Page(items.Length);
            if (full.Length <= maximumCharacters) { compacted = full; return true; }
            var smallest = Page(0);
            if (smallest.Length > maximumCharacters) return false;
            // Prefix content alone is a lower bound on every candidate. Avoid
            // repeatedly serializing prefixes that cannot fit even without metadata.
            var prefixCharacters = 0;
            var maximumPrefix = 0;
            for (var index = 0; index < items.Length - 1; index++)
            {
                prefixCharacters += JsonSerializer.Serialize(items[index]).Length + (index > 0 ? 1 : 0);
                if (prefixCharacters > maximumCharacters) break;
                maximumPrefix = index + 1;
            }
            // Try bounded subsets of complete original kinds before discarding all
            // supplemental selectors. Navigation hints are optional; identities are not.
            if (continuation is not null)
                for (maximumAdditionalRows = Math.Min(8, items.Where(item => item.TryGetProperty("selection", out var selection) &&
                         selection.ValueKind == JsonValueKind.Object && item.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.String)
                         .Select(item => item.GetProperty("geometry").GetString()).Distinct(StringComparer.Ordinal).Count()); maximumAdditionalRows > 0; maximumAdditionalRows--)
                    foreach (var hints in new[] { true, false })
                    {
                        includeOmittedGeometryReads = hints;
                        for (var retained = maximumPrefix; retained > 0; retained--)
                        {
                            var candidate = Page(retained);
                            if (candidate.Length <= maximumCharacters) { compacted = candidate; return true; }
                        }
                    }
            includeOmittedGeometryReads = true;
            maximumAdditionalRows = 8;
            // Discovery metadata must never prevent a complete original item
            // from fitting. Prefer actual selectors over optional discovery hints.
            if (items.Length > 0 && Page(1).Length > maximumCharacters)
                includeCompleteSourceItems = false;
            if (items.Length > 0 && Page(1).Length > maximumCharacters)
                includeOmittedGeometryReads = false;
            // Optional rows/hints disappear when their kind enters the prefix,
            // so serialized sizes need not be monotonic. At most 50 candidates
            // are possible; choose the longest complete prefix that fits.
            compacted = smallest;
            for (var retained = maximumPrefix; retained > 0; retained--)
            {
                var candidate = Page(retained);
                if (candidate.Length <= maximumCharacters) { compacted = candidate; break; }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Identity(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(value.GetString(), "D", out var id) && id != Guid.Empty;
    internal static bool HasDuplicateFields(JsonElement value)
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
