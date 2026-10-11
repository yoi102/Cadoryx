using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentHistoryTools
{
    internal static readonly string Schema = BuildSchema();

    internal static async Task<string> ExecuteAsync(AiToolCall call, CadDocumentViewModel? document, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (call.ArgumentsJson.Length > 65536) throw new ArgumentException("History request exceeds its input budget.");
            using var parsed = JsonDocument.Parse(call.ArgumentsJson); var args = parsed.RootElement;
            var action = RequiredString(args, "action", 32);
            string[] fields = action switch
            {
                "set_suppressed" => ["id", "revision", "suppressed"],
                "reorder" => ["partId", "order"],
                "rollback" => ["partId", "position"],
                "return_end" => [],
                _ => throw new ArgumentException("Unknown history action.")
            };
            RequireFields(args, ["action", "documentId", "stateId", "cursor", .. fields]);
            var author = new DocumentCommandExpectation(new(RequiredGuid(args, "documentId")), new(RequiredGuid(args, "stateId")));
            if (!args.TryGetProperty("cursor", out var inputCursor)) throw new ArgumentException("Supply the inspected cursor or explicit null at the history end.");
            FeatureCursor? cursor = null;
            if (inputCursor.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    RequireFields(inputCursor, ["stateId", "partId", "position"]);
                    cursor = new(new(RequiredGuid(inputCursor, "stateId")), new(RequiredGuid(inputCursor, "partId")), Position(inputCursor));
                }
                catch(ArgumentException error)
                {
                    throw new ArgumentException("Invalid cursor object: "+error.Message+
                        " Copy cursor.stateId, cursor.partId and cursor.position from exact inspection; root partId is only rollback/reorder. Use explicit null only at the history end.",error);
                }
                if (cursor.State != author.StateId) throw new ArgumentException("Preview cursor does not belong to the requested author state.");
            }
            CadTimelineEditRequest request;
            if (action == "set_suppressed")
            {
                if (!args.TryGetProperty("suppressed", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Missing or invalid suppressed.");
                request = new(action, RequiredGuid(args, "id"), RequiredGuid(args, "revision"), flag.GetBoolean());
            }
            else if (action == "reorder")
            {
                if (!args.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Array || order.GetArrayLength() is < 1 or > 1024)
                    throw new ArgumentException("Reorder requires 1–1024 exact feature IDs.");
                var ids = order.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String &&
                    Guid.TryParseExact(item.GetString(), "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException("Invalid ordered feature identity.")).ToArray();
                request = new(action, PartId: RequiredGuid(args, "partId"), Order: ids);
            }
            else if (action == "rollback") request = new(action, PartId: RequiredGuid(args, "partId"), Position: Position(args));
            else request = new(action);
            if (document is null) throw new InvalidOperationException("No active document.");
            return await CadTimelineAuthoring.ApplyAsync(document, new(author, cursor), request, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or CadValidationException or KeyNotFoundException or StaleDocumentException)
        { return JsonSerializer.Serialize(new { success = false, message = error.Message }); }
    }

    private static int Position(JsonElement args) => args.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Number &&
        position.TryGetInt32(out var value) && value is >= 0 and <= 1000000 ? value : throw new ArgumentException("Invalid history position.");

    private static string BuildSchema()
    {
        object Ref(string name) => new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
        object Id() => Ref("id");
        object PositionField() => new { type = "integer", minimum = 0, maximum = 1000000 };
        object cursor = new { oneOf = new object[] { new { type = "null" }, new { type = "object",
            properties = new { stateId = Id(), partId = Id(), position = PositionField() }, required = new[] { "stateId", "partId", "position" }, additionalProperties = false } } };
        var actions = new[] { "set_suppressed", "reorder", "rollback", "return_end" };
        var shapes = actions.Select(action =>
        {
            var properties = new Dictionary<string, object> { ["action"] = new { @const = action }, ["documentId"] = new{}, ["stateId"] = new{}, ["cursor"] = new{} };
            var required = new List<string> { "action" };
            if (action == "set_suppressed")
            { properties["id"] = Id(); properties["revision"] = Id(); properties["suppressed"] = new { type = "boolean" }; required.AddRange(["id", "revision", "suppressed"]); }
            if (action is "reorder" or "rollback") { properties["partId"] = Id(); required.Add("partId"); }
            if (action == "reorder")
            { properties["order"] = new { type = "array", items = Id(), minItems = 1, maxItems = 1024, uniqueItems = true }; required.Add("order"); }
            if (action == "rollback") { properties["position"] = PositionField(); required.Add("position"); }
            return (object)new { type = "object", properties, required, additionalProperties = false };
        }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string,object>{["type"]="object",["properties"]=new{documentId=Id(),stateId=Id(),cursor=Ref("cursor")},["required"]=new[]{"documentId","stateId","cursor"},["oneOf"]=shapes,
            ["$defs"]=new Dictionary<string,object>{["id"]=new{type="string",format="uuid",minLength=36,maxLength=36},["cursor"]=cursor}});
    }
}
