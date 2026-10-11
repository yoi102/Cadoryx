using System.Text.Json;
using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent;

internal sealed record AgentNativeReadContinuation(string DocumentId, string StateId, string Path, string BodyId, string Revision)
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "action", "documentId", "stateId", "path", "bodyId", "revision", "offset", "limit" };

    internal static AgentNativeReadContinuation? FromActualQuery(AiToolCall? call, JsonElement receipt, JsonElement[] items)
    {
        if (call is null || call.Name != "cad_drawing" || call.ArgumentsJson.Length > 131072) return null;
        try
        {
            using var parsed = JsonDocument.Parse(call.ArgumentsJson); var args = parsed.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in args.EnumerateObject())
                if (!Fields.Contains(property.Name) || !names.Add(property.Name)) return null;
            if (!Text(args, "action", out var action) || action != "inspect_sources" ||
                !Text(args, "documentId", out var document) || !SameIdentity(document, receipt.GetProperty("documentId")) ||
                !Text(args, "stateId", out var state) || !SameIdentity(state, receipt.GetProperty("stateId")) ||
                !Text(args, "path", out var path) || path.Length > 8192 ||
                !Text(args, "bodyId", out var body) || !Identity(body) ||
                !Text(args, "revision", out var revision) || !Identity(revision)) return null;
            if (args.TryGetProperty("offset", out var offset) &&
                (!offset.TryGetInt32(out var offsetValue) || offsetValue != receipt.GetProperty("offset").GetInt32())) return null;
            if (args.TryGetProperty("limit", out var limit) &&
                (!limit.TryGetInt32(out var limitValue) || limitValue != receipt.GetProperty("limit").GetInt32())) return null;
            // Verify every actual selection that exposes its query context. No
            // coordinate, fingerprint, count or topology index supplies an ID.
            var selectionSeen = false;
            foreach (var item in items)
            {
                if (!item.TryGetProperty("selection", out var selection)) continue;
                if (selection.ValueKind != JsonValueKind.Object ||
                    !Text(selection, "path", out var sourcePath) || sourcePath != path ||
                    !selection.TryGetProperty("bodyId", out var sourceBody) || !SameIdentity(body, sourceBody) ||
                    !selection.TryGetProperty("revision", out var sourceRevision) || !SameIdentity(revision, sourceRevision)) return null;
                selectionSeen = true;
            }
            return selectionSeen ? new(document, state, path, body, revision) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    internal object NextRequest(int nextOffset, int originalLimit, int retainedItems) => new
    {
        action = "inspect_sources", documentId = DocumentId, stateId = StateId, path = Path,
        bodyId = BodyId, revision = Revision, offset = nextOffset,
        limit = Math.Min(originalLimit, Math.Max(1, retainedItems))
    };
    private static bool Identity(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool SameIdentity(string value, JsonElement other) => Identity(value) && other.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(other.GetString(), "D", out var id) && Guid.Parse(value) == id;
    private static bool Text(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!; return !string.IsNullOrWhiteSpace(value);
    }
}
