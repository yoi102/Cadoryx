using System.Text.Json;
using Cadoryx.AI.Contracts;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentWorkspaceTools
{
    internal const string Schema = """
        {"type":"object","properties":{"action":{"type":"string","enum":["contract","list","new","open","activate"]},"expected":{"type":"object","properties":{"workspaceStateId":{"type":"string","format":"uuid"},"activeSessionId":{"type":["string","null"],"format":"uuid"},"documentId":{"type":["string","null"],"format":"uuid"},"stateId":{"type":["string","null"],"format":"uuid"}},"required":["workspaceStateId","activeSessionId","documentId","stateId"],"additionalProperties":false},"name":{"type":"string","minLength":1,"maxLength":200},"path":{"type":"string","minLength":1,"maxLength":4096},"target":{"type":"object","properties":{"sessionId":{"type":"string","format":"uuid"},"documentId":{"type":"string","format":"uuid"},"stateId":{"type":"string","format":"uuid"}},"required":["sessionId","documentId","stateId"],"additionalProperties":false},"offset":{"type":"integer","minimum":0,"maximum":100000},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["action"],"additionalProperties":false}
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
            if (action == "list") return workspace.List(args);
            return await workspace.ApplyAsync(args, CadWorkspaceAuthoring.ParseExpectation(args), true, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return JsonSerializer.Serialize(new { success = false, message = e.Message }); }
    }
}
