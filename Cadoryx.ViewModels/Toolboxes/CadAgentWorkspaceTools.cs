using System.Text.Json;
using Cadoryx.AI.Contracts;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentWorkspaceTools
{
    internal const string Schema = """
        {"type":"object","properties":{"action":{"type":"string","enum":["contract","list","new","open","activate"]},"expected":{"type":"object","description":"list.expected verbatim; keep IDs/nulls; target separate.","properties":{"workspaceStateId":{"type":"string","format":"uuid"},"activeSessionId":{"type":["string","null"],"format":"uuid","description":"Null only if latest list.expected.activeSessionId is null."},"documentId":{"type":["string","null"],"format":"uuid","description":"Null only if latest list.expected.documentId is null."},"stateId":{"type":["string","null"],"format":"uuid","description":"Null only if latest list.expected.stateId is null."}},"required":["workspaceStateId","activeSessionId","documentId","stateId"],"additionalProperties":false},"name":{"type":"string","minLength":1,"maxLength":200},"path":{"type":"string","minLength":1,"maxLength":4096},"target":{"type":"object","properties":{"sessionId":{"type":"string","format":"uuid"},"documentId":{"type":"string","format":"uuid"},"stateId":{"type":"string","format":"uuid"}},"required":["sessionId","documentId","stateId"],"additionalProperties":false},"offset":{"type":"integer","minimum":0,"maximum":100000},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["action"],"additionalProperties":false}
        """;
    internal static async Task<string> ExecuteAsync(AiToolCall call, CadWorkspaceAuthoring? workspace, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (call.ArgumentsJson.Length > 8192) throw new ArgumentException("Workspace request exceeds its input budget.");
            using var parsed = JsonDocument.Parse(call.ArgumentsJson); var args = parsed.RootElement;
            var action = RequiredString(args, "action", 16);
            if (action == "contract") { RequireFields(args, ["action"]); return CadWorkspaceAuthoring.Contract(); }
            if (workspace is null) throw new NotSupportedException("Workspace authoring is unavailable in this context.");
            if (ValidateFields(args, action) is { } feedback) return feedback;
            if (action == "list") return workspace.List(args);
            return await workspace.ApplyAsync(args, CadWorkspaceAuthoring.ParseExpectation(args), true, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException e) when (e.Message == CadWorkspaceAuthoring.ExpectationChangedMessage)
        {
            // A well-formed expectation can become stale before or after an
            // asynchronous read. Keep the original rejection; supply no IDs,
            // replacement transition or automatic retry.
            return JsonSerializer.Serialize(new
            {
                success = false, message = e.Message, rejection = "workspace_expectation_changed",
                reinspection = new { toolName = "cad_workspace", arguments = new { action = "list" } },
                expectedRole = "Copy the complete current list.expected object. Its activeSessionId/documentId/stateId describe the currently active session. Do not replace them with target identities.",
                targetRole = "For activate, copy sessionId/documentId/stateId from the desired sessions[].identity row separately into target.",
                nextStep = "Read the workspace list again, then explicitly submit your intended action with its current expected and chosen target. The rejected request is not retried or repaired automatically."
            });
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return JsonSerializer.Serialize(new { success = false, message = e.Message }); }
    }

    private static string? ValidateFields(JsonElement args, string action)
    {
        string[] allowed = action switch
        {
            "list" => ["action", "offset", "limit"], "new" => ["action", "expected", "name"],
            "open" => ["action", "expected", "path"], "activate" => ["action", "expected", "target"],
            _ => throw new ArgumentException("Unknown workspace action. Read cad_workspace contract.")
        };
        var invalid = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) invalid.Add(property.Name);
        if (action != "list") Identity("expected", ["workspaceStateId", "activeSessionId", "documentId", "stateId"], true);
        if (action == "activate") Identity("target", ["sessionId", "documentId", "stateId"], false);
        if (action is "new" or "open")
        {
            var name = action == "new" ? "name" : "path"; var maximum = action == "new" ? 200 : 4096;
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { Length: > 0 } text || text.Length > maximum || string.IsNullOrWhiteSpace(text)) invalid.Add(name);
        }
        if (action == "list")
            foreach (var (name, min, max) in new[] { ("offset", 0, 100000), ("limit", 1, 50) })
                if (args.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetInt32(out var number) || number < min || number > max)) invalid.Add(name);
        if (invalid.Count == 0) return null;
        return JsonSerializer.Serialize(new
        {
            success = false, message = "Missing, invalid, unknown or duplicate workspace fields: " + string.Join(", ", invalid.Distinct()) + ".",
            invalidFields = invalid.Distinct().ToArray(), action,
            allowedFields = allowed,
            identitySource = new { toolName = "cad_workspace", arguments = new { action = "list" } },
            roles = new
            {
                expected = "Copy the complete list.expected object. It describes the current workspace and active session; explicit null is valid only for all three active identity fields in an empty workspace.",
                target = "For activate, copy one actual sessions[].identity sessionId/documentId/stateId triple. Names and row indexes are not target identities."
            },
            nextStep = "No workspace transition or file read was attempted. Read list, then explicitly retry your original action with only its allowed fields and the returned exact identities. No identity was filled in automatically."
        });

        void Identity(string name, string[] fields, bool nullableActive)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) { invalid.Add(name); return; }
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!fields.Contains(property.Name, StringComparer.Ordinal) || !members.Add(property.Name)) invalid.Add(name + "." + property.Name);
            foreach (var field in fields)
            {
                if (!value.TryGetProperty(field, out var item)) { invalid.Add(name + "." + field); continue; }
                if (nullableActive && field != "workspaceStateId" && item.ValueKind == JsonValueKind.Null) continue;
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id) || id == Guid.Empty)
                    invalid.Add(name + "." + field);
            }
            if (nullableActive && fields.Skip(1).All(field => value.TryGetProperty(field, out _)))
            {
                var nulls = fields.Skip(1).Count(field => value.GetProperty(field).ValueKind == JsonValueKind.Null);
                if (nulls is > 0 and < 3) foreach (var field in fields.Skip(1)) invalid.Add(name + "." + field);
            }
        }
    }
}
